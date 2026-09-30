using Microsoft.EntityFrameworkCore;

using Npgsql;

using Shouldly;

using Vigia.Datos.Persistencia;
using Vigia.Dominio.Monitores;
using Vigia.Dominio.Seguimiento;

namespace Vigia.Datos.Tests;

public class ParticionesTests : BaseDeDatosTest
{
    private async Task<Guid> MonitorAsync() => (await GuardarMonitorAsync()).Id;

    private static ResultadoEntidad Resultado(Guid monitorId, DateTimeOffset momento) =>
        new() { MonitorId = monitorId, Momento = momento, Correcto = true, LatenciaMs = 80 };

    [Fact]
    public async Task Asegurar_crea_el_mes_anterior_el_actual_y_los_tres_siguientes_y_repetirlo_no_falla()
    {
        await using var db = NuevoContexto();
        var particiones = new Particiones(db);

        await particiones.AsegurarAsync(Ahora, Cancelacion);
        await particiones.AsegurarAsync(Ahora, Cancelacion);

        var meses = await particiones.ListarAsync(Cancelacion);
        meses.ShouldContain(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        meses.ShouldContain(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        meses.ShouldContain(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        meses.Count(m => m >= new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) && m <= new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)).ShouldBe(5);
    }

    [Fact]
    public async Task Un_resultado_cae_en_la_particion_de_su_mes()
    {
        var monitor = await MonitorAsync();
        await using (var db = NuevoContexto())
        {
            await new Particiones(db).AsegurarAsync(Ahora, Cancelacion);
            db.Resultados.AddRange(
                Resultado(monitor, new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero)),
                Resultado(monitor, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
                Resultado(monitor, new DateTimeOffset(2026, 10, 31, 23, 59, 59, TimeSpan.Zero)));
            await db.SaveChangesAsync(Cancelacion);
        }

        await using var lectura = NuevoContexto();
        var reparto = await lectura.Database
            .SqlQuery<string>($"SELECT tableoid::regclass::text || ':' || count(*) AS \"Value\" FROM resultados GROUP BY tableoid ORDER BY 1")
            .ToListAsync(Cancelacion);

        reparto.ShouldBe(["resultados_2026_09:1", "resultados_2026_10:2"]);
    }

    [Fact]
    public async Task Sin_particion_para_un_mes_escribir_falla_y_por_eso_se_crean_por_adelantado()
    {
        var monitor = await MonitorAsync();
        await using var db = NuevoContexto();
        db.Resultados.Add(Resultado(monitor, new DateTimeOffset(2031, 3, 15, 12, 0, 0, TimeSpan.Zero)));

        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancelacion));

