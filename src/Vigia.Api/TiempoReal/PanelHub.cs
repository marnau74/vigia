using System.Threading.Channels;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using Vigia.Datos.Persistencia;

namespace Vigia.Api.TiempoReal;

/// <summary>
/// El canal por el que el panel se entera de lo que pasa sin recargar. Solo el servidor habla: los clientes
/// escuchan <see cref="MonitorActualizado"/> y no pueden enviar nada. Requiere sesión, igual que el resto de la API privada.
/// </summary>
[Authorize]
public sealed class PanelHub : Hub
{
    public const string Ruta = "/hubs/panel";

    /// <summary>El nombre del mensaje que reciben los clientes: una comprobación guardada, con el estado resultante.</summary>
    public const string MonitorActualizado = "MonitorActualizado";
}

/// <summary>
/// Reenvía al panel las comprobaciones que guarda el worker. El worker avisa por <c>NOTIFY</c> de
/// PostgreSQL; este servicio escucha, y cada aviso sale por SignalR a todos los paneles conectados.
/// </summary>
/// <remarks>
/// Entre la escucha y el envío hay una cola acotada: si los paneles fueran más lentos que el ritmo de
/// comprobaciones, se descartan los mensajes más antiguos en lugar de acumular memoria (al panel le
/// interesa lo último, no una cola de lo que ya pasó).
/// </remarks>
public sealed partial class ReenvioDeComprobaciones(string cadenaDeConexion, IHubContext<PanelHub> hub, TimeProvider reloj, ILogger<ReenvioDeComprobaciones> log) : BackgroundService
{
    public const int CapacidadDeLaCola = 1_000;

    private readonly Channel<ComprobacionPublicada> _cola = Channel.CreateBounded<ComprobacionPublicada>(
        new BoundedChannelOptions(CapacidadDeLaCola) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var escucha = EscuchaDeNotificaciones.EscucharAsync(
            cadenaDeConexion,
            Notificaciones.ComprobacionGuardada,
            alRecibir: carga =>
            {
                if (ComprobacionPublicada.Leer(carga) is { } comprobacion)
                {
                    _cola.Writer.TryWrite(comprobacion);
                }
            },
            alConectar: () => { },
            alFallar: excepcion => EscuchaPerdida(log, excepcion),
            reloj,
            stoppingToken);

        try
        {
            await foreach (var comprobacion in _cola.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await hub.Clients.All.SendAsync(PanelHub.MonitorActualizado, comprobacion, stoppingToken);
                }
                catch (Exception excepcion) when (excepcion is not OperationCanceledException)
                {
                    EnvioFallido(log, excepcion);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Parada normal.
        }

        await escucha;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Se perdió la escucha de comprobaciones; se reintenta en unos segundos.")]
    private static partial void EscuchaPerdida(ILogger log, Exception excepcion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo enviar una actualización al panel.")]
    private static partial void EnvioFallido(ILogger log, Exception excepcion);
}
