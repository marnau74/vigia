using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using Vigia.Comprobaciones.Red;
using Vigia.Dominio.Monitores;

namespace Vigia.Comprobaciones;

/// <summary>Un puerto TCP acepta conexiones. Es la comprobación más básica: «¿escucha alguien?».</summary>
public sealed class ComprobadorTcp(GuardiaDeDestinos guardia, IConector conector, TimeProvider reloj) : Comprobador<ConfiguracionTcp>(reloj)
{
    public override TipoMonitor Tipo => TipoMonitor.Tcp;

    protected override async Task<ResultadoComprobacion> EjecutarAsync(ConfiguracionTcp configuracion, Cronometro cronometro, CancellationToken cancellationToken)
    {
        var direcciones = await guardia.ResolverPermitidasAsync(configuracion.Host, configuracion.PermitirRedPrivada, cancellationToken);

        Exception? ultimoError = null;

        // Un nombre puede tener varias direcciones (IPv6 e IPv4): basta con que una responda.
        foreach (var direccion in direcciones)
        {
            try
            {
                using var socket = await conector.ConectarAsync(direccion, configuracion.Puerto, cancellationToken);

                return ResultadoComprobacion.Exito(
                    cronometro.Transcurrido,
                    new Dictionary<string, string> { ["ip"] = direccion.ToString(), ["puerto"] = configuracion.Puerto.ToString(CultureInfo.InvariantCulture) });
            }
            catch (SocketException excepcion)
            {
                ultimoError = excepcion;
            }
        }

        throw ultimoError!;
    }
}

/// <summary>Respuesta de un ping, sin depender de la clase de .NET para poder simularla.</summary>
public sealed record RespuestaPing(bool Exito, TimeSpan TiempoDeIda, string Estado);

/// <summary>Envía un ping ICMP. Es una interfaz porque no todos los entornos lo permiten (ver <see cref="ComprobadorIcmp"/>).</summary>
public interface IEnviadorPing
{
    Task<RespuestaPing> EnviarAsync(IPAddress direccion, TimeSpan tiempoMaximo, CancellationToken cancellationToken);
}

public sealed class EnviadorPingSistema : IEnviadorPing
{
    public async Task<RespuestaPing> EnviarAsync(IPAddress direccion, TimeSpan tiempoMaximo, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var respuesta = await ping.SendPingAsync(direccion, tiempoMaximo, cancellationToken: cancellationToken);

        return new RespuestaPing(respuesta.Status == IPStatus.Success, TimeSpan.FromMilliseconds(respuesta.RoundtripTime), respuesta.Status.ToString());
    }
}

/// <summary>
/// Un equipo responde al ping. En un contenedor Linux necesita el permiso <c>CAP_NET_RAW</c> (o
/// <c>net.ipv4.ping_group_range</c>): sin él, .NET no puede enviar el paquete y la comprobación falla
/// con un mensaje que lo explica, en lugar de dar «caído» sin más.
/// </summary>
public sealed class ComprobadorIcmp(GuardiaDeDestinos guardia, IEnviadorPing enviador, TimeProvider reloj) : Comprobador<ConfiguracionIcmp>(reloj)
{
    public override TipoMonitor Tipo => TipoMonitor.Icmp;

    protected override async Task<ResultadoComprobacion> EjecutarAsync(ConfiguracionIcmp configuracion, Cronometro cronometro, CancellationToken cancellationToken)
    {
        var direcciones = await guardia.ResolverPermitidasAsync(configuracion.Host, configuracion.PermitirRedPrivada, cancellationToken);
        var direccion = direcciones[0];

        RespuestaPing respuesta;

        try
        {
            respuesta = await enviador.EnviarAsync(direccion, configuracion.TiempoMaximo, cancellationToken);
        }
        catch (PingException excepcion)
        {
            return ResultadoComprobacion.Fallido(
                TipoFallo.Otro,
                $"No se pudo enviar el ping: {excepcion.InnerException?.Message ?? excepcion.Message}. En un contenedor Linux hace falta el permiso CAP_NET_RAW.",
                cronometro.Transcurrido);
        }

        var detalles = new Dictionary<string, string> { ["ip"] = direccion.ToString(), ["estado"] = respuesta.Estado };

        return respuesta.Exito
            ? ResultadoComprobacion.Exito(respuesta.TiempoDeIda, detalles)
            : ResultadoComprobacion.Fallido(TipoFallo.Otro, $"El equipo no respondió al ping ({respuesta.Estado}).", cronometro.Transcurrido, detalles);
    }
}
