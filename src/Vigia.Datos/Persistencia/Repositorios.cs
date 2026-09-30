using Microsoft.EntityFrameworkCore;

using Vigia.Dominio.Avisos;
using Vigia.Dominio.Mantenimiento;
using Vigia.Dominio.Seguimiento;

namespace Vigia.Datos.Persistencia;

/// <summary>Lo que el worker necesita saber de los monitores para planificarlos.</summary>
public sealed class RepositorioMonitores(VigiaDbContext db)
{
    public async Task<IReadOnlyList<Dominio.Monitores.Monitor>> ListarActivosAsync(CancellationToken cancellationToken) =>
        await db.Monitores.AsNoTracking().Where(m => m.Activo).OrderBy(m => m.Nombre).ToListAsync(cancellationToken);

    /// <summary>Las ventanas de mantenimiento que todavía no han terminado (en curso o futuras).</summary>
    public async Task<IReadOnlyList<VentanaMantenimiento>> ListarVentanasVigentesAsync(DateTimeOffset ahora, CancellationToken cancellationToken) =>
        await db.VentanasMantenimiento.AsNoTracking().Where(v => v.Fin > ahora).ToListAsync(cancellationToken);

    public async Task AgregarAsync(Dominio.Monitores.Monitor monitor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        db.Monitores.Add(monitor);
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Guarda y recupera el seguimiento de un monitor (su máquina de estados) junto con lo que produce:
/// el resultado de la comprobación, los cambios de estado y los incidentes. Todo lo de una
/// comprobación se guarda en <b>una sola operación</b> (una transacción): o queda todo o no queda nada,
/// y así nunca hay un incidente abierto sin su cambio de estado, ni un estado sin su resultado, ni una
/// caída sin su aviso.
/// </summary>
public sealed class RepositorioSeguimiento(VigiaDbContext db)
{
    /// <summary>El seguimiento guardado, o uno nuevo (sin datos) si el monitor todavía no tiene.</summary>
    public async Task<SeguimientoDeMonitor> CargarAsync(Guid monitorId, DateTimeOffset ahora, CancellationToken cancellationToken)
    {
        var fila = await db.Seguimientos.AsNoTracking().FirstOrDefaultAsync(s => s.MonitorId == monitorId, cancellationToken);

        if (fila is null)
        {
            return new SeguimientoDeMonitor(monitorId, ahora);
        }

        var incidente = fila.IncidenteAbiertoId is { } id
            ? await db.Incidentes.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            : null;

        return new SeguimientoDeMonitor(monitorId, fila.Estado, fila.FallosSeguidos, fila.Desde, fila.UltimaObservacion, incidente);
    }

    public async Task GuardarAsync(
        SeguimientoDeMonitor seguimiento,
        IReadOnlyList<EventoDeSeguimiento> eventos,
        ResultadoEntidad resultado,
        IReadOnlyList<Aviso> avisos,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seguimiento);
        ArgumentNullException.ThrowIfNull(eventos);
        ArgumentNullException.ThrowIfNull(resultado);
        ArgumentNullException.ThrowIfNull(avisos);

        db.Resultados.Add(resultado);

        // Los avisos entran en la misma transacción que el cambio de estado que los provoca (bandeja de salida).
        db.Avisos.AddRange(avisos);

        foreach (var evento in eventos)
        {
            switch (evento)
            {
                case EstadoCambiado cambio:
                    db.CambiosDeEstado.Add(new CambioDeEstadoEntidad { MonitorId = cambio.MonitorId, Momento = cambio.Momento, Anterior = cambio.Anterior, Nuevo = cambio.Nuevo });
                    break;
                case IncidenteAbierto abierto:
                    db.Incidentes.Add(abierto.Incidente);
                    break;
                case IncidenteCerrado cerrado:
                    db.Incidentes.Update(cerrado.Incidente);
                    break;
            }
        }

        // Mientras siga caído, cada fallo actualiza la causa y el contador del incidente abierto.
        if (seguimiento.IncidenteAbierto is { } enCurso && !eventos.OfType<IncidenteAbierto>().Any())
        {
            db.Incidentes.Update(enCurso);
        }

        var fila = await db.Seguimientos.FindAsync([seguimiento.MonitorId], cancellationToken);

        if (fila is null)
        {
            fila = new SeguimientoEntidad { MonitorId = seguimiento.MonitorId };
            db.Seguimientos.Add(fila);
        }

        fila.Estado = seguimiento.Estado;
        fila.FallosSeguidos = seguimiento.FallosSeguidos;
        fila.Desde = seguimiento.Desde;
        fila.UltimaObservacion = seguimiento.UltimaObservacion;
        fila.IncidenteAbiertoId = seguimiento.IncidenteAbierto?.Id;

        await db.SaveChangesAsync(cancellationToken);
    }
}
