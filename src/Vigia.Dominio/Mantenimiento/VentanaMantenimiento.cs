using Vigia.Dominio.Comun;

namespace Vigia.Dominio.Mantenimiento;

public static class ErroresMantenimiento
{
    public static readonly ErrorDominio PeriodoInvalido =
        new("mantenimiento.periodo_invalido", "La ventana de mantenimiento debe terminar después de empezar y durar como máximo 30 días.");

    public static readonly ErrorDominio MotivoInvalido =
        new("mantenimiento.motivo_invalido", "El motivo del mantenimiento debe tener entre 1 y 200 caracteres.");

    public static readonly ErrorDominio SinMonitores =
        new("mantenimiento.sin_monitores", "La ventana de mantenimiento debe afectar al menos a un monitor.");
}

/// <summary>
/// Un periodo planificado en el que unos monitores no avisan ni cuentan en la disponibilidad: un
/// reinicio programado, una migración. Sin ventana, un mantenimiento se leería como una caída y
/// manchará la disponibilidad de los 90 días de la página de estado.
/// </summary>
public sealed class VentanaMantenimiento
{
    public static readonly TimeSpan DuracionMaxima = TimeSpan.FromDays(30);

    private readonly Guid[] _monitorIds;

    // Constructor para que EF Core reconstruya la ventana desde la base de datos.
    private VentanaMantenimiento()
    {
        Motivo = null!;
        _monitorIds = [];
    }

    private VentanaMantenimiento(Guid[] monitorIds, DateTimeOffset inicio, DateTimeOffset fin, string motivo)
    {
        Id = Guid.NewGuid();
        _monitorIds = monitorIds;
        Inicio = inicio.ToUniversalTime();
        Fin = fin.ToUniversalTime();
        Motivo = motivo;
    }

    public Guid Id { get; }

    public IReadOnlyList<Guid> MonitorIds => _monitorIds;

    public DateTimeOffset Inicio { get; }

    public DateTimeOffset Fin { get; }

    public string Motivo { get; }

    public static Resultado<VentanaMantenimiento> Crear(IReadOnlyCollection<Guid> monitorIds, DateTimeOffset inicio, DateTimeOffset fin, string motivo)
    {
        ArgumentNullException.ThrowIfNull(monitorIds);

        if (monitorIds.Count == 0)
        {
            return Resultado.Fallo<VentanaMantenimiento>(ErroresMantenimiento.SinMonitores);
        }

        if (fin <= inicio || fin - inicio > DuracionMaxima)
        {
            return Resultado.Fallo<VentanaMantenimiento>(ErroresMantenimiento.PeriodoInvalido);
        }

        var motivoLimpio = motivo?.Trim() ?? string.Empty;

        return motivoLimpio.Length is < 1 or > 200
            ? Resultado.Fallo<VentanaMantenimiento>(ErroresMantenimiento.MotivoInvalido)
            : Resultado.Exito(new VentanaMantenimiento([.. monitorIds.Distinct()], inicio, fin, motivoLimpio));
    }

    /// <summary>¿Está el monitor en mantenimiento en este instante? El inicio cuenta y el fin no: [inicio, fin).</summary>
    public bool Cubre(Guid monitorId, DateTimeOffset momento) =>
        momento >= Inicio && momento < Fin && _monitorIds.Contains(monitorId);

    /// <summary>¿Alguna de las ventanas cubre al monitor en este instante?</summary>
    public static bool AlgunaCubre(IEnumerable<VentanaMantenimiento> ventanas, Guid monitorId, DateTimeOffset momento)
    {
        ArgumentNullException.ThrowIfNull(ventanas);

        return ventanas.Any(v => v.Cubre(monitorId, momento));
    }
}
