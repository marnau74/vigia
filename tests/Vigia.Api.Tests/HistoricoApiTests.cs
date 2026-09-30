using System.Net;
using System.Text.Json;

using Shouldly;

using Vigia.Datos.Persistencia;
using Vigia.Dominio.Monitores;
using Vigia.Dominio.Seguimiento;

namespace Vigia.Api.Tests;

public class HistoricoApiTests : BaseApiTest
{
    private static readonly DateTimeOffset Ahora = FabricaDeApi.Inicio;
    private static readonly DateTimeOffset Hoy = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    private async Task SembrarAsync(Action<VigiaDbContext> preparar)
    {
        await using var db = Fabrica.NuevoContexto();
        preparar(db);
        await db.SaveChangesAsync(Cancelacion);
    }

    private async Task<JsonElement> GetAsync(string ruta)
    {
        using var respuesta = await Cliente.GetAsync(ruta, Cancelacion);
        respuesta.StatusCode.ShouldBe(HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync(Cancelacion));

        return await LeerAsync(respuesta);
    }

    private async Task<HttpStatusCode> EstadoDeAsync(string ruta)
    {
        using var respuesta = await Cliente.GetAsync(ruta, Cancelacion);

        return respuesta.StatusCode;
    }

    // --- Resultados -------------------------------------------------------------------------------

