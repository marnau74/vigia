using Microsoft.Extensions.Logging;

namespace Vigia.Worker;

/// <summary>Los mensajes de log del worker, generados en compilación (sin reservar memoria si el nivel está apagado).</summary>
internal static partial class Registros
{
    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo guardar la comprobación del monitor {MonitorId}.")]
    public static partial void NoSeGuardo(this ILogger log, Exception excepcion, Guid monitorId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Un manejador de eventos falló; la comprobación ya estaba guardada.")]
    public static partial void ManejadorFallo(this ILogger log, Exception excepcion);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo recargar la lista de monitores; se sigue con la anterior.")]
    public static partial void RecargaFallo(this ILogger log, Exception excepcion);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló la comprobación del monitor {MonitorId}.")]
    public static partial void ComprobacionFallo(this ILogger log, Exception excepcion, Guid monitorId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falló el mantenimiento de datos; se reintentará.")]
    public static partial void MantenimientoFallo(this ILogger log, Exception excepcion);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agregadas {Horas} horas y {Dias} días.")]
    public static partial void Agregado(this ILogger log, int horas, int dias);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retención aplicada: {Particiones} particiones y {Horas} agregados por hora eliminados.")]
    public static partial void RetencionAplicada(this ILogger log, int particiones, int horas);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Se perdió la escucha de cambios de monitores; se reintenta en {Segundos} s.")]
    public static partial void EscuchaPerdida(this ILogger log, Exception excepcion, double segundos);
}
