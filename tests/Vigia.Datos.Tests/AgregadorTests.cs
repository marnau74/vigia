using Microsoft.EntityFrameworkCore;

using Shouldly;

using Vigia.Datos.Persistencia;
using Vigia.Dominio.Disponibilidad;
using Vigia.Dominio.Monitores;

namespace Vigia.Datos.Tests;

public class AgregadorTests : BaseDeDatosTest
{
    private static readonly DateTimeOffset Dia = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Hora10 = Dia.AddHours(10);
    private static readonly int[] MinutosBuenos = [0, 3, 6, 9, 12, 15, 18, 36, 39, 51, 54, 57];
    private static readonly int[] MinutosMalos = [21, 24, 27, 30, 33];

    private async Task CambioAsync(Guid monitorId, DateTimeOffset momento, EstadoMonitor anterior, EstadoMonitor nuevo)
    {
        await using var db = NuevoContexto();
        db.CambiosDeEstado.Add(new CambioDeEstadoEntidad { MonitorId = monitorId, Momento = momento, Anterior = anterior, Nuevo = nuevo });
        await db.SaveChangesAsync(Cancelacion);
    }

    private async Task ResultadosAsync(Guid monitorId, params (DateTimeOffset Momento, bool Correcto, int LatenciaMs, bool Mantenimiento)[] filas)
    {
        await using var db = NuevoContexto();
        db.Resultados.AddRange(filas.Select(f => new ResultadoEntidad
        {
            MonitorId = monitorId,
            Momento = f.Momento,
            Correcto = f.Correcto,
            LatenciaMs = f.LatenciaMs,
            Fallo = (short)(f.Correcto ? 0 : 1),
            EnMantenimiento = f.Mantenimiento,
        }));
        await db.SaveChangesAsync(Cancelacion);
    }

    /// <summary>
    /// Comprobaciones cada dos minutos en [desde, hasta): el monitor se vigiló todo ese tiempo (con un intervalo de un
    /// minuto, cada una vale tres). Correctas, salvo que <paramref name="correcto"/> diga otra cosa para su momento.
    /// </summary>
    private Task VigiladoAsync(Guid monitorId, DateTimeOffset desde, DateTimeOffset hasta, Func<DateTimeOffset, bool>? correcto = null, int latenciaMs = 400, bool mantenimiento = false)
    {
        var momentos = new List<DateTimeOffset>();

        for (var momento = desde; momento < hasta; momento = momento.AddMinutes(2))
        {
            momentos.Add(momento);
        }

        return ResultadosAsync(monitorId, [.. momentos.Select(m => (m, correcto?.Invoke(m) ?? true, latenciaMs, mantenimiento))]);
    }

    private async Task AgregarHoraAsync(DateTimeOffset hora)
    {
        await using var db = NuevoContexto();
        await new Agregador(db).AgregarHoraAsync(hora, Cancelacion);
    }

    /// <summary>
    /// La hora de las 10, vigilada entera (una comprobación cada tres minutos): 45 minutos operativo y 15 caído;
    /// doce comprobaciones buenas (100 a 1200 ms), cinco fallidas y tres en mantenimiento.
    /// </summary>
    private async Task<Vigia.Dominio.Monitores.Monitor> PrepararHoraAsync()
    {
        var monitor = await GuardarMonitorAsync();

        await CambioAsync(monitor.Id, Dia.AddHours(9).AddMinutes(55), EstadoMonitor.Desconocido, EstadoMonitor.Operativo);
        await CambioAsync(monitor.Id, Hora10.AddMinutes(20), EstadoMonitor.Operativo, EstadoMonitor.Caido);
        await CambioAsync(monitor.Id, Hora10.AddMinutes(35), EstadoMonitor.Caido, EstadoMonitor.Operativo);

        var buenas = MinutosBuenos.Select((minuto, i) => (Hora10.AddMinutes(minuto), true, (i + 1) * 100, false));
        var malas = MinutosMalos.Select(minuto => (Hora10.AddMinutes(minuto), false, 10000, false));
        var mantenimiento = new[] { (Hora10.AddMinutes(42), false, 9999, true), (Hora10.AddMinutes(45), true, 9999, true), (Hora10.AddMinutes(48), true, 9999, true) };
        await ResultadosAsync(monitor.Id, [.. buenas, .. malas, .. mantenimiento]);

        return monitor;
    }