        ((PostgresException)error.InnerException!).SqlState.ShouldBe("23514", "no partition of relation found for row");
    }

    [Fact]
    public async Task Una_consulta_de_un_solo_mes_solo_lee_la_particion_de_ese_mes()
    {
        await using var db = NuevoContexto();
        await new Particiones(db).AsegurarAsync(Ahora, Cancelacion);

        var plan = string.Join('\n', await db.Database
            .SqlQuery<string>($"EXPLAIN SELECT * FROM resultados WHERE momento >= '2026-10-05 00:00:00+00' AND momento < '2026-10-06 00:00:00+00'")
            .ToListAsync(Cancelacion));

        plan.ShouldContain("resultados_2026_10");
        plan.ShouldNotContain("resultados_2026_09", customMessage: "el resto de particiones se descartan sin mirarlas");
        plan.ShouldNotContain("resultados_2026_11");
    }

    [Fact]
    public async Task La_retencion_elimina_las_particiones_enteras_antiguas_y_las_filas_viejas_de_la_partida_por_el_limite()
    {
        var monitor = await MonitorAsync();
        await using var db = NuevoContexto();
        var particiones = new Particiones(db);
        await particiones.AsegurarAsync(Ahora, Cancelacion);
        await particiones.CrearAsync(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), Cancelacion);
        db.Resultados.AddRange(
            Resultado(monitor, new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)), // partición vieja entera
            Resultado(monitor, new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero)), // septiembre termina antes del límite: entera
            Resultado(monitor, new DateTimeOffset(2026, 10, 1, 5, 0, 0, TimeSpan.Zero)), // octubre queda partida por el límite: esta es vieja
            Resultado(monitor, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)), // y esta ya no
            Resultado(monitor, new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync(Cancelacion);

        var limite = Ahora.AddDays(-14); // 1 de octubre a las 10:30

        var eliminadas = await particiones.AplicarRetencionAsync(limite, Cancelacion);

        eliminadas.ShouldBe(["resultados_2026_07", "resultados_2026_09"]);
        var meses = await particiones.ListarAsync(Cancelacion);
        meses.ShouldNotContain(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        meses.ShouldNotContain(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        meses.ShouldContain(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        await using var lectura = NuevoContexto();
        (await lectura.Resultados.Select(r => r.Momento).OrderBy(m => m).ToListAsync(Cancelacion)).ShouldBe(
        [
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero),
        ]);
    }

    [Fact]
    public async Task Borrar_una_particion_es_instantaneo_aunque_tenga_muchas_filas()
    {
        var monitor = await MonitorAsync();
        await using var db = NuevoContexto();
        var particiones = new Particiones(db);
        await particiones.CrearAsync(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), Cancelacion);

        await db.Database.ExecuteSqlAsync($"INSERT INTO resultados SELECT gen_random_uuid(), {monitor}, '2026-03-05 00:00:00+00'::timestamptz + (g || ' seconds')::interval, true, 80, 0, NULL, NULL, false FROM generate_series(1, 200000) g", Cancelacion);
        (await db.Resultados.CountAsync(Cancelacion)).ShouldBe(200_000);

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        await particiones.AplicarRetencionAsync(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), Cancelacion);
        reloj.Stop();

        (await db.Resultados.CountAsync(Cancelacion)).ShouldBe(0);
        reloj.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3), "un DROP TABLE no recorre las filas, un DELETE de 200.000 sí");
    }

    [Fact]
    public void El_nombre_de_una_particion_y_el_inicio_de_un_mes_son_los_esperados()
    {
        Particiones.NombreDe(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero)).ShouldBe("resultados_2026_10");
        Particiones.NombreDe(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)).ShouldBe("resultados_2027_01");
        Particiones.InicioDelMes(new DateTimeOffset(2026, 10, 31, 23, 59, 59, TimeSpan.FromHours(2))).ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    }
}

public class RepositorioSeguimientoTests : BaseDeDatosTest
{
    private static readonly ReglasDeSeguimiento Reglas = new(3, TimeSpan.FromSeconds(2));

    private DateTimeOffset _momento = Ahora;

    /// <summary>Una comprobación completa: carga el seguimiento, lo aplica y guarda todo en un contexto nuevo (como haría un proceso distinto cada vez).</summary>
    private async Task<IReadOnlyList<EventoDeSeguimiento>> ComprobarAsync(Guid monitorId, bool correcto, string? error = null, int latenciaMs = 80)
    {
        _momento = _momento.AddMinutes(1);

        await using var db = NuevoContexto();
        var repositorio = new RepositorioSeguimiento(db);

        var seguimiento = await repositorio.CargarAsync(monitorId, _momento, Cancelacion);
        var eventos = seguimiento.Registrar(new Observacion(correcto, TimeSpan.FromMilliseconds(latenciaMs), error, _momento), Reglas);
        var resultado = new ResultadoEntidad { MonitorId = monitorId, Momento = _momento, Correcto = correcto, LatenciaMs = latenciaMs, Fallo = (short)(correcto ? 0 : 1), Error = error };

        await new Particiones(db).AsegurarAsync(_momento, Cancelacion);
        await repositorio.GuardarAsync(seguimiento, eventos, resultado, Cancelacion);

        return eventos;
    }

    [Fact]
    public async Task Un_monitor_sin_seguimiento_empieza_desconocido()
    {
        var monitor = await GuardarMonitorAsync();

        await using var db = NuevoContexto();
        var seguimiento = await new RepositorioSeguimiento(db).CargarAsync(monitor.Id, Ahora, Cancelacion);

        seguimiento.Estado.ShouldBe(EstadoMonitor.Desconocido);
        seguimiento.IncidenteAbierto.ShouldBeNull();
    }

