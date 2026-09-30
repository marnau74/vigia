using System.Net;
using System.Text.RegularExpressions;

using Vigia.Dominio.Comun;

namespace Vigia.Dominio.Monitores;

/// <summary>
/// Las reglas de cada tipo de configuración. Se comprueban al guardar el monitor, para que un dato
/// imposible (un puerto 99999, una URL sin esquema) se rechace con un mensaje claro y no llegue
/// nunca al planificador. La protección contra destinos peligrosos (SSRF) NO está aquí: depende de
/// a qué resuelva un nombre en el momento de conectar, y eso solo se sabe al comprobar.
/// </summary>
public static partial class ValidacionDeConfiguracion
{
    public static readonly ErrorDominio CambioDeTipo = ErroresMonitor.CambioDeTipo;

    public static Resultado Validar(ConfiguracionMonitor configuracion)
    {
        ArgumentNullException.ThrowIfNull(configuracion);

        return configuracion switch
        {
            ConfiguracionHttp http => ValidarHttp(http),
            ConfiguracionTls tls => ValidarTls(tls),
            ConfiguracionDns dns => ValidarDns(dns),
            ConfiguracionTcp tcp => ValidarTcp(tcp),
            ConfiguracionIcmp icmp => ValidarIcmp(icmp),
            _ => Invalida($"Tipo de monitor desconocido: {configuracion.GetType().Name}."),
        };
    }

    /// <summary>Un nombre de equipo válido o una dirección IP. Sin puerto, ni esquema, ni ruta, ni espacios.</summary>
    public static bool EsHostValido(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
        {
            return false;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out _) || PatronHost().IsMatch(host);
    }

    private static Resultado ValidarHttp(ConfiguracionHttp http)
    {
        if (http.Url is null || !http.Url.IsAbsoluteUri || http.Url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(http.Url.Host))
        {
            return Invalida("La dirección debe ser una URL completa que empiece por http:// o https://.");
        }

        if (!string.IsNullOrEmpty(http.Url.UserInfo))
        {
            return Invalida("La dirección no puede llevar usuario ni contraseña.");
        }

        if (http.Url.OriginalString.Length > 2000)
        {
            return Invalida("La dirección es demasiado larga (máximo 2000 caracteres).");
        }

        if (http.Metodo is not ("GET" or "HEAD"))
        {
            return Invalida("El método debe ser GET o HEAD.");
        }

        if (http.MaxRedirecciones is < 0 or > 10)
        {
            return Invalida("Las redirecciones seguidas deben estar entre 0 y 10.");
        }

        if (http.PalabraClave is { } palabra && (palabra.Trim().Length == 0 || palabra.Length > 200))
        {
            return Invalida("La palabra clave debe tener entre 1 y 200 caracteres.");
        }

        return Resultado.Exito();
    }

    private static Resultado ValidarTls(ConfiguracionTls tls) =>
        !EsHostValido(tls.Host)
            ? Invalida("El equipo no es un nombre ni una dirección IP válidos.")
            : PuertoValido(tls.Puerto);

    private static Resultado ValidarTcp(ConfiguracionTcp tcp) =>
        !EsHostValido(tcp.Host)
            ? Invalida("El equipo no es un nombre ni una dirección IP válidos.")
            : PuertoValido(tcp.Puerto);

    private static Resultado ValidarIcmp(ConfiguracionIcmp icmp) =>
        EsHostValido(icmp.Host) ? Resultado.Exito() : Invalida("El equipo no es un nombre ni una dirección IP válidos.");

    private static Resultado ValidarDns(ConfiguracionDns dns)
    {
        if (!EsHostValido(dns.Nombre) || IPAddress.TryParse(dns.Nombre, out _))
        {
            return Invalida("El nombre a resolver debe ser un nombre de dominio.");
        }

        if (!Enum.IsDefined(dns.Registro))
        {
            return Invalida("El tipo de registro no es válido.");
        }

        if (dns.Servidor is { Length: > 0 } servidor && !IPAddress.TryParse(servidor, out _) && !IPEndPoint.TryParse(servidor, out _))
        {
            return Invalida("El servidor DNS debe ser una dirección IP, con puerto opcional.");
        }

        if (dns.Esperados is { } esperados && (esperados.Count > 50 || esperados.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 500)))
        {
            return Invalida("Los registros esperados no pueden estar vacíos ni ser más de 50.");
        }

        return Resultado.Exito();
    }

    private static Resultado PuertoValido(int puerto) =>
        puerto is >= 1 and <= 65535 ? Resultado.Exito() : Invalida("El puerto debe estar entre 1 y 65535.");

    private static Resultado Invalida(string mensaje) =>
        Resultado.Fallo(new ErrorDominio("monitor.configuracion_invalida", mensaje));

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PatronHost();
}