    [Fact]
    public async Task La_hora_resume_el_tiempo_en_cada_estado_y_las_estadisticas_de_las_comprobaciones()
    {
        var monitor = await PrepararHoraAsync();

        await AgregarHoraAsync(Hora10);

        await using var db = NuevoContexto();
        var fila = await db.ResultadosHora.SingleAsync(Cancelacion);
        fila.MonitorId.ShouldBe(monitor.Id);
        fila.Periodo.ShouldBe(Hora10);
        fila.SegOperativo.ShouldBe(45 * 60);
        fila.SegCaido.ShouldBe(15 * 60);
        fila.SegDesconocido.ShouldBe(0);
        fila.SegMantenimiento.ShouldBe(0);
        fila.Correctas.ShouldBe(12);
        fila.Fallidas.ShouldBe(5);
        fila.Comprobaciones.ShouldBe(17);
        fila.EnMantenimiento.ShouldBe(3, "las hechas en mantenimiento no cuentan como correctas ni como fallidas");
        fila.LatenciaMediaMs.ShouldBe(650);
        fila.P50Ms.ShouldBe(600);
        fila.P95Ms.ShouldBe(1200);
    }

    [Fact]
    public async Task La_latencia_solo_se_calcula_con_las_comprobaciones_correctas_y_no_las_de_mantenimiento()
    {
        await PrepararHoraAsync();

        await AgregarHoraAsync(Hora10);

        await using var db = NuevoContexto();
        var fila = await db.ResultadosHora.SingleAsync(Cancelacion);
        fila.P95Ms.ShouldNotBe(10000, "un fallo por tiempo agotado no dice nada de lo rápido que va el servicio");
        fila.P95Ms.ShouldNotBe(9999, "ni lo que pasó durante un mantenimiento");
    }

    [Fact]
    public async Task La_disponibilidad_de_la_hora_sale_del_mismo_calculo_del_dominio()
    {
        await PrepararHoraAsync();

        await AgregarHoraAsync(Hora10);

        await using var db = NuevoContexto();
        var fila = await db.ResultadosHora.SingleAsync(Cancelacion);
        CalculadoraDisponibilidad.Calcular(fila.Tiempos)!.Value.Fraccion.ShouldBe(0.75m);
    }

    [Fact]
    public async Task Recalcular_una_hora_es_idempotente_y_recoge_lo_nuevo()
    {
        var monitor = await PrepararHoraAsync();
        await AgregarHoraAsync(Hora10);
        await AgregarHoraAsync(Hora10);

        await using (var db = NuevoContexto())
        {
            (await db.ResultadosHora.CountAsync(Cancelacion)).ShouldBe(1);
        }

        await ResultadosAsync(monitor.Id, (Hora10.AddMinutes(50), true, 1100, false));
        await AgregarHoraAsync(Hora10);

        await using var lectura = NuevoContexto();
        var fila = await lectura.ResultadosHora.SingleAsync(Cancelacion);
        fila.Correctas.ShouldBe(13);
    }

    [Fact]
    public async Task Sin_cambios_ni_resultados_la_hora_entera_es_desconocida()
    {
        await GuardarMonitorAsync();

        await AgregarHoraAsync(Hora10);

        await using var db = NuevoContexto();
        var fila = await db.ResultadosHora.SingleAsync(Cancelacion);
        fila.SegDesconocido.ShouldBe(3600);
        fila.Comprobaciones.ShouldBe(0);
        fila.LatenciaMediaMs.ShouldBeNull();
        fila.P95Ms.ShouldBeNull();
        CalculadoraDisponibilidad.Calcular(fila.Tiempos).ShouldBeNull("sin datos no hay disponibilidad, no un 100 % inventado");
    }

    [Fact]
    public async Task Un_monitor_creado_despues_de_la_hora_no_tiene_agregado_de_esa_hora()
    {
        await GuardarMonitorAsync(creado: Hora10.AddHours(2));

        await AgregarHoraAsync(Hora10);

        await using var db = NuevoContexto();
        (await db.ResultadosHora.CountAsync(Cancelacion)).ShouldBe(0);
    }

    [Fact]
    public async Task El_estado_al_empezar_la_hora_es_el_del_ultimo_cambio_anterior_aunque_sea_de_hace_dias()
    {
        var monitor = await GuardarMonitorAsync();
        await CambioAsync(monitor.Id, Dia.AddDays(-3), EstadoMonitor.Desconocido, EstadoMonitor.Caido);
        await VigiladoAsync(monitor.Id, Hora10, Hora10.AddHours(1), correcto: _ => false);

        await AgregarHoraAsync(Hora10);

        await using var db = NuevoContexto();
        (await db.ResultadosHora.SingleAsync(Cancelacion)).SegCaido.ShouldBe(3600);
    }

