using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.DependencyInjection;

using Vigia.Comprobaciones.Red;

namespace Vigia.Comprobaciones.Http;

/// <summary>Los tiempos de las fases de una petición que se pueden medir por separado.</summary>
public sealed class MedidasDeConexion
{
    public TimeSpan? Dns { get; set; }

    public TimeSpan? Conexion { get; set; }

    /// <summary>Desde que se envía la petición hasta que llegan las cabeceras de la respuesta (incluye la negociación TLS).</summary>
    public TimeSpan PrimerByte { get; set; }
}

/// <summary>
/// Los clientes HTTP de las comprobaciones. La defensa contra SSRF está en cómo se conectan:
/// <list type="bullet">
/// <item>La conexión no la abre .NET a partir del nombre: la abre <see cref="ConectarAsync"/>, que resuelve
/// el nombre <b>una vez</b>, valida las direcciones y conecta a esa dirección concreta (sin volver a
/// resolver: así el DNS no puede cambiar de respuesta entre la comprobación y el uso).</item>
/// <item>Se prohíbe el proxy del sistema, que conectaría al destino por su cuenta sin pasar por la guardia.</item>
/// <item>Las redirecciones no las sigue el cliente: las sigue el comprobador, y cada salto abre una
/// conexión nueva que pasa otra vez por la guardia.</item>
/// </list>
/// Hay un cliente por cada combinación de «permitir red privada» y «verificar certificado».
/// </summary>
public static class ClientesHttp
{
    public static readonly HttpRequestOptionsKey<MedidasDeConexion> ClaveMedidas = new("vigia.medidas");

    public static string Nombre(bool permitirRedPrivada, bool verificarCertificado) =>
        $"vigia-{(permitirRedPrivada ? "privada" : "publica")}-{(verificarCertificado ? "verificado" : "sinverificar")}";

    public static IServiceCollection AddClientesHttp(this IServiceCollection servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        foreach (var permitirRedPrivada in new[] { false, true })
        {
            foreach (var verificarCertificado in new[] { true, false })
            {
                servicios
                    .AddHttpClient(Nombre(permitirRedPrivada, verificarCertificado))
                    .ConfigureHttpClient(cliente => cliente.Timeout = Timeout.InfiniteTimeSpan) // el tiempo máximo lo manda el comprobador
                    .ConfigurePrimaryHttpMessageHandler(proveedor => CrearManejador(proveedor, permitirRedPrivada, verificarCertificado))
                    .RemoveAllLoggers();
            }
        }

        return servicios;
    }

    private static SocketsHttpHandler CrearManejador(IServiceProvider proveedor, bool permitirRedPrivada, bool verificarCertificado)
    {
        var guardia = proveedor.GetRequiredService<GuardiaDeDestinos>();
        var conector = proveedor.GetRequiredService<IConector>();
        var reloj = proveedor.GetRequiredService<TimeProvider>();

        var manejador = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,

            // Las conexiones se reciclan para que un cambio de DNS del servicio vigilado se note.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (contexto, cancellationToken) => ConectarAsync(contexto, guardia, conector, reloj, permitirRedPrivada, cancellationToken),
        };

        if (!verificarCertificado)
        {
            // Solo para monitores que lo piden de forma explícita (servicios internos con certificado propio).
            // Un servicio público sigue con la validación normal.
#pragma warning disable CA5359
            manejador.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        return manejador;
    }

    private static async ValueTask<Stream> ConectarAsync(
        SocketsHttpConnectionContext contexto,
        GuardiaDeDestinos guardia,
        IConector conector,
        TimeProvider reloj,
        bool permitirRedPrivada,
        CancellationToken cancellationToken)
    {
        MedidasDeConexion? medidasDePeticion = null;
        contexto.InitialRequestMessage?.Options.TryGetValue(ClaveMedidas, out medidasDePeticion);

        var inicioDns = reloj.GetTimestamp();
        var direcciones = await guardia.ResolverPermitidasAsync(contexto.DnsEndPoint.Host, permitirRedPrivada, cancellationToken);
        if (medidasDePeticion is not null)
        {
            medidasDePeticion.Dns = reloj.GetElapsedTime(inicioDns);
        }

        var inicioConexion = reloj.GetTimestamp();
        Exception? ultimoError = null;

        foreach (var direccion in direcciones)
        {
            try
            {
                var socket = await conector.ConectarAsync(direccion, contexto.DnsEndPoint.Port, cancellationToken);
                if (medidasDePeticion is not null)
                {
                    medidasDePeticion.Conexion = reloj.GetElapsedTime(inicioConexion);
                }

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException excepcion)
            {
                ultimoError = excepcion;
            }
        }

        throw ultimoError ?? new SocketException((int)SocketError.HostNotFound);
    }
}