    [Fact]
    public async Task Los_resultados_por_defecto_son_las_ultimas_24_horas_de_mas_nuevo_a_mas_viejo()
    {
        var m = await MonitorEnBaseDeDatosAsync();
        await SembrarAsync(db => db.Resultados.AddRange(
            new ResultadoEntidad { MonitorId = m.Id, Momento = Ahora.AddHours(-1), Correcto = true, LatenciaMs = 100 },
            new ResultadoEntidad { MonitorId = m.Id, Momento = Ahora.AddHours(-2), Correcto = false, LatenciaMs = 10000, Fallo = 1, Error = "Sin respuesta en 10 s." },
            new ResultadoEntidad { MonitorId = m.Id, Momento = Ahora.AddHours(-30), Correcto = true, LatenciaMs = 300 },
            new ResultadoEntidad { MonitorId = m.Id, Momento = Ahora.AddHours(-3), Correcto = true, LatenciaMs = 50, EnMantenimiento = true }));

        var resultados = (await GetAsync($"/api/monitores/{m.Id}/resultados")).EnumerateArray().ToList();

        resultados.Select(r => r.GetProperty("latenciaMs").GetInt32()).ShouldBe([100, 10000, 50]);
        resultados[1].GetProperty("correcto").GetBoolean().ShouldBeFalse();
        resultados[1].GetProperty("fallo").GetString().ShouldBe("TiempoAgotado");
        resultados[1].GetProperty("error").GetString().ShouldBe("Sin respuesta en 10 s.");
        resultados[0].GetProperty("fallo").ValueKind.ShouldBe(JsonValueKind.Null);
        resultados[2].GetProperty("enMantenimiento").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Los_resultados_se_pueden_acotar_por_fechas_y_por_cantidad()
    {
        var m = await MonitorEnBaseDeDatosAsync();
        await SembrarAsync(db => db.Resultados.AddRange(Enumerable.Range(1, 10).Select(i => new ResultadoEntidad { MonitorId = m.Id, Momento = Ahora.AddHours(-i), Correcto = true, LatenciaMs = i })));
        var desde = Uri.EscapeDataString(Ahora.AddHours(-6).ToString("o"));
        var hasta = Uri.EscapeDataString(Ahora.AddHours(-2).ToString("o"));

        var rango = (await GetAsync($"/api/monitores/{m.Id}/resultados?desde={desde}&hasta={hasta}")).EnumerateArray().Select(r => r.GetProperty("latenciaMs").GetInt32());
        var limitados = (await GetAsync($"/api/monitores/{m.Id}/resultados?limite=3")).EnumerateArray().Select(r => r.GetProperty("latenciaMs").GetInt32());

        rango.ShouldBe([3, 4, 5, 6], "«desde» incluye y «hasta» no");
        limitados.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Los_resultados_de_un_monitor_no_incluyen_los_de_otro()
    {
        var uno = await MonitorEnBaseDeDatosAsync("Uno");
        var otro = await MonitorEnBaseDeDatosAsync("Otro");
        await SembrarAsync(db => db.Resultados.AddRange(
            new ResultadoEntidad { MonitorId = uno.Id, Momento = Ahora.AddHours(-1), Correcto = true, LatenciaMs = 1 },
            new ResultadoEntidad { MonitorId = otro.Id, Momento = Ahora.AddHours(-1), Correcto = true, LatenciaMs = 2 }));

        (await GetAsync($"/api/monitores/{uno.Id}/resultados")).EnumerateArray().Single().GetProperty("latenciaMs").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData("desde=2026-10-01T00:00:00Z&hasta=2026-10-20T00:00:00Z", "historico.rango_demasiado_grande")]
    [InlineData("desde=2026-10-15T10:00:00Z&hasta=2026-10-15T09:00:00Z", "historico.rango_invalido")]
    [InlineData("desde=2026-10-15T10:00:00Z&hasta=2026-10-15T10:00:00Z", "historico.rango_invalido")]
    [InlineData("limite=0", "historico.limite_invalido")]
    [InlineData("limite=-4", "historico.limite_invalido")]
    public async Task Los_parametros_absurdos_son_un_400_con_su_codigo(string consulta, string codigo)
    {
        var m = await MonitorEnBaseDeDatosAsync();

        using var respuesta = await Cliente.GetAsync($"/api/monitores/{m.Id}/resultados?{consulta}", Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LeerAsync(respuesta)).GetProperty("codigo").GetString().ShouldBe(codigo);
    }

    [Fact]
    public async Task Un_limite_enorme_se_recorta_al_maximo_para_no_devolver_millones_de_filas()
    {
        var m = await MonitorEnBaseDeDatosAsync();
        await SembrarAsync(db => db.Resultados.AddRange(Enumerable.Range(1, 30).Select(i => new ResultadoEntidad { MonitorId = m.Id, Momento = Ahora.AddMinutes(-i), Correcto = true })));

        var resultados = await GetAsync($"/api/monitores/{m.Id}/resultados?limite=2000000000");

        resultados.GetArrayLength().ShouldBe(30);
        Vigia.Api.Consultas.ConsultasDePanel.MaximoDeFilasDeResultados.ShouldBe(5000);
    }

    // --- Latencia -------------------------------------------------------------------------------------

    [Fact]
    public async Task La_latencia_trae_los_puntos_por_hora_con_media_p50_y_p95()
    {
        var m = await MonitorEnBaseDeDatosAsync();
        await SembrarAsync(db => db.ResultadosHora.AddRange(
            new AgregadoHora { MonitorId = m.Id, Periodo = Ahora.AddHours(-3), LatenciaMediaMs = 120, P50Ms = 100, P95Ms = 400, Correctas = 58, Fallidas = 2, SegOperativo = 3540, SegCaido = 60 },
            new AgregadoHora { MonitorId = m.Id, Periodo = Ahora.AddHours(-2), LatenciaMediaMs = 90, P50Ms = 80, P95Ms = 200, Correctas = 60, SegOperativo = 3600 },
            new AgregadoHora { MonitorId = m.Id, Periodo = Ahora.AddHours(-50), LatenciaMediaMs = 1, P50Ms = 1, P95Ms = 1 }));

        var puntos = (await GetAsync($"/api/monitores/{m.Id}/latencia")).EnumerateArray().ToList();

        puntos.Count.ShouldBe(2);
        puntos[0].GetProperty("hora").GetDateTimeOffset().ShouldBe(Ahora.AddHours(-3), "de más antiguo a más nuevo, para dibujar la gráfica");
        puntos[0].GetProperty("mediaMs").GetDouble().ShouldBe(120);
        puntos[0].GetProperty("p50Ms").GetDouble().ShouldBe(100);
        puntos[0].GetProperty("p95Ms").GetDouble().ShouldBe(400);
        puntos[0].GetProperty("fallidas").GetInt32().ShouldBe(2);
        puntos[0].GetProperty("disponibilidad").GetDecimal().ShouldBe(98.33m, "3540/3600 truncado, no redondeado");
        puntos[1].GetProperty("disponibilidad").GetDecimal().ShouldBe(100m);
    }

    [Fact]
    public async Task La_latencia_admite_mas_horas_y_rechaza_rangos_absurdos()
    {
        var m = await MonitorEnBaseDeDatosAsync();
        await SembrarAsync(db => db.ResultadosHora.Add(new AgregadoHora { MonitorId = m.Id, Periodo = Ahora.AddHours(-50), LatenciaMediaMs = 1 }));

        (await GetAsync($"/api/monitores/{m.Id}/latencia?horas=72")).GetArrayLength().ShouldBe(1);
        (await EstadoDeAsync($"/api/monitores/{m.Id}/latencia?horas=0")).ShouldBe(HttpStatusCode.BadRequest);
        (await EstadoDeAsync($"/api/monitores/{m.Id}/latencia?horas=3000")).ShouldBe(HttpStatusCode.BadRequest);
    }

    // --- Disponibilidad --------------------------------------------------------------------------------

    [Fact]
    public async Task La_disponibilidad_a_24_horas_7_30_y_90_dias_sale_de_los_agregados_y_se_trunca()
    {
        var m = await MonitorEnBaseDeDatosAsync();
        await SembrarAsync(db =>
        {
            // Horas: una reciente al 100 %, una de ayer caída entera y una de hace 10 días de 2 h operativas.
            db.ResultadosHora.AddRange(
                new AgregadoHora { MonitorId = m.Id, Periodo = Ahora.AddHours(-2), SegOperativo = 3600 },
                new AgregadoHora { MonitorId = m.Id, Periodo = Ahora.AddHours(-30), SegCaido = 3600 },
                new AgregadoHora { MonitorId = m.Id, Periodo = Ahora.AddDays(-10), SegOperativo = 7200 });

            // Días: 80000 s en pie y 6400 caído = 92,5925…, que se enseña como 92,59 y no como 92,6.
            db.ResultadosDia.Add(new AgregadoDia { MonitorId = m.Id, Periodo = Hoy.AddDays(-5), SegOperativo = 80000, SegCaido = 6400 });
        });

        var disponibilidad = await GetAsync($"/api/monitores/{m.Id}/disponibilidad");

        disponibilidad.GetProperty("ultimas24Horas").GetDecimal().ShouldBe(100m);
        disponibilidad.GetProperty("ultimos7Dias").GetDecimal().ShouldBe(50m);
        disponibilidad.GetProperty("ultimos30Dias").GetDecimal().ShouldBe(75m);
        disponibilidad.GetProperty("ultimos90Dias").GetDecimal().ShouldBe(92.59m);
    }

    [Fact]
    public async Task Sin_datos_la_disponibilidad_es_nula_y_el_mantenimiento_no_cuenta()
    {
        var m = await MonitorEnBaseDeDatosAsync();

        (await GetAsync($"/api/monitores/{m.Id}/disponibilidad")).GetProperty("ultimos30Dias").ValueKind.ShouldBe(JsonValueKind.Null);

        await SembrarAsync(db => db.ResultadosHora.Add(new AgregadoHora { MonitorId = m.Id, Periodo = Ahora.AddHours(-2), SegMantenimiento = 3600 }));

        (await GetAsync($"/api/monitores/{m.Id}/disponibilidad")).GetProperty("ultimas24Horas").ValueKind.ShouldBe(JsonValueKind.Null, "una hora entera de mantenimiento no es ni un 100 % ni un 0 %");
    }

    // --- Barras diarias ----------------------------------------------------------------------------------

    [Fact]
    public async Task Las_barras_traen_un_dia_por_posicion_con_huecos_y_hoy_con_las_horas_cerradas()
    {
        var m = await MonitorEnBaseDeDatosAsync();
        await SembrarAsync(db =>
        {
            db.ResultadosDia.AddRange(
                new AgregadoDia { MonitorId = m.Id, Periodo = Hoy.AddDays(-1), SegOperativo = 86400 },
                new AgregadoDia { MonitorId = m.Id, Periodo = Hoy.AddDays(-3), SegOperativo = 43200, SegCaido = 43200 });
            db.ResultadosHora.Add(new AgregadoHora { MonitorId = m.Id, Periodo = Hoy.AddHours(5), SegOperativo = 3600 });
        });

        var barras = (await GetAsync($"/api/monitores/{m.Id}/barras?dias=5")).EnumerateArray().ToList();

        barras.Count.ShouldBe(5);
        barras.Select(b => b.GetProperty("dia").GetString()).ShouldBe(["2026-10-11", "2026-10-12", "2026-10-13", "2026-10-14", "2026-10-15"]);
        barras[0].GetProperty("disponibilidad").ValueKind.ShouldBe(JsonValueKind.Null, "día sin datos: hueco, no un 100 %");
        barras[1].GetProperty("disponibilidad").GetDecimal().ShouldBe(50m);
        barras[1].GetProperty("peorEstado").GetString().ShouldBe("Caido");
        barras[1].GetProperty("segundosCaido").GetDouble().ShouldBe(43200);
        barras[2].GetProperty("disponibilidad").ValueKind.ShouldBe(JsonValueKind.Null);
        barras[3].GetProperty("disponibilidad").GetDecimal().ShouldBe(100m);
        barras[3].GetProperty("peorEstado").GetString().ShouldBe("Operativo");
        barras[4].GetProperty("disponibilidad").GetDecimal().ShouldBe(100m, "hoy suma las horas ya cerradas");
    }

    [Fact]
    public async Task Por_defecto_son_noventa_barras_y_el_limite_es_noventa()
    {
        var m = await MonitorEnBaseDeDatosAsync();

        (await GetAsync($"/api/monitores/{m.Id}/barras")).GetArrayLength().ShouldBe(90);
        (await EstadoDeAsync($"/api/monitores/{m.Id}/barras?dias=91")).ShouldBe(HttpStatusCode.BadRequest);
        (await EstadoDeAsync($"/api/monitores/{m.Id}/barras?dias=0")).ShouldBe(HttpStatusCode.BadRequest);
    }

    // --- Incidentes --------------------------------------------------------------------------------------

    [Fact]
    public async Task Los_incidentes_de_un_monitor_salen_del_mas_nuevo_al_mas_viejo_con_su_duracion_y_causa()
    {
        var m = await MonitorEnBaseDeDatosAsync("Servicio");
        var viejo = Incidente.Abrir(m.Id, Ahora.AddDays(-2), "Error 500", 3);
        viejo.Cerrar(Ahora.AddDays(-2).AddMinutes(12), MotivoDeCierre.Recuperado);
        var abierto = Incidente.Abrir(m.Id, Ahora.AddMinutes(-30), "Sin respuesta en 10 s.", 3);
        await SembrarAsync(db => db.Incidentes.AddRange(viejo, abierto));

        var incidentes = (await GetAsync($"/api/monitores/{m.Id}/incidentes")).EnumerateArray().ToList();

        incidentes.Count.ShouldBe(2);
        incidentes[0].GetProperty("monitor").GetString().ShouldBe("Servicio");
        incidentes[0].GetProperty("cerradoEn").ValueKind.ShouldBe(JsonValueKind.Null);
        incidentes[0].GetProperty("duracionSegundos").GetDouble().ShouldBe(1800, "un incidente abierto dura hasta ahora");
        incidentes[0].GetProperty("causa").GetString().ShouldBe("Sin respuesta en 10 s.");
        incidentes[1].GetProperty("duracionSegundos").GetDouble().ShouldBe(720);
        incidentes[1].GetProperty("cerradoPor").GetString().ShouldBe("Recuperado");
    }

    [Fact]
    public async Task La_lista_global_de_incidentes_se_puede_filtrar_por_abiertos_y_limitar()
    {
        var uno = await MonitorEnBaseDeDatosAsync("Uno");
        var otro = await MonitorEnBaseDeDatosAsync("Otro");
        var cerrado = Incidente.Abrir(uno.Id, Ahora.AddDays(-1), "x", 3);
        cerrado.Cerrar(Ahora.AddDays(-1).AddMinutes(5), MotivoDeCierre.Recuperado);
        await SembrarAsync(db => db.Incidentes.AddRange(cerrado, Incidente.Abrir(otro.Id, Ahora.AddHours(-1), "y", 3)));

        (await GetAsync("/api/incidentes")).GetArrayLength().ShouldBe(2);
        (await GetAsync("/api/incidentes?abiertos=true")).EnumerateArray().Single().GetProperty("monitor").GetString().ShouldBe("Otro");
        (await GetAsync("/api/incidentes?limite=1")).GetArrayLength().ShouldBe(1);
        (await EstadoDeAsync("/api/incidentes?limite=0")).ShouldBe(HttpStatusCode.BadRequest);
        (await EstadoDeAsync("/api/incidentes?limite=1000")).ShouldBe(HttpStatusCode.BadRequest);
    }

    // --- Todo necesita que el monitor exista ------------------------------------------------------------------

    [Theory]
    [InlineData("resultados")]
    [InlineData("latencia")]
    [InlineData("disponibilidad")]
    [InlineData("barras")]
    [InlineData("incidentes")]
    public async Task Pedir_el_historico_de_un_monitor_inexistente_es_un_404(string recurso)
    {
        (await EstadoDeAsync($"/api/monitores/{Guid.NewGuid()}/{recurso}")).ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void El_estado_del_monitor_se_serializa_con_todos_los_nombres_esperados()
    {
        Enum.GetNames<EstadoMonitor>().ShouldBe(["Desconocido", "Operativo", "Degradado", "Sospechoso", "Caido", "Mantenimiento"], ignoreOrder: true);
    }
}
