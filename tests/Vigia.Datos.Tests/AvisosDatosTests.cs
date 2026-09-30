using Microsoft.EntityFrameworkCore;

using Npgsql;

using Shouldly;

using Vigia.Datos.Persistencia;
using Vigia.Dominio.Avisos;
using Vigia.Dominio.Seguimiento;

using MonitorDeDominio = Vigia.Dominio.Monitores.Monitor;

namespace Vigia.Datos.Tests;

public class AvisosDatosTests : BaseDeDatosTest
{
    private static readonly ReglasDeSeguimiento Reglas = new(3, null);
    private static readonly DestinoDeAviso Correo = new(CanalAviso.Correo, "guardia@ejemplo.com");
    private static readonly DestinoDeAviso Telegram = new(CanalAviso.Telegram, "99");

    private DateTimeOffset _momento = Ahora;

    /// <summary>Una comprobación completa con los avisos que provoca, guardada como lo hace el worker.</summary>
    private async Task<IReadOnlyList<EventoDeSeguimiento>> ComprobarAsync(Guid monitorId, bool correcto, params DestinoDeAviso[] destinos)
    {
        _momento = _momento.AddMinutes(1);

        await using var db = NuevoContexto();
        var repositorio = new RepositorioSeguimiento(db);

        var seguimiento = await repositorio.CargarAsync(monitorId, _momento, Cancelacion);
        var eventos = seguimiento.Registrar(new Observacion(correcto, TimeSpan.FromMilliseconds(80), correcto ? null : "sin respuesta", _momento), Reglas);
        var resultado = new ResultadoEntidad { MonitorId = monitorId, Momento = _momento, Correcto = correcto, Fallo = (short)(correcto ? 0 : 1) };

        await new Particiones(db).AsegurarAsync(_momento, Cancelacion);
        await repositorio.GuardarAsync(seguimiento, eventos, resultado, PlanDeAvisos.Crear(eventos, destinos, _momento), Cancelacion);

        return eventos;
    }

    private async Task<MonitorDeDominio> CaidoAsync(string nombre, params DestinoDeAviso[] destinos)
    {
        var monitor = await GuardarMonitorAsync(nombre);

        for (var i = 0; i < 3; i++)
        {
            await ComprobarAsync(monitor.Id, false, destinos);
        }

        return monitor;
    }

    // --- Se guardan con el cambio de estado ---------------------------------------------------------

    [Fact]
    public async Task Abrir_un_incidente_guarda_sus_avisos_en_la_misma_operacion()
    {
        var monitor = await CaidoAsync("Mi web", Correo, Telegram);

        await using var db = NuevoContexto();
        var incidente = await db.Incidentes.SingleAsync(Cancelacion);
        var avisos = await db.Avisos.ToListAsync(Cancelacion);

        avisos.Count.ShouldBe(2);
        avisos.ShouldAllBe(a => a.IncidenteId == incidente.Id && a.MonitorId == monitor.Id && a.Tipo == TipoAviso.Caida);
        avisos.Select(a => a.Canal).Order().ShouldBe([CanalAviso.Correo, CanalAviso.Telegram]);
        avisos.ShouldAllBe(a => a.EstaPendiente && a.Intentos == 0);
    }

    [Fact]
    public async Task Cerrar_el_incidente_añade_los_avisos_de_recuperacion_y_no_repite_los_de_caida()
    {
        var monitor = await CaidoAsync("Mi web", Correo);
        await ComprobarAsync(monitor.Id, false, Correo);
        await ComprobarAsync(monitor.Id, true, Correo);

        await using var db = NuevoContexto();
        (await db.Avisos.OrderBy(a => a.CreadoEn).Select(a => a.Tipo).ToListAsync(Cancelacion)).ShouldBe([TipoAviso.Caida, TipoAviso.Recuperacion]);
    }

    [Fact]
    public async Task Si_el_guardado_falla_no_queda_ni_el_incidente_ni_el_aviso()
    {
        var monitor = await GuardarMonitorAsync();
        await ComprobarAsync(monitor.Id, false, Correo);
        await ComprobarAsync(monitor.Id, false, Correo);

        _momento = _momento.AddMinutes(1);
        await using (var db = NuevoContexto())
        {
            var repositorio = new RepositorioSeguimiento(db);
            var seguimiento = await repositorio.CargarAsync(monitor.Id, _momento, Cancelacion);
            var eventos = seguimiento.Registrar(new Observacion(false, TimeSpan.FromSeconds(1), "tres", _momento), Reglas);
            var invalido = new ResultadoEntidad { MonitorId = monitor.Id, Momento = _momento, Fallo = 1, Error = new string('x', 600) };

            await Should.ThrowAsync<DbUpdateException>(() => repositorio.GuardarAsync(seguimiento, eventos, invalido, PlanDeAvisos.Crear(eventos, [Correo], _momento), Cancelacion));
        }

        await using var lectura = NuevoContexto();
        (await lectura.Incidentes.CountAsync(Cancelacion)).ShouldBe(0);
        (await lectura.Avisos.CountAsync(Cancelacion)).ShouldBe(0, "un aviso sin su incidente no puede existir");
    }

