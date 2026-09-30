using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace Vigia.Worker.Planificacion;

/// <summary>
/// Se entera al momento de que la API ha creado, cambiado o pausado un monitor, sin preguntar a la base
/// de datos cada pocos segundos: PostgreSQL avisa por el canal <c>monitores_cambiados</c>
/// (<c>LISTEN/NOTIFY</c>) y el planificador recarga. Si la conexión se cae se reconecta, y como un aviso
/// perdido no se puede recuperar, al reconectar se pide una recarga por si acaso (además de la recarga
/// periódica del planificador, que es la red de seguridad).
/// </summary>
public sealed class EscuchaDeCambios(string cadenaDeConexion, Planificador planificador, TimeProvider reloj, ILogger<EscuchaDeCambios> log) : BackgroundService
{
    public const string Canal = "monitores_cambiados";

    public static readonly TimeSpan EsperaTrasFallo = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conexion = new NpgsqlConnection(cadenaDeConexion);
                await conexion.OpenAsync(stoppingToken);
                conexion.Notification += (_, _) => planificador.Recargar();

                await using (var orden = new NpgsqlCommand($"LISTEN {Canal}", conexion))
                {
                    await orden.ExecuteNonQueryAsync(stoppingToken);
                }

                planificador.Recargar();

                while (!stoppingToken.IsCancellationRequested)
                {
                    await conexion.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception excepcion)
            {
                log.EscuchaPerdida(excepcion, EsperaTrasFallo.TotalSeconds);

                try
                {
                    await Task.Delay(EsperaTrasFallo, reloj, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
