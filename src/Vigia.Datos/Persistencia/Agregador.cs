using Microsoft.EntityFrameworkCore;

using Vigia.Dominio.Disponibilidad;
using Vigia.Dominio.Monitores;

namespace Vigia.Datos.Persistencia;

/// <summary>
/// Resume las comprobaciones en agregados por hora y por día. Sin ellos, enseñar «la disponibilidad de
/// los últimos 90 días» obligaría a recorrer millones de filas de <c>resultados</c> (y esas filas se
/// borran a los 14 días). Con ellos, son 90 filas por monitor.
/// </summary>
/// <remarks>
/// El tiempo pasado en cada estado sale del registro de cambios de estado, con el mismo código del dominio
/// que se usa en los tests (<see cref="TiemposPorEstado"/>): no hay una segunda implementación en SQL que
/// pueda dar otro resultado. Un estado solo cuenta mientras lo respalda alguna comprobación (cada una vale tres
/// intervalos): el tiempo de un monitor pausado o de un worker parado es «desconocido», no el último estado visto.
/// Todo es idempotente: recalcular una hora da la misma fila (mientras se conserven sus comprobaciones, 14 días).
/// Las estadísticas de latencia se calculan solo sobre las comprobaciones correctas (un fallo por
/// tiempo agotado «tarda» diez segundos y no dice nada sobre la velocidad del servicio) y quedan
/// fuera las hechas durante un mantenimiento.
/// </remarks>
public sealed class Agregador(VigiaDbContext db)
{
    /// <summary>Cuánto se conservan los agregados por hora (los de por día no se borran).</summary>
    public static readonly TimeSpan RetencionHoras = TimeSpan.FromDays(90);

