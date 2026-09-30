using System.Net;
using System.Net.Sockets;

namespace Vigia.Comprobaciones.Red;

/// <summary>
/// Las direcciones a las que una comprobación no puede conectar salvo permiso explícito: todo lo que
/// no es internet público. Vigía hace peticiones a destinos que escribe una persona; sin esta lista,
/// «vigila http://169.254.169.254/» leería las credenciales de la máquina en una nube, y
/// «vigila http://localhost:5432» exploraría los servicios internos del servidor.
/// </summary>
public static class RangosBloqueados
{
    private sealed record Rango(byte[] Base, int Prefijo, string Motivo);

    private static readonly byte[] BaseNat64 = IPAddress.Parse("64:ff9b::").GetAddressBytes();

    private static readonly Rango[] Rangos =
    [
        // IPv4
        V4("0.0.0.0", 8, "esta red (0.0.0.0/8)"),
        V4("10.0.0.0", 8, "red privada (10.0.0.0/8)"),
        V4("100.64.0.0", 10, "espacio compartido de operador, donde algunas nubes ponen sus metadatos (100.64.0.0/10)"),
        V4("127.0.0.0", 8, "loopback (127.0.0.0/8)"),
        V4("168.63.129.16", 32, "servicio interno de la plataforma Azure (168.63.129.16)"),
        V4("169.254.0.0", 16, "enlace local y metadatos de la nube (169.254.0.0/16)"),
        V4("172.16.0.0", 12, "red privada (172.16.0.0/12)"),
        V4("192.0.0.0", 24, "protocolo IETF, incluye los metadatos de Oracle Cloud (192.0.0.0/24)"),
        V4("192.0.2.0", 24, "documentación (192.0.2.0/24)"),
        V4("192.88.99.0", 24, "relé 6to4 (192.88.99.0/24)"),
        V4("192.168.0.0", 16, "red privada (192.168.0.0/16)"),
        V4("198.18.0.0", 15, "pruebas de rendimiento (198.18.0.0/15)"),
        V4("198.51.100.0", 24, "documentación (198.51.100.0/24)"),
        V4("203.0.113.0", 24, "documentación (203.0.113.0/24)"),
        V4("224.0.0.0", 4, "multidifusión (224.0.0.0/4)"),
        V4("240.0.0.0", 4, "reservada, incluye el broadcast (240.0.0.0/4)"),

        // IPv6
        V6("::", 8, "sin especificar, loopback o compatible con IPv4 (::/8)"),
        V6("100::", 64, "descartar (100::/64)"),
        V6("2001::", 32, "Teredo (2001::/32)"),
        V6("2001:db8::", 32, "documentación (2001:db8::/32)"),
        V6("64:ff9b:1::", 48, "NAT64 de uso local (64:ff9b:1::/48)"),
        V6("fc00::", 7, "red privada, incluye los metadatos de AWS (fc00::/7)"),
        V6("fe80::", 10, "enlace local (fe80::/10)"),
        V6("fec0::", 10, "sitio local, en desuso (fec0::/10)"),
        V6("ff00::", 8, "multidifusión (ff00::/8)"),
    ];

    /// <summary>¿Se puede conectar a esta dirección sin permiso especial?</summary>
    public static bool EstaBloqueada(IPAddress direccion) => Motivo(direccion) is not null;

    /// <summary>Por qué está bloqueada la dirección, o <c>null</c> si es una dirección pública.</summary>
    public static string? Motivo(IPAddress direccion)
    {
        ArgumentNullException.ThrowIfNull(direccion);

        // Una dirección IPv6 puede llevar dentro una IPv4 (::ffff:127.0.0.1, NAT64, 6to4). El truco
        // clásico es escribir así una dirección interna para saltarse una lista que solo mira IPv4:
        // se desempaqueta y se comprueba la dirección IPv4 de dentro.
        if (Incrustada(direccion) is { } interna)
        {
            return Motivo(interna) is { } motivo ? $"{motivo}, escrita dentro de una dirección IPv6" : null;
        }

        var bytes = direccion.GetAddressBytes();

        foreach (var rango in Rangos)
        {
            if (rango.Base.Length == bytes.Length && Coincide(bytes, rango.Base, rango.Prefijo))
            {
                return rango.Motivo;
            }
        }

        return null;
    }

    private static IPAddress? Incrustada(IPAddress direccion)
    {
        if (direccion.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }

        if (direccion.IsIPv4MappedToIPv6)
        {
            return direccion.MapToIPv4();
        }

        var b = direccion.GetAddressBytes();

        // NAT64 (64:ff9b::/96): la IPv4 son los últimos 4 bytes.
        if (Coincide(b, BaseNat64, 96))
        {
            return new IPAddress(b[12..16]);
        }

        // 6to4 (2002::/16): la IPv4 son los bytes 2 a 5.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return new IPAddress(b[2..6]);
        }

        return null;
    }

    private static bool Coincide(byte[] direccion, byte[] red, int prefijo)
    {
        var bytesCompletos = prefijo / 8;

        for (var i = 0; i < bytesCompletos; i++)
        {
            if (direccion[i] != red[i])
            {
                return false;
            }
        }

        var bitsRestantes = prefijo % 8;

        if (bitsRestantes == 0)
        {
            return true;
        }

        var mascara = (byte)(0xFF << (8 - bitsRestantes));

        return (direccion[bytesCompletos] & mascara) == (red[bytesCompletos] & mascara);
    }

    private static Rango V4(string direccion, int prefijo, string motivo) => new(IPAddress.Parse(direccion).GetAddressBytes(), prefijo, motivo);

    private static Rango V6(string direccion, int prefijo, string motivo) => new(IPAddress.Parse(direccion).GetAddressBytes(), prefijo, motivo);
}
