using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Vigia.Contratos;
using Vigia.Datos.Persistencia;
using Vigia.Dominio.Disponibilidad;
using Vigia.Dominio.Monitores;

using MonitorDeDominio = Vigia.Dominio.Monitores.Monitor;

namespace Vigia.Api.Consultas;

/// <summary>
/// Lo que lee el panel: estado actual, latencia, disponibilidad e incidentes. Las disponibilidades salen
/// de los agregados por hora y por día, no de la tabla grande de resultados (ADR 0004), y se calculan con
/// el mismo código del dominio que el resto del programa.
/// </summary>
/// <remarks>
/// Los agregados se calculan al terminar cada hora, así que las cifras van con hasta una hora de retraso
/// (las barras de hoy suman las horas ya cerradas de hoy). Es el precio de que consultar 90 días sea leer
/// 90 filas por monitor y no millones.
/// </remarks>
public sealed class ConsultasDePanel(VigiaDbContext db, TimeProvider reloj)
{
    public const int MaximoDeFilasDeResultados = 5_000;

    public static readonly TimeSpan VentanaMaximaDeResultados = TimeSpan.FromDays(14);

    // --- Monitores --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<MonitorDto>> ListarMonitoresAsync(Guid? grupoId, CancellationToken cancellationToken)
    {
        var consulta = db.Monitores.AsNoTracking();

        if (grupoId is { } grupo)
        {
            consulta = consulta.Where(m => m.GrupoId == grupo);
        }

        return await ComponerAsync(await consulta.OrderBy(m => m.Nombre).ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<MonitorDto?> ObtenerMonitorAsync(Guid id, CancellationToken cancellationToken)
    {
        var monitor = await db.Monitores.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

        return monitor is null ? null : (await ComponerAsync([monitor], cancellationToken)).Single();
    }

    private async Task<IReadOnlyList<MonitorDto>> ComponerAsync(List<MonitorDeDominio> monitores, CancellationToken cancellationToken)
    {
        if (monitores.Count == 0)
        {
            return [];
        }

        var ids = monitores.Select(m => m.Id).ToList();
        var ahora = reloj.GetUtcNow();

        var seguimientos = await db.Seguimientos.AsNoTracking().Where(s => ids.Contains(s.MonitorId)).ToDictionaryAsync(s => s.MonitorId, cancellationToken);
        var abiertos = await db.Incidentes.AsNoTracking().Where(i => ids.Contains(i.MonitorId) && i.CerradoEn == null).ToDictionaryAsync(i => i.MonitorId, cancellationToken);
        var latencias = await UltimasLatenciasAsync(ids, ahora, cancellationToken);
        var disponibilidades = await DisponibilidadPorMonitorAsync(db.ResultadosHora.AsNoTracking().Where(h => ids.Contains(h.MonitorId) && h.Periodo >= ahora.AddDays(-30)), cancellationToken);

        return
        [
            .. monitores.Select(monitor =>
            {
                seguimientos.TryGetValue(monitor.Id, out var seguimiento);
                abiertos.TryGetValue(monitor.Id, out var incidente);
                latencias.TryGetValue(monitor.Id, out var latencia);
                disponibilidades.TryGetValue(monitor.Id, out var disponibilidad);

                return new MonitorDto(
                    monitor.Id,
                    monitor.Nombre,
                    monitor.Tipo.ToString(),
                    ConfiguracionComoJson(monitor.Configuracion),
                    (int)monitor.Intervalo.TotalSeconds,
                    monitor.FallosParaIncidente,
                    monitor.UmbralLento is { } umbral ? (int)umbral.TotalMilliseconds : null,
                    monitor.GrupoId,
                    monitor.Activo,
                    seguimiento?.Estado ?? EstadoMonitor.Desconocido,
                    seguimiento?.Desde,
                    seguimiento?.UltimaObservacion,
                    latencia,
                    disponibilidad,
                    incidente is null ? null : new IncidenteAbiertoDto(incidente.Id, incidente.AbiertoEn, incidente.Causa, incidente.Fallos));
            }),
        ];
    }

    public static JsonElement ConfiguracionComoJson(ConfiguracionMonitor configuracion)
    {
        using var documento = JsonDocument.Parse(ConversorConfiguracion.Serializar(configuracion));

        return documento.RootElement.Clone();
    }

    /// <summary>La latencia de la última comprobación de cada monitor, si fue correcta (la de un fallo es el tiempo que se esperó, no dice nada).</summary>
    private async Task<Dictionary<Guid, int?>> UltimasLatenciasAsync(IReadOnlyList<Guid> ids, DateTimeOffset ahora, CancellationToken cancellationToken)
    {
        // Solo se mira el último día: acota las particiones que se leen y un monitor sin datos recientes no enseña una latencia vieja.
        var desde = ahora.AddDays(-1);

        var filas = await db.Database.SqlQuery<UltimaLatencia>($"""
            SELECT DISTINCT ON (monitor_id) monitor_id, latencia_ms, correcto
            FROM resultados
            WHERE momento >= {desde} AND monitor_id = ANY({ids.ToArray()})
            ORDER BY monitor_id, momento DESC
            """).ToListAsync(cancellationToken);

        return filas.ToDictionary(f => f.MonitorId, f => f.Correcto ? (int?)f.LatenciaMs : null);
    }

    private sealed record UltimaLatencia(Guid MonitorId, int LatenciaMs, bool Correcto);

    // --- Disponibilidad ---------------------------------------------------------------------------------

    private static decimal? Porcentaje(TiemposPorEstado tiempos) => CalculadoraDisponibilidad.Calcular(tiempos)?.Porcentaje(2);

    /// <summary>Suma los agregados de cada monitor en la base de datos (una fila por monitor, no miles) y calcula su disponibilidad.</summary>
    private static async Task<Dictionary<Guid, decimal?>> DisponibilidadPorMonitorAsync<T>(IQueryable<T> agregados, CancellationToken cancellationToken)
        where T : AgregadoDeResultados
    {
        var sumas = await agregados
            .GroupBy(a => a.MonitorId)
            .Select(g => new
            {
                MonitorId = g.Key,
                Operativo = g.Sum(a => a.SegOperativo),
                Degradado = g.Sum(a => a.SegDegradado),
                Sospechoso = g.Sum(a => a.SegSospechoso),
                Caido = g.Sum(a => a.SegCaido),
            })
            .ToListAsync(cancellationToken);

        return sumas.ToDictionary(
            s => s.MonitorId,
            s => Porcentaje(TiemposPorEstado.De(
            [
                (EstadoMonitor.Operativo, TimeSpan.FromSeconds(s.Operativo)),
                (EstadoMonitor.Degradado, TimeSpan.FromSeconds(s.Degradado)),
                (EstadoMonitor.Sospechoso, TimeSpan.FromSeconds(s.Sospechoso)),
                (EstadoMonitor.Caido, TimeSpan.FromSeconds(s.Caido)),
            ])));
    }

    public async Task<DisponibilidadDto> DisponibilidadAsync(Guid monitorId, CancellationToken cancellationToken)
    {
        var ahora = reloj.GetUtcNow();

        async Task<decimal?> Horas(int dias) =>
            (await DisponibilidadPorMonitorAsync(db.ResultadosHora.AsNoTracking().Where(h => h.MonitorId == monitorId && h.Periodo >= ahora.AddDays(-dias)), cancellationToken)).GetValueOrDefault(monitorId);

        // Las consultas van una tras otra: un DbContext no admite dos a la vez.
        var dia = await Horas(1);
        var semana = await Horas(7);
        var mes = await Horas(30);
        var trimestre = (await DisponibilidadPorMonitorAsync(
            db.ResultadosDia.AsNoTracking().Where(d => d.MonitorId == monitorId && d.Periodo >= Agregador.InicioDeDia(ahora).AddDays(-90)),
            cancellationToken)).GetValueOrDefault(monitorId);

        return new DisponibilidadDto(dia, semana, mes, trimestre);
    }

    /// <summary>Una barra por día de los últimos <paramref name="dias"/> (incluido hoy, con las horas ya cerradas), con los días sin datos como huecos.</summary>
    public async Task<IReadOnlyList<BarraDiaria>> BarrasAsync(Guid monitorId, int dias, CancellationToken cancellationToken) =>
        (await BarrasAsync([monitorId], dias, cancellationToken)).GetValueOrDefault(monitorId) ?? [];

    public async Task<Dictionary<Guid, IReadOnlyList<BarraDiaria>>> BarrasAsync(IReadOnlyCollection<Guid> monitorIds, int dias, CancellationToken cancellationToken)
    {
        var hoy = Agregador.InicioDeDia(reloj.GetUtcNow());
        var primero = hoy.AddDays(-(dias - 1));
        var lista = monitorIds.ToList();

        var completos = await db.ResultadosDia.AsNoTracking().Where(d => lista.Contains(d.MonitorId) && d.Periodo >= primero && d.Periodo < hoy).ToListAsync(cancellationToken);
        var deHoy = await db.ResultadosHora.AsNoTracking().Where(h => lista.Contains(h.MonitorId) && h.Periodo >= hoy).ToListAsync(cancellationToken);

        return lista.ToDictionary(
            id => id,
            id =>
            {
                var porDia = completos.Where(d => d.MonitorId == id).ToDictionary(d => d.Periodo, d => (AgregadoDeResultados)d);
                var horasDeHoy = deHoy.Where(h => h.MonitorId == id).ToList();

                if (horasDeHoy.Count > 0)
                {
                    porDia[hoy] = Resumir(id, hoy, horasDeHoy);
                }

                return (IReadOnlyList<BarraDiaria>)[.. Enumerable.Range(0, dias).Select(i => Barra(primero.AddDays(i), porDia))];
            });
    }

    private static AgregadoDia Resumir(Guid monitorId, DateTimeOffset dia, IReadOnlyList<AgregadoHora> horas)
    {
        var resumen = new AgregadoDia { MonitorId = monitorId, Periodo = dia };
        resumen.PonerTiempos(TiemposPorEstado.Sumar(horas.Select(h => h.Tiempos)));

        return resumen;
    }

    private static BarraDiaria Barra(DateTimeOffset dia, Dictionary<DateTimeOffset, AgregadoDeResultados> porDia)
    {
        var fecha = DateOnly.FromDateTime(dia.UtcDateTime);

        if (!porDia.TryGetValue(dia, out var agregado))
        {
            return new BarraDiaria(fecha, null, 0, null);
        }

        EstadoMonitor? peor = agregado.SegCaido > 0 ? EstadoMonitor.Caido
            : agregado.SegDegradado > 0 ? EstadoMonitor.Degradado
            : agregado.SegOperativo > 0 ? EstadoMonitor.Operativo
            : agregado.SegMantenimiento > 0 ? EstadoMonitor.Mantenimiento
            : null;

        return new BarraDiaria(fecha, Porcentaje(agregado.Tiempos), agregado.SegCaido, peor);
    }

    // --- Latencia y resultados ---------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PuntoDeLatencia>> LatenciaAsync(Guid monitorId, int horas, CancellationToken cancellationToken)
    {
        var desde = reloj.GetUtcNow().AddHours(-horas);
        var filas = await db.ResultadosHora.AsNoTracking().Where(h => h.MonitorId == monitorId && h.Periodo >= desde).OrderBy(h => h.Periodo).ToListAsync(cancellationToken);

        return [.. filas.Select(h => new PuntoDeLatencia(h.Periodo, h.LatenciaMediaMs, h.P50Ms, h.P95Ms, h.Correctas, h.Fallidas, Porcentaje(h.Tiempos)))];
    }

    public async Task<IReadOnlyList<ResultadoDto>> ResultadosAsync(Guid monitorId, DateTimeOffset desde, DateTimeOffset hasta, int limite, CancellationToken cancellationToken)
    {
        var filas = await db.Resultados.AsNoTracking()
            .Where(r => r.MonitorId == monitorId && r.Momento >= desde && r.Momento < hasta)
            .OrderByDescending(r => r.Momento)
            .Take(Math.Min(limite, MaximoDeFilasDeResultados))
            .ToListAsync(cancellationToken);

        return [.. filas.Select(r => new ResultadoDto(r.Momento, r.Correcto, r.LatenciaMs, r.Fallo == 0 ? null : ((Vigia.Comprobaciones.TipoFallo)r.Fallo).ToString(), r.Error, r.EnMantenimiento))];
    }

    // --- Incidentes ---------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<IncidenteDto>> IncidentesAsync(Guid? monitorId, bool soloAbiertos, int limite, CancellationToken cancellationToken)
    {
        var ahora = reloj.GetUtcNow();

        var consulta = from i in db.Incidentes.AsNoTracking()
                       join m in db.Monitores.AsNoTracking() on i.MonitorId equals m.Id
                       select new { Incidente = i, Monitor = m.Nombre };

        if (monitorId is { } id)
        {
            consulta = consulta.Where(x => x.Incidente.MonitorId == id);
        }

        if (soloAbiertos)
        {
            consulta = consulta.Where(x => x.Incidente.CerradoEn == null);
        }

        var filas = await consulta.OrderByDescending(x => x.Incidente.AbiertoEn).Take(limite).ToListAsync(cancellationToken);

        return
        [
            .. filas.Select(x => new IncidenteDto(
                x.Incidente.Id,
                x.Incidente.MonitorId,
                x.Monitor,
                x.Incidente.AbiertoEn,
                x.Incidente.CerradoEn,
                x.Incidente.CerradoPor?.ToString(),
                x.Incidente.Duracion(ahora).TotalSeconds,
                x.Incidente.Fallos,
                x.Incidente.Causa)),
        ];
    }

    // --- La página de estado pública ------------------------------------------------------------------------------

    public const int DiasDeLaPaginaPublica = 90;

    /// <summary>
    /// El estado de un grupo público, con solo lo que se puede enseñar a cualquiera: nombres de servicio, estado,
    /// disponibilidad y cuándo hubo incidentes. Nada de direcciones, configuraciones ni mensajes de error.
    /// Un grupo que no existe o no es público es indistinguible: los dos devuelven <c>null</c>.
    /// </summary>
    public async Task<EstadoPublico?> PaginaPublicaAsync(string slug, CancellationToken cancellationToken)
    {
        var grupo = await db.Grupos.AsNoTracking().FirstOrDefaultAsync(g => g.Slug == slug && g.Publico, cancellationToken);

        if (grupo is null)
        {
            return null;
        }

        var ahora = reloj.GetUtcNow();
        var monitores = await db.Monitores.AsNoTracking().Where(m => m.GrupoId == grupo.Id && m.Activo).OrderBy(m => m.Nombre).ToListAsync(cancellationToken);
        var ids = monitores.Select(m => m.Id).ToList();

        var estados = await db.Seguimientos.AsNoTracking().Where(s => ids.Contains(s.MonitorId)).ToDictionaryAsync(s => s.MonitorId, s => s.Estado, cancellationToken);
        var barras = await BarrasAsync(ids, DiasDeLaPaginaPublica, cancellationToken);
        var disponibilidad = await DisponibilidadPorMonitorAsync(
            db.ResultadosDia.AsNoTracking().Where(d => ids.Contains(d.MonitorId) && d.Periodo >= Agregador.InicioDeDia(ahora).AddDays(-DiasDeLaPaginaPublica)),
            cancellationToken);

        var servicios = monitores.Select(m =>
        {
            var estado = estados.GetValueOrDefault(m.Id, EstadoMonitor.Desconocido);

            return new ServicioPublico(m.Nombre, estado, disponibilidad.GetValueOrDefault(m.Id), barras[m.Id]);
        }).ToList();

        var desde = ahora.AddDays(-30);
        var incidentes = await (from i in db.Incidentes.AsNoTracking()
                                join m in db.Monitores.AsNoTracking() on i.MonitorId equals m.Id
                                where ids.Contains(i.MonitorId) && (i.CerradoEn == null || i.CerradoEn >= desde)
                                orderby i.AbiertoEn descending
                                select new { i.AbiertoEn, i.CerradoEn, Servicio = m.Nombre })
            .Take(20)
            .ToListAsync(cancellationToken);

        return new EstadoPublico(
            grupo.Nombre,
            grupo.Slug,
            EstadoGeneralDe.Calcular([.. servicios.Select(s => s.Estado)]),
            ahora,
            servicios,
            [.. incidentes.Select(i => new IncidentePublico(i.Servicio, i.AbiertoEn, i.CerradoEn, ((i.CerradoEn ?? ahora) - i.AbiertoEn).TotalSeconds))]);
    }
}
