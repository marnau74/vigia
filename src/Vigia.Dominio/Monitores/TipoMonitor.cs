namespace Vigia.Dominio.Monitores;

/// <summary>Qué se comprueba de un servicio.</summary>
public enum TipoMonitor
{
    /// <summary>Una URL responde con el código esperado (y, si se pide, con cierta palabra) dentro del tiempo máximo.</summary>
    Http = 1,

    /// <summary>El certificado TLS de un servidor es válido y le quedan días.</summary>
    Tls = 2,

    /// <summary>Un nombre se resuelve, y a lo esperado.</summary>
    Dns = 3,

    /// <summary>Un puerto acepta conexiones.</summary>
    Tcp = 4,

    /// <summary>Un equipo responde al ping.</summary>
    Icmp = 5,
}
