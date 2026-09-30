using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vigia.Comprobaciones.Tests.Apoyo;

/// <summary>Un servidor web real (Kestrel) en 127.0.0.1, con las rutas que cada test necesite.</summary>
public sealed class ServidorWeb : IAsyncDisposable
{
    private readonly WebApplication _aplicacion;

    private ServidorWeb(WebApplication aplicacion, int puerto)
    {
        _aplicacion = aplicacion;
        Puerto = puerto;
    }

    public int Puerto { get; }

    public static async Task<ServidorWeb> IniciarAsync(Action<WebApplication> rutas, X509Certificate2? certificado = null)
    {
        var constructor = WebApplication.CreateSlimBuilder();
        constructor.Logging.ClearProviders();
        constructor.WebHost.ConfigureKestrel(opciones => opciones.Listen(IPAddress.Loopback, 0, listen =>
        {
            if (certificado is not null)
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificado);
            }
        }));

        var aplicacion = constructor.Build();
        rutas(aplicacion);
        await aplicacion.StartAsync();

        var direccion = aplicacion.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        return new ServidorWeb(aplicacion, new Uri(direccion).Port);
    }

    public async ValueTask DisposeAsync()
    {
        await _aplicacion.StopAsync();
        await _aplicacion.DisposeAsync();
    }
}

/// <summary>Un servidor TCP que acepta conexiones y no dice nada (para probar tiempos agotados en el saludo).</summary>
public sealed class ServidorTcpMudo : IDisposable
{
    private readonly TcpListener _escucha = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _clientes = [];
    private readonly CancellationTokenSource _parada = new();

    public ServidorTcpMudo()
    {
        _escucha.Start();
        _ = AceptarAsync();
    }

    public int Puerto => ((IPEndPoint)_escucha.LocalEndpoint).Port;

