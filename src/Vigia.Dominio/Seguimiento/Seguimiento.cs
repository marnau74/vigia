using Vigia.Dominio.Comun;
using Vigia.Dominio.Monitores;

namespace Vigia.Dominio.Seguimiento;

/// <summary>
/// Lo que el dominio necesita saber de una comprobación. Es su propio tipo, y no el resultado de la
/// biblioteca de red: el dominio no depende de cómo se comprueba, solo de si salió bien y cuánto tardó.
/// </summary>
/// <param name="Correcto">La comprobación cumplió lo que se le pedía.</param>
/// <param name="Latencia">Cuánto tardó.</param>
/// <param name="Error">La causa del fallo, para el incidente y el aviso. Sin valor si fue bien.</param>
/// <param name="Momento">Cuándo se hizo.</param>
public sealed record Observacion(bool Correcto, TimeSpan Latencia, string? Error, DateTimeOffset Momento);

/// <summary>Cómo se cierra un incidente.</summary>
public enum MotivoDeCierre
{
    /// <summary>El servicio volvió a responder.</summary>
    Recuperado = 1,

    /// <summary>Empezó una ventana de mantenimiento: el tiempo que sigue no cuenta y no se avisa.</summary>
    Mantenimiento = 2,
}

/// <summary>Un periodo en el que un servicio estuvo caído, de principio a fin.</summary>
public sealed class Incidente
{
    // Constructor para que EF Core reconstruya el incidente desde la base de datos.
    private Incidente() => Causa = null!;

    private Incidente(Guid monitorId, DateTimeOffset abiertoEn, string causa, int fallos)
    {
        Id = Guid.NewGuid();
        MonitorId = monitorId;
        AbiertoEn = abiertoEn.ToUniversalTime();
        Causa = Recortar(causa);
        Fallos = fallos;
    }

    /// <summary>Longitud máxima de la causa: lo bastante larga para un mensaje de error y lo bastante corta para no guardar una página entera.</summary>
    public const int LongitudMaximaCausa = 500;

    public Guid Id { get; }

    public Guid MonitorId { get; }

    public DateTimeOffset AbiertoEn { get; }

    public DateTimeOffset? CerradoEn { get; private set; }

    public MotivoDeCierre? CerradoPor { get; private set; }

    /// <summary>El último error visto mientras el incidente estuvo abierto (lo que se enseña en el aviso y en el panel).</summary>
    public string Causa { get; private set; }

    /// <summary>Cuántas comprobaciones fallidas se han visto durante el incidente.</summary>
    public int Fallos { get; private set; }

    public bool EstaAbierto => CerradoEn is null;

    public TimeSpan Duracion(DateTimeOffset ahora) => (CerradoEn ?? ahora) - AbiertoEn;

    public static Incidente Abrir(Guid monitorId, DateTimeOffset momento, string causa, int fallos) =>
        new(monitorId, momento, causa, fallos);

    /// <summary>Anota otro fallo mientras sigue caído y guarda su causa como la más reciente.</summary>
    public void RegistrarFallo(string causa)
    {
        Causa = Recortar(causa);
        Fallos++;
    }

    private static string Recortar(string causa) => causa.Length > LongitudMaximaCausa ? causa[..LongitudMaximaCausa] : causa;

    public void Cerrar(DateTimeOffset momento, MotivoDeCierre motivo)
    {
        if (!EstaAbierto)
        {
            return;
        }

        CerradoEn = momento.ToUniversalTime();
        CerradoPor = motivo;
    }
}

/// <summary>Algo que ha pasado al registrar una observación y que otros deben saber (guardarlo, avisar, enseñarlo).</summary>
public abstract record EventoDeSeguimiento(Guid MonitorId, DateTimeOffset Momento);

/// <summary>El estado del monitor ha cambiado. Se guarda cada cambio para poder calcular cuánto tiempo estuvo en cada estado.</summary>
public sealed record EstadoCambiado(Guid MonitorId, EstadoMonitor Anterior, EstadoMonitor Nuevo, DateTimeOffset Momento)
    : EventoDeSeguimiento(MonitorId, Momento);

/// <summary>Se ha abierto un incidente: hay que avisar de la caída (una sola vez).</summary>
public sealed record IncidenteAbierto(Guid MonitorId, Incidente Incidente, DateTimeOffset Momento)
    : EventoDeSeguimiento(MonitorId, Momento);

/// <summary>El incidente se ha cerrado: hay que avisar de la recuperación (una sola vez), salvo que fuera por mantenimiento.</summary>
public sealed record IncidenteCerrado(Guid MonitorId, Incidente Incidente, MotivoDeCierre Motivo, DateTimeOffset Momento)
    : EventoDeSeguimiento(MonitorId, Momento);

/// <summary>Cómo se interpreta una comprobación de un monitor.</summary>
/// <param name="FallosParaIncidente">Fallos seguidos que hacen falta para dar el servicio por caído.</param>
/// <param name="UmbralLento">Por encima de esta latencia, un éxito cuenta como «degradado». Sin valor, nunca.</param>
public sealed record ReglasDeSeguimiento(int FallosParaIncidente, TimeSpan? UmbralLento)
{
    public static ReglasDeSeguimiento De(Monitores.Monitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        return new ReglasDeSeguimiento(monitor.FallosParaIncidente, monitor.UmbralLento);
    }
}