    [Fact]
    public async Task El_estado_sobrevive_entre_procesos_un_incidente_se_abre_una_vez_y_se_cierra_al_recuperarse()
    {
        var monitor = await GuardarMonitorAsync();

        await ComprobarAsync(monitor.Id, true);
        await ComprobarAsync(monitor.Id, false, "tiempo agotado");
        await ComprobarAsync(monitor.Id, false, "tiempo agotado");
        var apertura = await ComprobarAsync(monitor.Id, false, "conexión rechazada");
        await ComprobarAsync(monitor.Id, false, "conexión rechazada otra vez");

        apertura.OfType<IncidenteAbierto>().Count().ShouldBe(1);

        await using (var db = NuevoContexto())
        {
            var incidente = await db.Incidentes.SingleAsync(Cancelacion);
            incidente.EstaAbierto.ShouldBeTrue();
            incidente.Fallos.ShouldBe(4);
            incidente.Causa.ShouldBe("conexión rechazada otra vez", "la causa es el último error");
            (await new RepositorioSeguimiento(db).CargarAsync(monitor.Id, _momento, Cancelacion)).Estado.ShouldBe(EstadoMonitor.Caido);
        }

        var cierre = await ComprobarAsync(monitor.Id, true);

        cierre.OfType<IncidenteCerrado>().Count().ShouldBe(1);
        await using var lectura = NuevoContexto();
        var cerrado = await lectura.Incidentes.SingleAsync(Cancelacion);
        cerrado.EstaAbierto.ShouldBeFalse();
        cerrado.CerradoPor.ShouldBe(MotivoDeCierre.Recuperado);
        cerrado.Duracion(_momento).ShouldBe(TimeSpan.FromMinutes(2), "del tercer fallo (minuto 4) al éxito (minuto 6)");
        (await lectura.Seguimientos.SingleAsync(Cancelacion)).IncidenteAbiertoId.ShouldBeNull();
    }

    [Fact]
    public async Task Cada_comprobacion_deja_su_resultado_y_cada_cambio_de_estado_su_registro()
    {
        var monitor = await GuardarMonitorAsync();

        await ComprobarAsync(monitor.Id, true); // desconocido → operativo
        await ComprobarAsync(monitor.Id, true);
        await ComprobarAsync(monitor.Id, false, "error"); // operativo → sospechoso
        await ComprobarAsync(monitor.Id, true); // sospechoso → operativo

        await using var db = NuevoContexto();
        (await db.Resultados.CountAsync(Cancelacion)).ShouldBe(4);
        var cambios = await db.CambiosDeEstado.OrderBy(c => c.Momento).Select(c => new { c.Anterior, c.Nuevo }).ToListAsync(Cancelacion);
        cambios.Select(c => (c.Anterior, c.Nuevo)).ShouldBe(
        [
            (EstadoMonitor.Desconocido, EstadoMonitor.Operativo),
            (EstadoMonitor.Operativo, EstadoMonitor.Sospechoso),
            (EstadoMonitor.Sospechoso, EstadoMonitor.Operativo),
        ]);
    }

    [Fact]
    public async Task Una_respuesta_lenta_deja_el_monitor_degradado_y_se_guarda()
    {
        var monitor = await GuardarMonitorAsync();

        await ComprobarAsync(monitor.Id, true, latenciaMs: 3000);

        await using var db = NuevoContexto();
        (await db.Seguimientos.SingleAsync(Cancelacion)).Estado.ShouldBe(EstadoMonitor.Degradado);
    }