    [Fact]
    public async Task Sin_comprobaciones_el_ultimo_estado_no_se_alarga_y_la_hora_es_desconocida()
    {
        // Operativo desde hace días, pero pausado (o con el worker parado): nadie ha mirado en toda la hora.
        var monitor = await GuardarMonitorAsync();
        await CambioAsync(monitor.Id, Dia.AddDays(-3), EstadoMonitor.Desconocido, EstadoMonitor.Operativo);

        await AgregarHoraAsync(Hora10);

        await using var db = NuevoContexto();
        var fila = await db.ResultadosHora.SingleAsync(Cancelacion);
        fila.SegOperativo.ShouldBe(0);
        fila.SegDesconocido.ShouldBe(3600);
        CalculadoraDisponibilidad.Calcular(fila.Tiempos).ShouldBeNull("sin vigilar no hay disponibilidad que enseñar, ni buena ni mala");
    }

    [Fact]
    public async Task Si_se_deja_de_vigilar_a_mitad_de_hora_solo_cuenta_hasta_que_vence_la_ultima_comprobacion()
    {
        var monitor = await GuardarMonitorAsync();
        await CambioAsync(monitor.Id, Hora10.AddMinutes(-5), EstadoMonitor.Desconocido, EstadoMonitor.Caido);
        await VigiladoAsync(monitor.Id, Hora10.AddMinutes(-4), Hora10.AddMinutes(20), correcto: _ => false);

        await AgregarHoraAsync(Hora10);

        await using var db = NuevoContexto();
        var fila = await db.ResultadosHora.SingleAsync(Cancelacion);
        fila.SegCaido.ShouldBe(21 * 60, "la última comprobación, del minuto 18, vale tres minutos: hasta el 21");
        fila.SegDesconocido.ShouldBe(39 * 60);
    }

    [Fact]
    public async Task El_mantenimiento_se_cuenta_como_tiempo_de_mantenimiento_y_no_penaliza()
    {
        var monitor = await GuardarMonitorAsync();
        await CambioAsync(monitor.Id, Hora10.AddMinutes(-5), EstadoMonitor.Desconocido, EstadoMonitor.Operativo);
        await CambioAsync(monitor.Id, Hora10.AddMinutes(30), EstadoMonitor.Operativo, EstadoMonitor.Mantenimiento);
        await VigiladoAsync(monitor.Id, Hora10, Hora10.AddMinutes(30));
        await VigiladoAsync(monitor.Id, Hora10.AddMinutes(30), Hora10.AddHours(1), mantenimiento: true);

        await AgregarHoraAsync(Hora10);

        await using var db = NuevoContexto();
        var fila = await db.ResultadosHora.SingleAsync(Cancelacion);
        fila.SegOperativo.ShouldBe(30 * 60);
        fila.SegMantenimiento.ShouldBe(30 * 60);
        CalculadoraDisponibilidad.Calcular(fila.Tiempos)!.Value.Fraccion.ShouldBe(1m);
    }

    [Fact]
    public async Task Los_agregados_de_varios_monitores_no_se_mezclan()
    {
        var uno = await GuardarMonitorAsync("Uno");
        var otro = await GuardarMonitorAsync("Otro");
        await CambioAsync(uno.Id, Hora10.AddMinutes(-1), EstadoMonitor.Desconocido, EstadoMonitor.Operativo);
        await CambioAsync(otro.Id, Hora10.AddMinutes(-1), EstadoMonitor.Desconocido, EstadoMonitor.Caido);
        await VigiladoAsync(uno.Id, Hora10, Hora10.AddHours(1), latenciaMs: 100);
        await VigiladoAsync(otro.Id, Hora10, Hora10.AddHours(1), correcto: _ => false);

        await AgregarHoraAsync(Hora10);

        await using var db = NuevoContexto();
        var filas = await db.ResultadosHora.ToDictionaryAsync(f => f.MonitorId, Cancelacion);
        filas[uno.Id].SegOperativo.ShouldBe(3600);
        filas[uno.Id].Correctas.ShouldBe(30);
        filas[uno.Id].Fallidas.ShouldBe(0);
        filas[otro.Id].SegCaido.ShouldBe(3600);
        filas[otro.Id].Fallidas.ShouldBe(30);
    }

    // --- Días y pendientes -------------------------------------------------------------------