    public static DateTimeOffset InicioDeHora(DateTimeOffset momento)
    {
        var utc = momento.UtcDateTime;

        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    public static DateTimeOffset InicioDeDia(DateTimeOffset momento)
    {
        var utc = momento.UtcDateTime;

        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Calcula (o recalcula) el agregado de una hora para todos los monitores que ya existían en ella.</summary>
    public async Task AgregarHoraAsync(DateTimeOffset hora, CancellationToken cancellationToken)
    {
        var inicio = InicioDeHora(hora);
        var fin = inicio.AddHours(1);

        var monitores = await db.Monitores.AsNoTracking().Where(m => m.CreadoEn < fin).Select(m => new { m.Id, m.Intervalo }).ToListAsync(cancellationToken);

        foreach (var monitor in monitores)
        {
            await AgregarHoraDeAsync(monitor.Id, monitor.Intervalo * Dominio.Monitores.Monitor.ComprobacionesToleradas, inicio, fin, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <param name="vigencia">Cuánto vale cada comprobación (<see cref="Dominio.Monitores.Monitor.VigenciaDeUnaComprobacion"/>).</param>
    private async Task AgregarHoraDeAsync(Guid monitorId, TimeSpan vigencia, DateTimeOffset inicio, DateTimeOffset fin, CancellationToken cancellationToken)
    {
        // Estado al empezar la hora: el del último cambio anterior (o desconocido si no hubo ninguno).
        var estadoInicial = await db.CambiosDeEstado
            .Where(c => c.MonitorId == monitorId && c.Momento <= inicio)
            .OrderByDescending(c => c.Momento)
            .Select(c => (EstadoMonitor?)c.Nuevo)
            .FirstOrDefaultAsync(cancellationToken) ?? EstadoMonitor.Desconocido;

        var cambiosDentro = await db.CambiosDeEstado
            .Where(c => c.MonitorId == monitorId && c.Momento > inicio && c.Momento < fin)
            .OrderBy(c => c.Momento)
            .Select(c => new { c.Nuevo, c.Momento })
            .ToListAsync(cancellationToken);

        var cambios = new List<(EstadoMonitor Estado, DateTimeOffset Desde)> { (estadoInicial, inicio) };
        cambios.AddRange(cambiosDentro.Select(c => (c.Nuevo, c.Momento)));

        // Una comprobación de antes de la hora todavía cubre su principio, si no ha vencido.
        var desdeVigentes = inicio - vigencia;
        var conVigentes = await db.Resultados.AsNoTracking()
            .Where(r => r.MonitorId == monitorId && r.Momento >= desdeVigentes && r.Momento < fin)
            .Select(r => new { r.Momento, r.Correcto, r.LatenciaMs, r.EnMantenimiento })
            .ToListAsync(cancellationToken);

        // El estado solo cuenta mientras alguna comprobación lo respalda: lo demás es tiempo sin vigilar (desconocido).
        var tiempos = TiemposPorEstado.DeTramosVigilados(TiemposPorEstado.TramosDe(cambios, fin), conVigentes.Select(r => r.Momento), vigencia, inicio, fin);

        var resultados = conVigentes.Where(r => r.Momento >= inicio).ToList();

        var contadas = resultados.Where(r => !r.EnMantenimiento).ToList();
        var latencias = contadas.Where(r => r.Correcto).Select(r => TimeSpan.FromMilliseconds(r.LatenciaMs)).ToList();

        var fila = await db.ResultadosHora.FindAsync([monitorId, inicio], cancellationToken);

        if (fila is null)
        {
            fila = new AgregadoHora { MonitorId = monitorId, Periodo = inicio };
            db.ResultadosHora.Add(fila);
        }

        fila.PonerTiempos(tiempos);
        fila.Correctas = latencias.Count;
        fila.Fallidas = contadas.Count - latencias.Count;
        fila.Comprobaciones = contadas.Count;
        fila.EnMantenimiento = resultados.Count - contadas.Count;
        fila.LatenciaMediaMs = Estadistica.Media(latencias)?.TotalMilliseconds;
        fila.P50Ms = Estadistica.Percentil(latencias, 50)?.TotalMilliseconds;
        fila.P95Ms = Estadistica.Percentil(latencias, 95)?.TotalMilliseconds;
    }

    /// <summary>Calcula (o recalcula) el agregado de un día a partir de los de sus horas.</summary>
    public async Task AgregarDiaAsync(DateTimeOffset dia, CancellationToken cancellationToken)
    {
        var inicio = InicioDeDia(dia);
        var fin = inicio.AddDays(1);

        var horas = await db.ResultadosHora.AsNoTracking()
            .Where(h => h.Periodo >= inicio && h.Periodo < fin)
            .ToListAsync(cancellationToken);

        foreach (var deMonitor in horas.GroupBy(h => h.MonitorId))
        {
            var fila = await db.ResultadosDia.FindAsync([deMonitor.Key, inicio], cancellationToken);

            if (fila is null)
            {
                fila = new AgregadoDia { MonitorId = deMonitor.Key, Periodo = inicio };
                db.ResultadosDia.Add(fila);
            }

            var correctas = deMonitor.Sum(h => h.Correctas);

            fila.PonerTiempos(TiemposPorEstado.Sumar(deMonitor.Select(h => h.Tiempos)));
            fila.Correctas = correctas;
            fila.Fallidas = deMonitor.Sum(h => h.Fallidas);
            fila.Comprobaciones = deMonitor.Sum(h => h.Comprobaciones);
            fila.EnMantenimiento = deMonitor.Sum(h => h.EnMantenimiento);

            // La media del día es la de las medias de las horas, ponderada por las comprobaciones correctas de cada una.
            fila.LatenciaMediaMs = correctas == 0
                ? null
                : deMonitor.Where(h => h.LatenciaMediaMs is not null).Sum(h => h.LatenciaMediaMs!.Value * h.Correctas) / correctas;
            fila.PeorP95HoraMs = deMonitor.Max(h => h.P95Ms);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Agrega todas las horas completas que faltan y los días que quedan completos, hasta <paramref name="ahora"/>.
    /// Se puede llamar cuantas veces se quiera: solo trabaja donde falta trabajo. Devuelve (horas, días) calculados.
    /// </summary>
    public async Task<(int Horas, int Dias)> AgregarPendientesAsync(DateTimeOffset ahora, CancellationToken cancellationToken)
    {
        var horaActual = InicioDeHora(ahora);

        var ultimaAgregada = await db.ResultadosHora.MaxAsync(h => (DateTimeOffset?)h.Periodo, cancellationToken);
        DateTimeOffset primera;

        if (ultimaAgregada is { } ultima)
        {
            primera = ultima.AddHours(1);
        }
        else
        {
            // Primera vez: se empieza por la hora del resultado más antiguo que exista.
            var primerResultado = await db.Resultados.MinAsync(r => (DateTimeOffset?)r.Momento, cancellationToken);
            primera = primerResultado is { } momento ? InicioDeHora(momento) : horaActual;
        }

        var horas = 0;

        for (var hora = primera; hora < horaActual; hora = hora.AddHours(1))
        {
            await AgregarHoraAsync(hora, cancellationToken);
            horas++;
        }

        // Un día está completo cuando ya se han agregado todas sus horas: su última hora es anterior a la hora actual.
        var dias = 0;

        if (horas > 0)
        {
            for (var dia = InicioDeDia(primera); dia.AddDays(1) <= horaActual; dia = dia.AddDays(1))
            {
                await AgregarDiaAsync(dia, cancellationToken);
                dias++;
            }
        }

        return (horas, dias);
    }

    /// <summary>Borra los agregados por hora anteriores a la retención (90 días). Los de por día se conservan.</summary>
    public async Task<int> PurgarHorasAntiguasAsync(DateTimeOffset ahora, CancellationToken cancellationToken)
    {
        var limite = ahora - RetencionHoras;

        return await db.ResultadosHora.Where(h => h.Periodo < limite).ExecuteDeleteAsync(cancellationToken);
    }
}
