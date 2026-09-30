using Vigia.Dominio.Disponibilidad;
using Vigia.Dominio.Monitores;

namespace Vigia.Datos.Persistencia;

/// <summary>
/// El seguimiento de un monitor tal como se guarda. Es una entidad de persistencia aparte y no la
/// propia máquina de estados del dominio, porque esta referencia al incidente abierto como un objeto
/// y en la base de datos es una clave.
/// </summary>
public sealed class SeguimientoEntidad
{
    public Guid MonitorId { get; set; }

    public EstadoMonitor Estado { get; set; }

    public int FallosSeguidos { get; set; }

    public DateTimeOffset Desde { get; set; }

    public DateTimeOffset? UltimaObservacion { get; set; }

    public Guid? IncidenteAbiertoId { get; set; }
}

/// <summary>Un cambio de estado de un monitor. Del registro de cambios salen los tramos con los que se calcula la disponibilidad.</summary>
public sealed class CambioDeEstadoEntidad
{
    public long Id { get; set; }

    public Guid MonitorId { get; set; }

    public DateTimeOffset Momento { get; set; }

    public EstadoMonitor Anterior { get; set; }

    public EstadoMonitor Nuevo { get; set; }
}

/// <summary>Una comprobación. La tabla está particionada por mes (ver la migración «ResultadosParticionados»).</summary>
public sealed class ResultadoEntidad
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MonitorId { get; set; }

    public DateTimeOffset Momento { get; set; }

    public bool Correcto { get; set; }

    public int LatenciaMs { get; set; }

    /// <summary>Un valor de <c>TipoFallo</c> (0 si fue bien). Es un número para que la base de datos no dependa de la biblioteca de red.</summary>
    public short Fallo { get; set; }

    public string? Error { get; set; }

    /// <summary>Los detalles propios de cada tipo (código HTTP, días del certificado…), como JSON.</summary>
    public string? Detalles { get; set; }

    /// <summary>Se hizo durante una ventana de mantenimiento: se guarda, pero no cuenta.</summary>
    public bool EnMantenimiento { get; set; }
}

/// <summary>Resumen de un periodo de un monitor: tiempo en cada estado y estadísticas de las comprobaciones.</summary>
public abstract class AgregadoDeResultados
{
    public Guid MonitorId { get; set; }

    /// <summary>El inicio del periodo (una hora o un día, en UTC).</summary>
    public DateTimeOffset Periodo { get; set; }

    public double SegDesconocido { get; set; }

    public double SegOperativo { get; set; }

    public double SegDegradado { get; set; }

    public double SegSospechoso { get; set; }

    public double SegCaido { get; set; }

    public double SegMantenimiento { get; set; }

    public int Comprobaciones { get; set; }

    public int Correctas { get; set; }

    public int Fallidas { get; set; }

    /// <summary>Las hechas durante un mantenimiento, que no entran en las dos cuentas anteriores.</summary>
    public int EnMantenimiento { get; set; }

    public double? LatenciaMediaMs { get; set; }

    public TiemposPorEstado Tiempos => TiemposPorEstado.De(
    [
        (EstadoMonitor.Desconocido, TimeSpan.FromSeconds(SegDesconocido)),
        (EstadoMonitor.Operativo, TimeSpan.FromSeconds(SegOperativo)),
        (EstadoMonitor.Degradado, TimeSpan.FromSeconds(SegDegradado)),
        (EstadoMonitor.Sospechoso, TimeSpan.FromSeconds(SegSospechoso)),
        (EstadoMonitor.Caido, TimeSpan.FromSeconds(SegCaido)),
        (EstadoMonitor.Mantenimiento, TimeSpan.FromSeconds(SegMantenimiento)),
    ]);

    public void PonerTiempos(TiemposPorEstado tiempos)
    {
        ArgumentNullException.ThrowIfNull(tiempos);

        SegDesconocido = tiempos[EstadoMonitor.Desconocido].TotalSeconds;
        SegOperativo = tiempos[EstadoMonitor.Operativo].TotalSeconds;
        SegDegradado = tiempos[EstadoMonitor.Degradado].TotalSeconds;
        SegSospechoso = tiempos[EstadoMonitor.Sospechoso].TotalSeconds;
        SegCaido = tiempos[EstadoMonitor.Caido].TotalSeconds;
        SegMantenimiento = tiempos[EstadoMonitor.Mantenimiento].TotalSeconds;
    }
}

public sealed class AgregadoHora : AgregadoDeResultados
{
    public double? P50Ms { get; set; }

    public double? P95Ms { get; set; }
}

public sealed class AgregadoDia : AgregadoDeResultados
{
    /// <summary>
    /// El mayor p95 de las horas del día. Un percentil no se puede sumar ni promediar, y las
    /// comprobaciones sin agregar ya no existen cuando se consulta un día antiguo: se guarda el peor
    /// p95 horario, que es una cota superior honesta y no un percentil inventado.
    /// </summary>
    public double? PeorP95HoraMs { get; set; }
}