    [Fact]
    public async Task Los_pendientes_agregan_todas_las_horas_completas_y_los_dias_que_quedan_completos()
    {
        var monitor = await GuardarMonitorAsync();
        await CambioAsync(monitor.Id, Dia, EstadoMonitor.Desconocido, EstadoMonitor.Operativo);
        await CambioAsync(monitor.Id, Dia.AddHours(30), EstadoMonitor.Operativo, EstadoMonitor.Caido); // 6 h del día 11
        await CambioAsync(monitor.Id, Dia.AddHours(36), EstadoMonitor.Caido, EstadoMonitor.Operativo);
        await ResultadosAsync(monitor.Id, (Dia.AddHours(25).AddMinutes(1), true, 300, false), (Dia.AddHours(26).AddMinutes(1), true, 500, false));

        // Vigilado desde el día 10 a la 01:00, con comprobaciones correctas de 400 ms salvo mientras estuvo caído.
        await VigiladoAsync(monitor.Id, Dia.AddHours(1), Dia.AddDays(2), correcto: m => m < Dia.AddHours(30) || m >= Dia.AddHours(36));

        await using (var db = NuevoContexto())
        {
            var (horas, dias) = await new Agregador(db).AgregarPendientesAsync(Dia.AddDays(2).AddMinutes(30), Cancelacion);

            horas.ShouldBe(47, "desde el día 10 a las 01:00 (hora del primer resultado) hasta las 23:00 del día 11");
            dias.ShouldBeGreaterThanOrEqualTo(1);
        }

        await using var lectura = NuevoContexto();
        var dia11 = await lectura.ResultadosDia.SingleAsync(d => d.Periodo == Dia.AddDays(1), Cancelacion);
        dia11.SegCaido.ShouldBe(6 * 3600);
        dia11.SegOperativo.ShouldBe(18 * 3600);
        dia11.SegDesconocido.ShouldBe(0);
        dia11.Correctas.ShouldBe((18 * 30) + 2);
        dia11.Fallidas.ShouldBe(6 * 30);
        dia11.LatenciaMediaMs!.Value.ShouldBe(400, 0.001);
        CalculadoraDisponibilidad.Calcular(dia11.Tiempos)!.Value.Fraccion.ShouldBe(0.75m);
    }

    [Fact]
    public async Task Volver_a_ejecutar_los_pendientes_no_repite_trabajo_y_la_hora_siguiente_se_agrega_cuando_termina()
    {
        var monitor = await GuardarMonitorAsync();
        await CambioAsync(monitor.Id, Dia, EstadoMonitor.Desconocido, EstadoMonitor.Operativo);
        await ResultadosAsync(monitor.Id, (Dia.AddHours(1), true, 100, false));

        await using var db = NuevoContexto();
        var agregador = new Agregador(db);
        var primera = await agregador.AgregarPendientesAsync(Dia.AddHours(3).AddMinutes(10), Cancelacion);
        var repetida = await agregador.AgregarPendientesAsync(Dia.AddHours(3).AddMinutes(20), Cancelacion);
        var siguiente = await agregador.AgregarPendientesAsync(Dia.AddHours(4).AddMinutes(1), Cancelacion);

        primera.Horas.ShouldBe(2, "las horas 01 y 02; la 03 sigue en curso");
        repetida.ShouldBe((0, 0));
        siguiente.Horas.ShouldBe(1);
    }

    [Fact]
    public async Task La_hora_en_curso_no_se_agrega_hasta_que_termina()
    {
        var monitor = await GuardarMonitorAsync();
        await CambioAsync(monitor.Id, Dia, EstadoMonitor.Desconocido, EstadoMonitor.Operativo);
        await ResultadosAsync(monitor.Id, (Dia.AddHours(5).AddMinutes(10), true, 100, false));

        await using var db = NuevoContexto();
        await new Agregador(db).AgregarPendientesAsync(Dia.AddHours(5).AddMinutes(30), Cancelacion);

        (await db.ResultadosHora.CountAsync(Cancelacion)).ShouldBe(0);
    }

    [Fact]
    public async Task La_retencion_borra_las_horas_de_hace_mas_de_noventa_dias_y_conserva_los_dias()
    {
        var monitor = await GuardarMonitorAsync(creado: Ahora.AddYears(-1));
        await using var db = NuevoContexto();
        db.ResultadosHora.AddRange(
            new AgregadoHora { MonitorId = monitor.Id, Periodo = Ahora.AddDays(-91) },
            new AgregadoHora { MonitorId = monitor.Id, Periodo = Ahora.AddDays(-89) });
        db.ResultadosDia.Add(new AgregadoDia { MonitorId = monitor.Id, Periodo = Ahora.AddDays(-200) });
        await db.SaveChangesAsync(Cancelacion);

        var borradas = await new Agregador(db).PurgarHorasAntiguasAsync(Ahora, Cancelacion);

        borradas.ShouldBe(1);
        (await db.ResultadosHora.CountAsync(Cancelacion)).ShouldBe(1);
        (await db.ResultadosDia.CountAsync(Cancelacion)).ShouldBe(1);
    }

    [Fact]
    public void Las_horas_y_los_dias_se_truncan_en_utc()
    {
        Agregador.InicioDeHora(new DateTimeOffset(2026, 10, 10, 10, 47, 12, TimeSpan.FromHours(2))).ShouldBe(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));
        Agregador.InicioDeDia(new DateTimeOffset(2026, 10, 10, 1, 30, 0, TimeSpan.FromHours(2))).ShouldBe(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
    }
}
