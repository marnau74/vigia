using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Vigia.Datos.Persistencia;

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
    public const string Canal = Notificaciones.CambioDeMonitores;

    public static readonly TimeSpan EsperaTrasFallo = EscuchaDeNotificaciones.EsperaTrasFallo;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        EscuchaDeNotificaciones.EscucharAsync(
            cadenaDeConexion,
            Canal,
            alRecibir: _ => planificador.Recargar(),
            alConectar: planificador.Recargar,
            alFallar: excepcion => log.EscuchaPerdida(excepcion, EsperaTrasFallo.TotalSeconds),
            reloj,
            stoppingToken);
}
