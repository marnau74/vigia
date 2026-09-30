using System.Net.Sockets;
using System.Security.Authentication;

using Vigia.Comprobaciones.Red;
using Vigia.Dominio.Monitores;

namespace Vigia.Comprobaciones;

/// <summary>Por qué falló una comprobación. Sirve para agrupar causas y para explicar el aviso.</summary>
public enum TipoFallo
{
    Ninguno = 0,
    TiempoAgotado = 1,
    ConexionRechazada = 2,
    ResolucionDns = 3,
    Tls = 4,
    CodigoInesperado = 5,
    PalabraClaveAusente = 6,
    DestinoBloqueado = 7,
    RegistroInesperado = 8,
    Otro = 9,
}

/// <summary>Lo que resulta de una comprobación: si fue bien, cuánto tardó y, si no, por qué.</summary>
/// <param name="Correcto">La comprobación cumplió lo que se le pedía.</param>
/// <param name="Latencia">Cuánto tardó, medido con el reloj inyectado.</param>
/// <param name="Fallo">La causa, o <see cref="TipoFallo.Ninguno"/> si fue bien.</param>
/// <param name="Error">Un texto legible para la persona que lee el aviso.</param>
/// <param name="Detalles">Datos propios de cada tipo (código HTTP, días de certificado, registros DNS…).</param>
public sealed record ResultadoComprobacion(
    bool Correcto,
    TimeSpan Latencia,
    TipoFallo Fallo,
    string? Error,
    IReadOnlyDictionary<string, string> Detalles)
{
    private static readonly IReadOnlyDictionary<string, string> SinDetalles = new Dictionary<string, string>();

    public static ResultadoComprobacion Exito(TimeSpan latencia, IReadOnlyDictionary<string, string>? detalles = null) =>
        new(true, latencia, TipoFallo.Ninguno, null, detalles ?? SinDetalles);

    public static ResultadoComprobacion Fallido(TipoFallo fallo, string error, TimeSpan latencia, IReadOnlyDictionary<string, string>? detalles = null) =>
        new(false, latencia, fallo, error, detalles ?? SinDetalles);
}

/// <summary>Hace un tipo de comprobación. Nunca lanza por un fallo del destino: lo devuelve como resultado.</summary>
public interface IComprobador
{
    TipoMonitor Tipo { get; }

    Task<ResultadoComprobacion> ComprobarAsync(ConfiguracionMonitor configuracion, CancellationToken cancellationToken);
}

/// <summary>Mide el tiempo desde que empezó la comprobación, con el reloj inyectado (los tests lo controlan).</summary>
public readonly struct Cronometro(TimeProvider reloj)
{
    private readonly long _inicio = reloj.GetTimestamp();

    public TimeSpan Transcurrido => reloj.GetElapsedTime(_inicio);
}

/// <summary>
/// Lo común a todas las comprobaciones: el tiempo máximo (que corta la comprobación y la cuenta como
/// fallo, no como error del programa), la medición de la latencia y la traducción de las excepciones
/// de red a causas legibles. Cada tipo solo escribe lo suyo.
/// </summary>
public abstract class Comprobador<TConfiguracion>(TimeProvider reloj) : IComprobador
    where TConfiguracion : ConfiguracionMonitor
{
    protected TimeProvider Reloj { get; } = reloj;

    public abstract TipoMonitor Tipo { get; }

    public async Task<ResultadoComprobacion> ComprobarAsync(ConfiguracionMonitor configuracion, CancellationToken cancellationToken)
    {
        if (configuracion is not TConfiguracion propia)
        {
            throw new ArgumentException($"Este comprobador es de tipo {Tipo} y recibió {configuracion?.GetType().Name ?? "nada"}.", nameof(configuracion));
        }

        var cronometro = new Cronometro(Reloj);

        // El tiempo máximo se cuenta con el reloj inyectado: en los tests se avanza a mano, sin esperar.
        using var limite = new CancellationTokenSource(propia.TiempoMaximo, Reloj);
        using var enlazado = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limite.Token);

        try
        {
            return await EjecutarAsync(propia, cronometro, enlazado.Token);
        }
        catch (OperationCanceledException) when (limite.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ResultadoComprobacion.Fallido(
                TipoFallo.TiempoAgotado,
                $"Sin respuesta en {propia.TiempoMaximo.TotalSeconds:0.##} s.",
                cronometro.Transcurrido);
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            var (tipo, mensaje) = Clasificar(excepcion);

            return ResultadoComprobacion.Fallido(tipo, mensaje, cronometro.Transcurrido);
        }
    }

    protected abstract Task<ResultadoComprobacion> EjecutarAsync(TConfiguracion configuracion, Cronometro cronometro, CancellationToken cancellationToken);

    /// <summary>Recorre la cadena de excepciones (HttpClient envuelve las de red) hasta la que explica el fallo.</summary>
    protected static (TipoFallo Tipo, string Mensaje) Clasificar(Exception excepcion)
    {
        for (var actual = excepcion; actual is not null; actual = actual.InnerException)
        {
            switch (actual)
            {
                case DestinoBloqueadoException bloqueado:
                    return (TipoFallo.DestinoBloqueado, bloqueado.Message);
                case NoSeResuelveException noResuelve:
                    return (TipoFallo.ResolucionDns, noResuelve.Message);
                case AuthenticationException autenticacion:
                    return (TipoFallo.Tls, $"Fallo de TLS: {autenticacion.Message}");
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return (TipoFallo.ConexionRechazada, "Conexión rechazada: no hay nada escuchando en ese puerto.");
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } dns:
                    return (TipoFallo.ResolucionDns, $"El nombre no se resuelve ({dns.SocketErrorCode}).");
                case SocketException socket:
                    return (TipoFallo.Otro, $"Error de red: {socket.SocketErrorCode}.");
            }
        }

        return (TipoFallo.Otro, excepcion.Message);
    }
}