    [Fact]
    public async Task Si_el_guardado_falla_no_queda_nada_a_medias()
    {
        var monitor = await GuardarMonitorAsync();
        await ComprobarAsync(monitor.Id, true);
        await ComprobarAsync(monitor.Id, false, "uno");
        await ComprobarAsync(monitor.Id, false, "dos");

        // La tercera abriría un incidente, pero el resultado no se puede guardar (error demasiado largo para la columna).
        _momento = _momento.AddMinutes(1);
        await using (var db = NuevoContexto())
        {
            var repositorio = new RepositorioSeguimiento(db);
            var seguimiento = await repositorio.CargarAsync(monitor.Id, _momento, Cancelacion);
            var eventos = seguimiento.Registrar(new Observacion(false, TimeSpan.FromSeconds(1), "tres", _momento), Reglas);
            eventos.OfType<IncidenteAbierto>().Count().ShouldBe(1);
            var invalido = new ResultadoEntidad { MonitorId = monitor.Id, Momento = _momento, Correcto = false, Fallo = 1, Error = new string('x', 600) };

            await Should.ThrowAsync<DbUpdateException>(() => repositorio.GuardarAsync(seguimiento, eventos, invalido, Cancelacion));
        }

        await using var lectura = NuevoContexto();
        (await lectura.Incidentes.CountAsync(Cancelacion)).ShouldBe(0, "ni incidente");
        (await lectura.Resultados.CountAsync(Cancelacion)).ShouldBe(3, "ni resultado");
        (await lectura.Seguimientos.SingleAsync(Cancelacion)).Estado.ShouldBe(EstadoMonitor.Sospechoso, "el estado guardado sigue siendo el anterior");
    }

    [Fact]
    public async Task Un_error_muy_largo_se_recorta_en_la_causa_del_incidente()
    {
        var monitor = await GuardarMonitorAsync();

        await ComprobarAsync(monitor.Id, false, new string('e', 400));
        await ComprobarAsync(monitor.Id, false, new string('e', 400));
        await ComprobarAsync(monitor.Id, false, new string('e', 400));

        await using var db = NuevoContexto();
        (await db.Incidentes.SingleAsync(Cancelacion)).Causa.Length.ShouldBe(400);

        var muyLargo = new string('x', 900);
        var incidente = Incidente.Abrir(monitor.Id, Ahora, muyLargo, 1);
        incidente.Causa.Length.ShouldBe(Incidente.LongitudMaximaCausa);
        incidente.RegistrarFallo(muyLargo);
        incidente.Causa.Length.ShouldBe(Incidente.LongitudMaximaCausa);
    }

    [Fact]
    public async Task Al_entrar_en_mantenimiento_estando_caido_el_incidente_se_cierra_por_mantenimiento_y_se_guarda()
    {
        var monitor = await GuardarMonitorAsync();
        await ComprobarAsync(monitor.Id, false, "a");
        await ComprobarAsync(monitor.Id, false, "b");
        await ComprobarAsync(monitor.Id, false, "c");

        _momento = _momento.AddMinutes(1);
        await using (var db = NuevoContexto())
        {
            var repositorio = new RepositorioSeguimiento(db);
            var seguimiento = await repositorio.CargarAsync(monitor.Id, _momento, Cancelacion);
            var eventos = seguimiento.ActualizarMantenimiento(true, _momento);
            await repositorio.GuardarAsync(seguimiento, eventos, new ResultadoEntidad { MonitorId = monitor.Id, Momento = _momento, EnMantenimiento = true, Fallo = 1 }, Cancelacion);
        }

        await using var lectura = NuevoContexto();
        var incidente = await lectura.Incidentes.SingleAsync(Cancelacion);
        incidente.CerradoPor.ShouldBe(MotivoDeCierre.Mantenimiento);
        (await lectura.Seguimientos.SingleAsync(Cancelacion)).Estado.ShouldBe(EstadoMonitor.Mantenimiento);
    }

    [Fact]
    public async Task Al_borrar_un_monitor_se_borra_todo_lo_suyo()
    {
        var monitor = await GuardarMonitorAsync();
        await ComprobarAsync(monitor.Id, false, "x");
        await ComprobarAsync(monitor.Id, false, "x");
        await ComprobarAsync(monitor.Id, false, "x");

        await using (var db = NuevoContexto())
        {
            await db.Monitores.Where(m => m.Id == monitor.Id).ExecuteDeleteAsync(Cancelacion);
        }

        await using var lectura = NuevoContexto();
        (await lectura.Seguimientos.CountAsync(Cancelacion)).ShouldBe(0);
        (await lectura.Incidentes.CountAsync(Cancelacion)).ShouldBe(0);
        (await lectura.CambiosDeEstado.CountAsync(Cancelacion)).ShouldBe(0);
        (await lectura.Resultados.CountAsync(Cancelacion)).ShouldBe(0);
    }
}