    // --- La base de datos impide los duplicados --------------------------------------------------------------

    [Fact]
    public async Task La_base_de_datos_rechaza_un_segundo_aviso_igual_aunque_el_codigo_se_equivocara()
    {
        var monitor = await CaidoAsync("Mi web", Correo);
        await using var db = NuevoContexto();
        var incidente = await db.Incidentes.SingleAsync(Cancelacion);

        db.Avisos.Add(Aviso.Crear(incidente.Id, monitor.Id, TipoAviso.Caida, Correo, Ahora));

        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancelacion));
        ((PostgresException)error.InnerException!).ConstraintName.ShouldBe("ux_avisos_sin_duplicados");
    }

    [Fact]
    public async Task El_mismo_tipo_a_otro_destino_o_por_otro_canal_si_se_permite()
    {
        var monitor = await CaidoAsync("Mi web", Correo);
        await using var db = NuevoContexto();
        var incidente = await db.Incidentes.SingleAsync(Cancelacion);

        db.Avisos.Add(Aviso.Crear(incidente.Id, monitor.Id, TipoAviso.Caida, new DestinoDeAviso(CanalAviso.Correo, "otra@ejemplo.com"), Ahora));
        db.Avisos.Add(Aviso.Crear(incidente.Id, monitor.Id, TipoAviso.Caida, Telegram, Ahora));

        await db.SaveChangesAsync(Cancelacion);
        (await db.Avisos.CountAsync(Cancelacion)).ShouldBe(3);
    }

    [Fact]
    public async Task Existen_el_indice_unico_y_el_de_pendientes_que_es_parcial()
    {
        var unico = await ConsultarAsync<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_avisos_sin_duplicados'");
        unico.ShouldContain("UNIQUE");
        unico.ShouldContain("incidente_id, tipo, canal, destino");

        var pendientes = await ConsultarAsync<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_avisos_pendientes'");
        pendientes.ShouldContain("WHERE");
        pendientes.ShouldContain("enviado_en IS NULL");
    }

    // --- Reclamar -------------------------------------------------------------------------------------------

    private async Task<List<Aviso>> CrearAvisosAsync(int cantidad, DateTimeOffset? proximoIntento = null)
    {
        var monitor = await GuardarMonitorAsync($"Monitor {Guid.NewGuid():N}");
        await using var db = NuevoContexto();
        var incidente = Incidente.Abrir(monitor.Id, Ahora, "causa", 3);
        db.Incidentes.Add(incidente);

        var avisos = Enumerable.Range(0, cantidad)
            .Select(i => Aviso.Crear(incidente.Id, monitor.Id, TipoAviso.Caida, new DestinoDeAviso(CanalAviso.Correo, $"a{i}@ejemplo.com"), Ahora))
            .ToList();

        if (proximoIntento is { } cuando)
        {
            avisos.ForEach(a => a.Reservar(cuando));
        }

        db.Avisos.AddRange(avisos);
        await db.SaveChangesAsync(Cancelacion);

        return avisos;
    }

    [Fact]
    public async Task Reclamar_devuelve_solo_los_que_ya_tocan_y_los_aparta_durante_la_reserva()
    {
        await CrearAvisosAsync(3);
        await CrearAvisosAsync(2, Ahora.AddHours(1));

        await using var db = NuevoContexto();
        var repositorio = new RepositorioAvisos(db);

        var primeros = await repositorio.ReclamarAsync(Ahora, TimeSpan.FromMinutes(2), 10, Cancelacion);
        var segundos = await repositorio.ReclamarAsync(Ahora, TimeSpan.FromMinutes(2), 10, Cancelacion);
        var tarde = await repositorio.ReclamarAsync(Ahora.AddMinutes(3), TimeSpan.FromMinutes(2), 10, Cancelacion);

        primeros.Count.ShouldBe(3);
        segundos.ShouldBeEmpty("ya están reservados");
        tarde.Count.ShouldBe(3, "la reserva venció y vuelven a tocar; los futuros aún no");
    }

    [Fact]
    public async Task Reclamar_respeta_el_maximo_y_el_orden_de_creacion()
    {
        await CrearAvisosAsync(5);

        await using var db = NuevoContexto();
        var lote = await new RepositorioAvisos(db).ReclamarAsync(Ahora, TimeSpan.FromMinutes(2), 2, Cancelacion);

        lote.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Los_enviados_y_los_abandonados_no_se_reclaman()
    {
        var avisos = await CrearAvisosAsync(3);

        await using (var db = NuevoContexto())
        {
            var guardados = await db.Avisos.ToListAsync(Cancelacion);
            guardados.First(a => a.Id == avisos[0].Id).MarcarEnviado(Ahora);

            var abandonado = guardados.First(a => a.Id == avisos[1].Id);
            for (var i = 0; i < Aviso.MaximoIntentos; i++)
            {
                abandonado.RegistrarFallo("x", Ahora);
            }

            abandonado.Reservar(Ahora);
            await db.SaveChangesAsync(Cancelacion);
        }

        await using var lectura = NuevoContexto();
        var lote = await new RepositorioAvisos(lectura).ReclamarAsync(Ahora.AddDays(1), TimeSpan.FromMinutes(2), 10, Cancelacion);

        lote.ShouldHaveSingleItem().Id.ShouldBe(avisos[2].Id);
    }

    [Fact]
    public async Task Dos_procesos_que_reclaman_a_la_vez_reciben_avisos_distintos()
    {
        await CrearAvisosAsync(60);

        async Task<List<Guid>> Reclamar()
        {
            await using var db = NuevoContexto();
            var ids = new List<Guid>();

            while (true)
            {
                var lote = await new RepositorioAvisos(db).ReclamarAsync(Ahora, TimeSpan.FromMinutes(5), 7, Cancelacion);

                if (lote.Count == 0)
                {
                    return ids;
                }

                ids.AddRange(lote.Select(a => a.Id));
            }
        }

        var resultados = await Task.WhenAll(Reclamar(), Reclamar(), Reclamar());
        var todos = resultados.SelectMany(r => r).ToList();

        todos.Count.ShouldBe(60, "no se pierde ninguno");
        todos.Distinct().Count().ShouldBe(60, "y nadie recibe uno que ya tiene otro");
    }

    // --- Contexto y purga ---------------------------------------------------------------------------------

    [Fact]
    public async Task El_contexto_trae_el_nombre_del_monitor_y_el_incidente_actuales()
    {
        var monitor = await CaidoAsync("Mi web", Correo);
        await ComprobarAsync(monitor.Id, true, Correo);

        await using var db = NuevoContexto();
        var aviso = await db.Avisos.FirstAsync(a => a.Tipo == TipoAviso.Recuperacion, Cancelacion);

        var contexto = await new RepositorioAvisos(db).CargarContextoAsync(aviso, Cancelacion);

        contexto.ShouldNotBeNull().NombreDelMonitor.ShouldBe("Mi web");
        contexto.Incidente.EstaAbierto.ShouldBeFalse();
        contexto.Incidente.Causa.ShouldBe("sin respuesta");
    }

    [Fact]
    public async Task Borrar_el_monitor_borra_sus_incidentes_y_sus_avisos()
    {
        var monitor = await CaidoAsync("Mi web", Correo, Telegram);

        await using (var db = NuevoContexto())
        {
            await db.Monitores.Where(m => m.Id == monitor.Id).ExecuteDeleteAsync(Cancelacion);
        }

        await using var lectura = NuevoContexto();
        (await lectura.Avisos.CountAsync(Cancelacion)).ShouldBe(0);
        (await lectura.Incidentes.CountAsync(Cancelacion)).ShouldBe(0);
    }

    [Fact]
    public async Task La_purga_borra_enviados_y_abandonados_antiguos_y_respeta_los_pendientes_y_los_recientes()
    {
        var avisos = await CrearAvisosAsync(4);

        await using (var db = NuevoContexto())
        {
            var guardados = await db.Avisos.ToListAsync(Cancelacion);
            guardados.First(a => a.Id == avisos[0].Id).MarcarEnviado(Ahora.AddDays(-40));
            guardados.First(a => a.Id == avisos[1].Id).MarcarEnviado(Ahora.AddDays(-2));

            var abandonado = guardados.First(a => a.Id == avisos[2].Id);
            for (var i = 0; i < Aviso.MaximoIntentos; i++)
            {
                abandonado.RegistrarFallo("x", Ahora);
            }

            await db.SaveChangesAsync(Cancelacion);
        }

        await using var purga = NuevoContexto();
        var borrados = await new RepositorioAvisos(purga).PurgarAsync(Ahora.AddDays(-30), Cancelacion);
        borrados.ShouldBe(1, "solo el enviado hace 40 días: el abandonado es de hoy");

        var borradosMasTarde = await new RepositorioAvisos(purga).PurgarAsync(Ahora.AddDays(1), Cancelacion);
        borradosMasTarde.ShouldBe(2, "el enviado reciente y el abandonado; el pendiente nunca");

        (await purga.Avisos.Select(a => a.Id).ToListAsync(Cancelacion)).ShouldBe([avisos[3].Id]);
    }
}
