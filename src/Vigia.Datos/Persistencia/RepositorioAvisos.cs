using Microsoft.EntityFrameworkCore;

using Vigia.Dominio.Avisos;
using Vigia.Dominio.Seguimiento;

namespace Vigia.Datos.Persistencia;

/// <summary>Lo que hace falta saber, además del propio aviso, para redactarlo.</summary>
public sealed record ContextoDeAviso(string NombreDelMonitor, Incidente Incidente);

/// <summary>
/// La bandeja de salida de avisos en PostgreSQL. Varias instancias del worker pueden vaciarla a la vez
/// sin enviar dos veces el mismo aviso.
/// </summary>
public sealed class RepositorioAvisos(VigiaDbContext db)
{
    public async Task<IReadOnlyList<Aviso>> ReclamarAsync(DateTimeOffset ahora, TimeSpan reserva, int maximo, CancellationToken cancellationToken)
    {
        var momento = ahora.ToUniversalTime();
        var hasta = (ahora + reserva).ToUniversalTime();

        // En un solo UPDATE atómico se eligen los avisos que toca enviar y se les aplaza el próximo
        // intento: «FOR UPDATE SKIP LOCKED» hace que, si otra instancia está eligiendo a la vez, cada una
        // salte los que la otra ya tiene en la mano en lugar de esperarla. Como el UPDATE confirma al
        // instante, no se mantiene ningún bloqueo mientras se habla con el servidor de correo o con
        // Telegram, que pueden tardar; si el proceso muere a medias, el aviso vuelve a tocar al pasar la
        // reserva en lugar de perderse.
        var ids = await db.Database.SqlQuery<Guid>($"""
            UPDATE avisos
            SET proximo_intento_en = {hasta}
            WHERE id IN (
                SELECT id FROM avisos
                WHERE enviado_en IS NULL AND abandonado = false AND proximo_intento_en <= {momento}
                ORDER BY proximo_intento_en
                LIMIT {maximo}
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);

        return await db.Avisos.Where(aviso => ids.Contains(aviso.Id)).OrderBy(aviso => aviso.CreadoEn).ToListAsync(cancellationToken);
    }

    /// <summary>El nombre del monitor y el incidente tal como están ahora (no como estaban al crear el aviso).</summary>
    public async Task<ContextoDeAviso?> CargarContextoAsync(Aviso aviso, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aviso);

        var nombre = await db.Monitores.AsNoTracking().Where(m => m.Id == aviso.MonitorId).Select(m => m.Nombre).FirstOrDefaultAsync(cancellationToken);
        var incidente = await db.Incidentes.AsNoTracking().FirstOrDefaultAsync(i => i.Id == aviso.IncidenteId, cancellationToken);

        return nombre is null || incidente is null ? null : new ContextoDeAviso(nombre, incidente);
    }

    public Task GuardarAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    /// <summary>Borra los avisos enviados hace más de <paramref name="antesDe"/> y los abandonados creados antes de esa fecha.</summary>
    public Task<int> PurgarAsync(DateTimeOffset antesDe, CancellationToken cancellationToken)
    {
        var limite = antesDe.ToUniversalTime();

        return db.Avisos
            .Where(aviso => (aviso.EnviadoEn != null && aviso.EnviadoEn < limite) || (aviso.Abandonado && aviso.CreadoEn < limite))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