    private async Task AceptarAsync()
    {
        try
        {
            while (!_parada.IsCancellationRequested)
            {
                var cliente = await _escucha.AcceptTcpClientAsync(_parada.Token);
                lock (_clientes)
                {
                    _clientes.Add(cliente);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fin del test.
        }
        catch (ObjectDisposedException)
        {
            // Fin del test.
        }
    }

    public void Dispose()
    {
        _parada.Cancel();
        _escucha.Stop();
        lock (_clientes)
        {
            foreach (var cliente in _clientes)
            {
                cliente.Dispose();
            }
        }

        _parada.Dispose();
    }
}

/// <summary>Un puerto en el que seguro no escucha nadie.</summary>
public static class PuertoCerrado
{
    public static int Nuevo()
    {
        var escucha = new TcpListener(IPAddress.Loopback, 0);
        escucha.Start();
        var puerto = ((IPEndPoint)escucha.LocalEndpoint).Port;
        escucha.Stop();

        return puerto;
    }
}

public static class Certificados
{
    /// <summary>Un certificado autofirmado para <paramref name="nombre"/>, válido en el intervalo dado.</summary>
    public static X509Certificate2 Autofirmado(string nombre, DateTimeOffset desde, DateTimeOffset hasta)
    {
        using var clave = RSA.Create(2048);
        var peticion = new CertificateRequest($"CN={nombre}", clave, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var alternativos = new SubjectAlternativeNameBuilder();
        alternativos.AddDnsName(nombre);
        peticion.CertificateExtensions.Add(alternativos.Build());
        peticion.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using var certificado = peticion.CreateSelfSigned(desde, hasta);

        // Se exporta y se vuelve a cargar para que Kestrel y SslStream puedan usar la clave privada.
        return X509CertificateLoader.LoadPkcs12(certificado.Export(X509ContentType.Pfx), null);
    }
}

/// <summary>
/// Un servidor DNS mínimo por UDP en 127.0.0.1, con las respuestas que cada test decida. Entiende
/// justo lo necesario: una pregunta por consulta, y respuestas A, AAAA, CNAME, MX y TXT.
/// </summary>
public sealed class ServidorDns : IDisposable
{
    public const int A = 1;
    public const int Cname = 5;
    public const int Mx = 15;
    public const int Txt = 16;
    public const int Aaaa = 28;

    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _parada = new();
    private readonly Func<string, int, RespuestaDns?> _manejador;

    public ServidorDns(Func<string, int, RespuestaDns?> manejador)
    {
        _manejador = manejador;
        _ = EscucharAsync();
    }

    public int Puerto => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public string Direccion => $"127.0.0.1:{Puerto}";

    public int Consultas { get; private set; }

    private async Task EscucharAsync()
    {
        try
        {
            while (!_parada.IsCancellationRequested)
            {
                var recibido = await _udp.ReceiveAsync(_parada.Token);
                Consultas++;

                var (nombre, tipo, finPregunta) = LeerPregunta(recibido.Buffer);
                var respuesta = _manejador(nombre, tipo);

                // Sin respuesta: el servidor «no contesta», para probar tiempos agotados.
                if (respuesta is not null)
                {
                    var bytes = Construir(recibido.Buffer, finPregunta, tipo, respuesta);
                    await _udp.SendAsync(bytes, recibido.RemoteEndPoint, _parada.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fin del test.
        }
        catch (ObjectDisposedException)
        {
            // Fin del test.
        }
    }

    private static (string Nombre, int Tipo, int Fin) LeerPregunta(byte[] datos)
    {
        var etiquetas = new List<string>();
        var posicion = 12;

        while (datos[posicion] != 0)
        {
            var longitud = datos[posicion];
            etiquetas.Add(Encoding.ASCII.GetString(datos, posicion + 1, longitud));
            posicion += longitud + 1;
        }

        posicion++;
        var tipo = (datos[posicion] << 8) | datos[posicion + 1];

        return (string.Join('.', etiquetas), tipo, posicion + 4);
    }

    private static byte[] Construir(byte[] consulta, int finPregunta, int tipo, RespuestaDns respuesta)
    {
        using var salida = new MemoryStream();

        salida.Write(consulta, 0, 2); // mismo identificador
        salida.WriteByte(0x81); // respuesta, recursión deseada
        salida.WriteByte((byte)(0x80 | respuesta.Codigo)); // recursión disponible + código de respuesta
        salida.Write([0, 1, 0, (byte)respuesta.Registros.Count, 0, 0, 0, 0]);
        salida.Write(consulta, 12, finPregunta - 12); // la pregunta, tal cual

        foreach (var registro in respuesta.Registros)
        {
            salida.Write([0xC0, 0x0C]); // el nombre, por referencia a la pregunta
            salida.Write([(byte)(registro.Tipo >> 8), (byte)registro.Tipo, 0, 1, 0, 0, 0, 60, (byte)(registro.Datos.Length >> 8), (byte)registro.Datos.Length]);
            salida.Write(registro.Datos);
        }

        _ = tipo;

        return salida.ToArray();
    }

    public void Dispose()
    {
        _parada.Cancel();
        _udp.Dispose();
        _parada.Dispose();
    }
}

public sealed record RegistroDns(int Tipo, byte[] Datos)
{
    public static RegistroDns DeA(string ip) => new(ServidorDns.A, IPAddress.Parse(ip).GetAddressBytes());

    public static RegistroDns DeAaaa(string ip) => new(ServidorDns.Aaaa, IPAddress.Parse(ip).GetAddressBytes());

    public static RegistroDns DeCname(string destino) => new(ServidorDns.Cname, Nombre(destino));

    public static RegistroDns DeMx(int preferencia, string servidor) => new(ServidorDns.Mx, [(byte)(preferencia >> 8), (byte)preferencia, .. Nombre(servidor)]);

    public static RegistroDns DeTxt(string texto)
    {
        var bytes = Encoding.ASCII.GetBytes(texto);
        return new RegistroDns(ServidorDns.Txt, [(byte)bytes.Length, .. bytes]);
    }

    private static byte[] Nombre(string nombre)
    {
        var salida = new List<byte>();

        foreach (var etiqueta in nombre.TrimEnd('.').Split('.'))
        {
            salida.Add((byte)etiqueta.Length);
            salida.AddRange(Encoding.ASCII.GetBytes(etiqueta));
        }

        salida.Add(0);

        return [.. salida];
    }
}

/// <param name="Codigo">0 = sin error, 2 = fallo del servidor, 3 = el nombre no existe.</param>
public sealed record RespuestaDns(int Codigo, IReadOnlyList<RegistroDns> Registros)
{
    public static RespuestaDns Con(params RegistroDns[] registros) => new(0, registros);

    public static RespuestaDns NoExiste { get; } = new(3, []);

    public static RespuestaDns FalloDelServidor { get; } = new(2, []);

    public static RespuestaDns SinRegistros { get; } = new(0, []);
}
