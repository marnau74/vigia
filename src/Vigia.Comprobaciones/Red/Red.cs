using System.Net;
using System.Net.Sockets;

namespace Vigia.Comprobaciones.Red;

/// <summary>El destino de la comprobación es una dirección a la que no se puede conectar sin permiso (SSRF).</summary>
public sealed class DestinoBloqueadoException : Exception
{
    public DestinoBloqueadoException(string mensaje)
        : base(mensaje)
    {
    }
}

/// <summary>El nombre no se resuelve a ninguna dirección.</summary>
public sealed class NoSeResuelveException : Exception
{
    public NoSeResuelveException(string mensaje, Exception? interna = null)
        : base(mensaje, interna)
    {
    }
}

/// <summary>Resuelve un nombre a sus direcciones IP. Es una interfaz para poder simular respuestas DNS en los tests.</summary>
public interface IResolvedorDirecciones
{
    Task<IReadOnlyList<IPAddress>> ResolverAsync(string host, CancellationToken cancellationToken);
}

public sealed class ResolvedorSistema : IResolvedorDirecciones
{
    public async Task<IReadOnlyList<IPAddress>> ResolverAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            return await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (SocketException excepcion) when (excepcion.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
        {
            throw new NoSeResuelveException($"El nombre «{host}» no se resuelve ({excepcion.SocketErrorCode}).", excepcion);
        }
    }
}

/// <summary>Abre una conexión TCP a una dirección IP ya validada. Es una interfaz para poder simular la red en los tests.</summary>
public interface IConector
{
    Task<Socket> ConectarAsync(IPAddress direccion, int puerto, CancellationToken cancellationToken);
}

public sealed class ConectorSocket : IConector
{
    public async Task<Socket> ConectarAsync(IPAddress direccion, int puerto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(direccion);

        var socket = new Socket(direccion.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(direccion, puerto), cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Decide a qué direcciones se puede conectar. Resuelve el nombre <b>una sola vez</b> y devuelve las
/// direcciones ya validadas: quien conecta debe usar esas direcciones, no volver a resolver el
/// nombre. Si se validara el nombre y luego se conectara «al nombre», un servidor DNS malicioso
/// podría contestar una dirección pública al comprobar y una interna al conectar (<i>DNS rebinding</i>).
/// </summary>
public sealed class GuardiaDeDestinos(IResolvedorDirecciones resolvedor)
{
    /// <summary>
    /// Devuelve las direcciones de <paramref name="host"/> a las que se puede conectar. Si alguna de las
    /// respuestas está bloqueada se rechaza el destino entero, sin quedarse «solo con las buenas»: un
    /// nombre que mezcla direcciones públicas e internas es sospechoso.
    /// </summary>
    public async Task<IReadOnlyList<IPAddress>> ResolverPermitidasAsync(string host, bool permitirRedPrivada, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        IReadOnlyList<IPAddress> direcciones = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await resolvedor.ResolverAsync(host, cancellationToken);

        if (direcciones.Count == 0)
        {
            throw new NoSeResuelveException($"El nombre «{host}» no tiene ninguna dirección.");
        }

        if (!permitirRedPrivada)
        {
            foreach (var direccion in direcciones)
            {
                if (RangosBloqueados.Motivo(direccion) is { } motivo)
                {
                    throw new DestinoBloqueadoException($"«{host}» apunta a {direccion}, {motivo}. Está prohibido salvo que el monitor permita la red privada.");
                }
            }
        }

        return direcciones;
    }
}
