using Vigia.Datos.Persistencia;
using Vigia.Dominio.Seguimiento;

namespace Vigia.Worker.Planificacion;

/// <summary>
/// Cuenta a la API, por <c>NOTIFY</c> de PostgreSQL, que se ha guardado una comprobación: la API lo
/// reenvía por SignalR al panel. El worker y la API no se hablan directamente (ADR 0001): la base de
/// datos que ya comparten hace de mensajero. Un aviso perdido solo significa que el panel se entera en la
/// siguiente comprobación; nunca afecta a lo guardado.
/// </summary>
public sealed class PublicadorDeComprobaciones(IServiceScopeFactory ambitos) : IManejadorDeEventos
{
    public async Task ManejarAsync(ComprobacionRegistrada comprobacion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comprobacion);

        var incidente = comprobacion.Eventos.Any(e => e is IncidenteAbierto) ? "abierto"
            : comprobacion.Eventos.Any(e => e is IncidenteCerrado) ? "cerrado"
            : null;

        var carga = new ComprobacionPublicada(
            comprobacion.MonitorId,
            comprobacion.Momento,
            comprobacion.Correcto,
            (int)Math.Min(int.MaxValue, comprobacion.Latencia.TotalMilliseconds),
            comprobacion.Estado,
            incidente);

        await using var ambito = ambitos.CreateAsyncScope();
        await ambito.ServiceProvider.GetRequiredService<VigiaDbContext>().NotificarAsync(Notificaciones.ComprobacionGuardada, carga.Serializar(), cancellationToken);
    }
}
