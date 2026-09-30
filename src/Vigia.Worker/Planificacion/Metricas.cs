using System.Diagnostics.Metrics;

using Vigia.Comprobaciones;

namespace Vigia.Worker.Planificacion;

/// <summary>Opciones del planificador (sección «Planificador» de la configuración).</summary>
public sealed class OpcionesPlanificador
{
    public const string Seccion = "Planificador";

    /// <summary>Cuántas comprobaciones pueden estar en marcha a la vez. Limita sockets y memoria aunque haya miles de monitores.</summary>
    public int Concurrencia { get; set; } = 20;

    /// <summary>Cada cuánto se relee la lista de monitores aunque nadie avise de cambios (red de seguridad del LISTEN/NOTIFY).</summary>
    public TimeSpan RecargaCada { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Espera antes de repetir una comprobación fallida, para no dar por caído un servicio por un tropiezo puntual.</summary>
    public TimeSpan EsperaReintento { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Cada cuánto se agregan las horas terminadas y se comprueba el mantenimiento de datos.</summary>
    public TimeSpan MantenimientoCada { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Cuánto se conservan las comprobaciones sin agregar.</summary>
    public TimeSpan RetencionResultados { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Aplica las migraciones al arrancar. Cómodo en local; en producción se decide en la configuración.</summary>
    public bool MigrarAlArrancar { get; set; } = true;
}

/// <summary>Las métricas del worker, para el panel de Aspire y cualquier recolector OpenTelemetry.</summary>
public sealed class MetricasVigia : IDisposable
{
    public const string NombreMedidor = "Vigia";

    private readonly Meter _medidor = new(NombreMedidor);
    private readonly Histogram<double> _duracion;
    private readonly Counter<long> _errores;
    private readonly UpDownCounter<long> _incidentesAbiertos;

    public MetricasVigia()
    {
        _duracion = _medidor.CreateHistogram<double>("vigia.comprobaciones.duracion", "ms", "Duración de cada comprobación.");
        _errores = _medidor.CreateCounter<long>("vigia.comprobaciones.errores_internos", description: "Comprobaciones que no se pudieron guardar por un fallo del propio Vigia.");
        _incidentesAbiertos = _medidor.CreateUpDownCounter<long>("vigia.incidentes.abiertos", description: "Incidentes abiertos ahora mismo.");
    }

    public void Comprobacion(string tipo, bool correcto, TimeSpan duracion) =>
        _duracion.Record(
            duracion.TotalMilliseconds,
            new KeyValuePair<string, object?>("tipo", tipo),
            new KeyValuePair<string, object?>("resultado", correcto ? "correcto" : "fallido"));

    public void ErrorInterno() => _errores.Add(1);

    public void IncidenteAbierto() => _incidentesAbiertos.Add(1);

    public void IncidenteCerrado() => _incidentesAbiertos.Add(-1);

    public void Dispose() => _medidor.Dispose();
}

/// <summary>Lo que ocurre tras guardar una comprobación (fase 4: avisos). El fallo de un manejador no afecta a las comprobaciones.</summary>
public interface IManejadorDeEventos
{
    Task ManejarAsync(IReadOnlyList<Vigia.Dominio.Seguimiento.EventoDeSeguimiento> eventos, CancellationToken cancellationToken);
}

internal sealed class ManejadorDeEventosVacio : IManejadorDeEventos
{
    public Task ManejarAsync(IReadOnlyList<Vigia.Dominio.Seguimiento.EventoDeSeguimiento> eventos, CancellationToken cancellationToken) => Task.CompletedTask;
}
