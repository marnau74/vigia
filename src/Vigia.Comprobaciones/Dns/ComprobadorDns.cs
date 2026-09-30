using System.Globalization;
using System.Net;

using DnsClient;
using DnsClient.Protocol;

using Vigia.Comprobaciones.Red;
using Vigia.Dominio.Monitores;

namespace Vigia.Comprobaciones.Dns;

/// <summary>
/// Un nombre se resuelve y, si se indica, a lo esperado. Habla el protocolo DNS directamente con
/// DnsClient en lugar de usar la resolución del sistema: así se puede preguntar a un servidor
/// concreto, ver los registros tal cual son (CNAME, MX, TXT) y medir cuánto tarda de verdad.
/// </summary>
/// <remarks>
/// El aviso «la respuesta ha cambiado» necesita recordar la respuesta anterior, y eso lo hace el
/// worker: este comprobador devuelve los registros ordenados en <c>registros</c> para que comparar
/// dos resultados sea comparar dos textos.
/// </remarks>
public sealed class ComprobadorDns(GuardiaDeDestinos guardia, TimeProvider reloj) : Comprobador<ConfiguracionDns>(reloj)
{
    public override TipoMonitor Tipo => TipoMonitor.Dns;

    protected override async Task<ResultadoComprobacion> EjecutarAsync(ConfiguracionDns configuracion, Cronometro cronometro, CancellationToken cancellationToken)
    {
        var cliente = await CrearClienteAsync(configuracion, cancellationToken);

        IDnsQueryResponse respuesta;

        try
        {
            respuesta = await cliente.QueryAsync(configuracion.Nombre, Consulta(configuracion.Registro), QueryClass.IN, cancellationToken);
        }
        catch (DnsResponseException excepcion)
        {
            // DnsClient envuelve la cancelación en su propia excepción: si nos cancelaron (o se acabó el
            // tiempo máximo), se deja pasar para que el comprobador lo trate como tal.
            cancellationToken.ThrowIfCancellationRequested();

            return excepcion.Code == DnsResponseCode.ConnectionTimeout
                ? ResultadoComprobacion.Fallido(TipoFallo.TiempoAgotado, "El servidor DNS no respondió a tiempo.", cronometro.Transcurrido)
                : ResultadoComprobacion.Fallido(TipoFallo.ResolucionDns, $"El servidor DNS no respondió correctamente: {excepcion.Message}", cronometro.Transcurrido);
        }

        var servidor = respuesta.NameServer.Address.ToString();

        if (respuesta.HasError)
        {
            var motivo = respuesta.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain
                ? "el nombre no existe (NXDOMAIN)"
                : respuesta.ErrorMessage;

            return ResultadoComprobacion.Fallido(TipoFallo.ResolucionDns, $"No se resolvió «{configuracion.Nombre}»: {motivo}.", cronometro.Transcurrido, new Dictionary<string, string> { ["servidor"] = servidor });
        }

        var registros = Registros(respuesta, configuracion.Registro);

        var detalles = new Dictionary<string, string>
        {
            ["registros"] = string.Join("; ", registros),
            ["servidor"] = servidor,
        };

        if (registros.Count == 0)
        {
            return ResultadoComprobacion.Fallido(TipoFallo.ResolucionDns, $"«{configuracion.Nombre}» no tiene registros {configuracion.Registro.ToString().ToUpperInvariant()}.", cronometro.Transcurrido, detalles);
        }

        if (configuracion.Esperados is { Count: > 0 } esperados)
        {
            var esperadosNormalizados = esperados.Select(Normalizar).OrderBy(r => r, StringComparer.Ordinal).ToList();

            if (!esperadosNormalizados.SequenceEqual(registros, StringComparer.Ordinal))
            {
                return ResultadoComprobacion.Fallido(
                    TipoFallo.RegistroInesperado,
                    $"Se esperaba «{string.Join("; ", esperadosNormalizados)}» y se recibió «{string.Join("; ", registros)}».",
                    cronometro.Transcurrido,
                    detalles);
            }
        }

        return ResultadoComprobacion.Exito(cronometro.Transcurrido, detalles);
    }

    private async Task<LookupClient> CrearClienteAsync(ConfiguracionDns configuracion, CancellationToken cancellationToken)
    {
        // Un servidor DNS escrito por una persona es un destino más: pasa por la misma guardia que cualquier otro.
        // Sin servidor se usan los del sistema (configuración de confianza).
        var opciones = configuracion.Servidor is { Length: > 0 } texto
            ? new LookupClientOptions(await ServidorAsync(texto, configuracion.PermitirRedPrivada, cancellationToken))
            : new LookupClientOptions();

        opciones.UseCache = false;
        opciones.Retries = 0;
        opciones.ThrowDnsErrors = false;
        opciones.Timeout = configuracion.TiempoMaximo;

        return new LookupClient(opciones);
    }

    private async Task<IPEndPoint> ServidorAsync(string texto, bool permitirRedPrivada, CancellationToken cancellationToken)
    {
        if (!IPEndPoint.TryParse(texto, out var punto))
        {
            if (!IPAddress.TryParse(texto, out var direccion))
            {
                throw new NoSeResuelveException($"El servidor DNS «{texto}» debe ser una dirección IP (con puerto opcional).");
            }

            punto = new IPEndPoint(direccion, 53);
        }
        else if (punto.Port == 0)
        {
            punto = new IPEndPoint(punto.Address, 53);
        }

        await guardia.ResolverPermitidasAsync(punto.Address.ToString(), permitirRedPrivada, cancellationToken);

        return punto;
    }

    private static QueryType Consulta(TipoRegistroDns registro) => registro switch
    {
        TipoRegistroDns.A => QueryType.A,
        TipoRegistroDns.Aaaa => QueryType.AAAA,
        TipoRegistroDns.Cname => QueryType.CNAME,
        TipoRegistroDns.Mx => QueryType.MX,
        TipoRegistroDns.Txt => QueryType.TXT,
        _ => throw new ArgumentOutOfRangeException(nameof(registro)),
    };

    /// <summary>Los registros del tipo pedido, como texto normalizado y ordenado (una consulta A a un CNAME devuelve también el CNAME).</summary>
    private static List<string> Registros(IDnsQueryResponse respuesta, TipoRegistroDns tipo)
    {
        var textos = respuesta.Answers.Select(r => Texto(r, tipo)).OfType<string>().Select(Normalizar);

        return [.. textos.OrderBy(t => t, StringComparer.Ordinal)];
    }

    private static string? Texto(DnsResourceRecord registro, TipoRegistroDns tipo) => (tipo, registro) switch
    {
        (TipoRegistroDns.A, ARecord a) => a.Address.ToString(),
        (TipoRegistroDns.Aaaa, AaaaRecord aaaa) => aaaa.Address.ToString(),
        (TipoRegistroDns.Cname, CNameRecord c) => c.CanonicalName.Value,
        (TipoRegistroDns.Mx, MxRecord mx) => $"{mx.Preference.ToString(CultureInfo.InvariantCulture)} {mx.Exchange.Value}",
        (TipoRegistroDns.Txt, TxtRecord txt) => string.Concat(txt.Text),
        _ => null,
    };

    /// <summary>Minúsculas y sin el punto final: <c>Mail.Example.com.</c> y <c>mail.example.com</c> son lo mismo.</summary>
    private static string Normalizar(string texto) => texto.Trim().TrimEnd('.').ToLowerInvariant();
}
