namespace Vigia.Dominio.Monitores;

/// <summary>
/// Lo que hay que saber para hacer una comprobación de un tipo concreto. Cada tipo de monitor tiene
/// su propia configuración; añadir un tipo nuevo es añadir una configuración y un comprobador, sin
/// tocar el planificador.
/// </summary>
public abstract record ConfiguracionMonitor
{
    /// <summary>Tiempo máximo de toda la comprobación. Pasado este tiempo, cuenta como fallo.</summary>
    public TimeSpan TiempoMaximo { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Permite que el destino sea una dirección privada, de loopback o de enlace local. Por defecto
    /// está prohibido: una herramienta que hace peticiones a direcciones escritas por una persona
    /// puede usarse para llegar a servicios internos (SSRF). Solo se activa de forma explícita,
    /// por ejemplo para vigilar la red de casa.
    /// </summary>
    public bool PermitirRedPrivada { get; init; }

    public abstract TipoMonitor Tipo { get; }
}

/// <param name="Url">Solo http o https, sin usuario ni contraseña en la dirección.</param>
public sealed record ConfiguracionHttp(Uri Url) : ConfiguracionMonitor
{
    public override TipoMonitor Tipo => TipoMonitor.Http;

    /// <summary>GET, o HEAD para no descargar el cuerpo cuando no se busca ninguna palabra.</summary>
    public string Metodo { get; init; } = "GET";

    public CodigosHttpEsperados CodigosEsperados { get; init; } = CodigosHttpEsperados.PorDefecto;

    /// <summary>Si se indica, la respuesta debe contenerla (sin distinguir mayúsculas). Se busca en el primer megabyte.</summary>
    public string? PalabraClave { get; init; }

    /// <summary>Cuántas redirecciones se siguen. Cada una se valida con las mismas reglas que la primera petición.</summary>
    public int MaxRedirecciones { get; init; } = 5;

    /// <summary>Si es <c>false</c>, se acepta un certificado caducado o autofirmado (servicios internos).</summary>
    public bool VerificarCertificado { get; init; } = true;
}

public sealed record ConfiguracionTls(string Host) : ConfiguracionMonitor
{
    public override TipoMonitor Tipo => TipoMonitor.Tls;

    public int Puerto { get; init; } = 443;

    /// <summary>Si es <c>false</c>, se lee el certificado aunque no sea de confianza (autofirmado), sin darlo por fallo.</summary>
    public bool VerificarCertificado { get; init; } = true;
}

public enum TipoRegistroDns
{
    A = 1,
    Aaaa = 2,
    Cname = 3,
    Mx = 4,
    Txt = 5,
}

/// <param name="Nombre">El nombre que se resuelve (por ejemplo, <c>www.example.com</c>).</param>
/// <param name="Registro">El tipo de registro que se pide.</param>
public sealed record ConfiguracionDns(string Nombre, TipoRegistroDns Registro) : ConfiguracionMonitor
{
    public override TipoMonitor Tipo => TipoMonitor.Dns;

    /// <summary>
    /// Si se indica, la respuesta debe ser exactamente esta (en cualquier orden). Sin ella, basta con
    /// que el nombre se resuelva. El cambio respecto a la última respuesta lo detecta el worker.
    /// </summary>
    public IReadOnlyList<string>? Esperados { get; init; }

    /// <summary>Servidor DNS al que preguntar (una dirección IP). Sin él, el del sistema.</summary>
    public string? Servidor { get; init; }
}

public sealed record ConfiguracionTcp(string Host, int Puerto) : ConfiguracionMonitor
{
    public override TipoMonitor Tipo => TipoMonitor.Tcp;
}

public sealed record ConfiguracionIcmp(string Host) : ConfiguracionMonitor
{
    public override TipoMonitor Tipo => TipoMonitor.Icmp;
}
