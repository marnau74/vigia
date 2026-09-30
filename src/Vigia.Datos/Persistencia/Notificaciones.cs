using Microsoft.EntityFrameworkCore;

namespace Vigia.Datos.Persistencia;

/// <summary>
/// Los avisos entre procesos por <c>LISTEN/NOTIFY</c> de PostgreSQL. La API y el worker no se hablan
/// directamente (ADR 0001): se enteran uno del otro a través de la base de datos que ya comparten, sin
/// un bus de mensajes y sin preguntar cada pocos segundos.
/// </summary>
/// <remarks>
/// Un <c>NOTIFY</c> dentro de una transacción solo se entrega al confirmarla, así que quien escucha
/// nunca ve un aviso de algo que todavía no se puede leer.
/// </remarks>
public static class Notificaciones
{
    /// <summary>De la API al worker: la lista de monitores o las ventanas de mantenimiento han cambiado, hay que recargar.</summary>
    public const string CambioDeMonitores = "monitores_cambiados";

    /// <summary>Del worker a la API: una comprobación se ha guardado (la carga es un JSON pequeño), para el panel en tiempo real.</summary>
    public const string ComprobacionGuardada = "comprobaciones_guardadas";

    /// <summary>Un <c>NOTIFY</c> admite cargas de menos de 8.000 bytes; las de aquí son de unos 150.</summary>
    public const int LongitudMaximaDeCarga = 7_000;

    public static Task NotificarAsync(this VigiaDbContext db, string canal, string carga, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(canal);
        ArgumentNullException.ThrowIfNull(carga);

        if (carga.Length > LongitudMaximaDeCarga)
        {
            throw new ArgumentException("La carga de una notificación debe ser pequeña; guarda los datos en tablas y avisa solo de que existen.", nameof(carga));
        }

        return db.Database.ExecuteSqlAsync($"SELECT pg_notify({canal}, {carga})", cancellationToken);
    }

    /// <summary>
    /// Guarda los cambios pendientes y avisa al worker de que los monitores cambiaron, en una sola
    /// transacción: o se guarda y se avisa, o ninguna de las dos cosas.
    /// </summary>
    public static async Task GuardarYAvisarDeCambioAsync(this VigiaDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Aspire configura reintentos automáticos de la conexión, y una transacción propia debe ejecutarse
        // dentro de la estrategia para poder repetirse entera si la conexión falla a medias.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await db.NotificarAsync(CambioDeMonitores, string.Empty, cancellationToken);
            await transaccion.CommitAsync(cancellationToken);
        });
    }
}
