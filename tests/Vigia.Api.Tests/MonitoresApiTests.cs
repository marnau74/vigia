using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

using Shouldly;

using Vigia.Datos.Persistencia;

namespace Vigia.Api.Tests;

public class MonitoresApiTests : BaseApiTest
{
    // --- Crear y leer ------------------------------------------------------------------------------

    [Fact]
    public async Task Crear_un_monitor_devuelve_201_con_su_direccion_y_el_estado_inicial()
    {
        using var respuesta = await Cliente.PostAsJsonAsync("/api/monitores", CuerpoDeMonitor("Mi web", umbralMs: 2000), Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.Created);
        var monitor = await LeerAsync(respuesta);
        respuesta.Headers.Location!.ToString().ShouldBe($"/api/monitores/{monitor.GetProperty("id").GetGuid()}");
        monitor.GetProperty("nombre").GetString().ShouldBe("Mi web");
        monitor.GetProperty("tipo").GetString().ShouldBe("Http");
        monitor.GetProperty("configuracion").GetProperty("tipo").GetString().ShouldBe("Http");
        monitor.GetProperty("configuracion").GetProperty("url").GetString().ShouldBe("https://ejemplo.com/");
        monitor.GetProperty("intervaloSegundos").GetInt32().ShouldBe(60);
        monitor.GetProperty("fallosParaIncidente").GetInt32().ShouldBe(3);
        monitor.GetProperty("umbralLentoMs").GetInt32().ShouldBe(2000);
        monitor.GetProperty("activo").GetBoolean().ShouldBeTrue();
        monitor.GetProperty("estado").GetString().ShouldBe("Desconocido", "los estados se escriben con su nombre");
        monitor.GetProperty("incidenteAbierto").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Un_monitor_creado_se_puede_leer_por_su_id_y_aparece_en_la_lista_ordenada_por_nombre()
    {
        var b = await CrearMonitorAsync("Beta");
        var a = await CrearMonitorAsync("Alfa");

        using var uno = await Cliente.GetAsync($"/api/monitores/{a}", Cancelacion);
        var lista = await LeerAsync(await Cliente.GetAsync("/api/monitores", Cancelacion));

        uno.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LeerAsync(uno)).GetProperty("nombre").GetString().ShouldBe("Alfa");
        lista.EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ShouldBe([a, b]);
    }

    [Fact]
    public async Task Cada_tipo_de_monitor_se_puede_crear()
    {
        object[] configuraciones =
        [
            new { tipo = "Http", url = "https://ejemplo.com/", palabraClave = "Bienvenido", metodo = "GET" },
            new { tipo = "Tls", host = "ejemplo.com" },
            new { tipo = "Dns", nombre = "ejemplo.com", registro = "A" },
            new { tipo = "Tcp", host = "ejemplo.com", puerto = 443 },
            new { tipo = "Icmp", host = "ejemplo.com" },
        ];

        foreach (var configuracion in configuraciones)
        {
            using var respuesta = await Cliente.PostAsJsonAsync("/api/monitores", CuerpoDeMonitor(configuracion: configuracion), Cancelacion);

            respuesta.StatusCode.ShouldBe(HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync(Cancelacion));
        }

        (await ContarAsync("monitores")).ShouldBe(5);
    }

    [Fact]
    public async Task Un_monitor_que_no_existe_da_404_con_problem_json()
    {
        using var respuesta = await Cliente.GetAsync($"/api/monitores/{Guid.NewGuid()}", Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        respuesta.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await LeerAsync(respuesta)).GetProperty("codigo").GetString().ShouldBe("no_encontrado");
    }

    [Fact]
    public async Task Un_id_que_no_es_un_guid_no_llega_a_ningun_endpoint()
    {
        (await Cliente.GetAsync("/api/monitores/no-es-un-guid", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // --- Validación ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", 60, 3, "monitor.nombre_invalido")]
    [InlineData("   ", 60, 3, "monitor.nombre_invalido")]
    [InlineData("Nombre", 10, 3, "monitor.intervalo_invalido")]
    [InlineData("Nombre", 100000, 3, "monitor.intervalo_invalido")]
    [InlineData("Nombre", 60, 0, "monitor.fallos_invalidos")]
    [InlineData("Nombre", 60, 11, "monitor.fallos_invalidos")]
    public async Task Los_datos_del_monitor_se_validan_con_las_reglas_del_dominio(string nombre, int intervalo, int fallos, string codigo)
    {
        using var respuesta = await Cliente.PostAsJsonAsync("/api/monitores", CuerpoDeMonitor(nombre, intervalo: intervalo, fallos: fallos), Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LeerAsync(respuesta)).GetProperty("codigo").GetString().ShouldBe(codigo);
        (await ContarAsync("monitores")).ShouldBe(0);
    }

    [Fact]
    public async Task El_tiempo_maximo_debe_ser_menor_que_el_intervalo()
    {
        using var respuesta = await Cliente.PostAsJsonAsync("/api/monitores", CuerpoDeMonitor(configuracion: new { tipo = "Http", url = "https://ejemplo.com/", tiempoMaximo = "00:00:45" }, intervalo: 30), Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LeerAsync(respuesta)).GetProperty("codigo").GetString().ShouldBe("monitor.tiempo_maximo_invalido");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10_000)]
    public async Task El_umbral_de_lentitud_debe_ser_positivo_y_menor_que_el_tiempo_maximo(int umbralMs)
    {
        using var respuesta = await Cliente.PostAsJsonAsync("/api/monitores", CuerpoDeMonitor(umbralMs: umbralMs), Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LeerAsync(respuesta)).GetProperty("codigo").GetString().ShouldBe("monitor.umbral_lento_invalido");
    }

    [Fact]
    public async Task Un_grupo_que_no_existe_es_un_error_del_cliente()
    {
        using var respuesta = await Cliente.PostAsJsonAsync("/api/monitores", CuerpoDeMonitor(grupoId: Guid.NewGuid()), Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LeerAsync(respuesta)).GetProperty("codigo").GetString().ShouldBe("monitor.grupo_inexistente");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"tipo\":\"NoExiste\"}")]
    [InlineData("{\"tipo\":7}")]
    [InlineData("{\"tipo\":\"Http\"}")]
    [InlineData("{\"tipo\":\"Http\",\"url\":\"no es una url\"}")]
    [InlineData("{\"tipo\":\"Http\",\"url\":\"ftp://ejemplo.com/\"}")]
    [InlineData("{\"tipo\":\"Http\",\"url\":\"https://usuario:clave@ejemplo.com/\"}")]
    [InlineData("{\"tipo\":\"Http\",\"url\":\"https://ejemplo.com/\",\"tiempoMaximo\":\"abc\"}")]
    [InlineData("{\"tipo\":\"Tcp\",\"host\":\"ejemplo.com\",\"puerto\":70000}")]
    [InlineData("{\"tipo\":\"Tcp\",\"host\":\"ejemplo.com\"}")]
    [InlineData("{\"tipo\":\"Dns\",\"nombre\":\"ejemplo.com\",\"registro\":\"XYZ\"}")]
    public async Task Una_configuracion_que_no_encaja_es_un_400_y_nunca_un_500(string configuracion)
    {
        var cuerpo = $"{{\"nombre\":\"X\",\"configuracion\":{configuracion},\"intervaloSegundos\":60,\"fallosParaIncidente\":3}}";
        using var contenido = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");

        using var respuesta = await Cliente.PostAsync("/api/monitores", contenido, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await respuesta.Content.ReadAsStringAsync(Cancelacion));
        (await ContarAsync("monitores")).ShouldBe(0);
    }

    [Theory]
    [InlineData("{ esto no es json")]
    [InlineData("")]
    [InlineData("{\"nombre\":\"X\",\"intervaloSegundos\":\"muchos\"}")]
    [InlineData("[1,2,3]")]
    public async Task Un_cuerpo_que_no_es_el_esperado_es_un_400(string cuerpo)
    {
        using var contenido = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");

        using var respuesta = await Cliente.PostAsync("/api/monitores", contenido, Cancelacion);

        ((int)respuesta.StatusCode).ShouldBeInRange(400, 499);
    }

    // --- Modificar, pausar, borrar ---------------------------------------------------------------------

    [Fact]
    public async Task Modificar_cambia_los_datos_y_devuelve_el_monitor_nuevo()
    {
        var id = await CrearMonitorAsync("Antes");

        using var respuesta = await Cliente.PutAsJsonAsync($"/api/monitores/{id}", CuerpoDeMonitor("Después", Http("https://otra.example/"), intervalo: 120, fallos: 2, umbralMs: 1500), Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.OK);
        var monitor = await LeerAsync(respuesta);
        monitor.GetProperty("nombre").GetString().ShouldBe("Después");
        monitor.GetProperty("configuracion").GetProperty("url").GetString().ShouldBe("https://otra.example/");
        monitor.GetProperty("intervaloSegundos").GetInt32().ShouldBe(120);
        monitor.GetProperty("fallosParaIncidente").GetInt32().ShouldBe(2);

        (await LeerAsync(await Cliente.GetAsync($"/api/monitores/{id}", Cancelacion))).GetProperty("nombre").GetString().ShouldBe("Después");
    }

    [Fact]
    public async Task Un_monitor_no_puede_cambiar_de_tipo()
    {
        var id = await CrearMonitorAsync("Web");

        using var respuesta = await Cliente.PutAsJsonAsync($"/api/monitores/{id}", CuerpoDeMonitor("Web", new { tipo = "Tcp", host = "ejemplo.com", puerto = 443 }), Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await LeerAsync(respuesta)).GetProperty("codigo").GetString().ShouldBe("monitor.cambio_de_tipo");
        (await LeerAsync(await Cliente.GetAsync($"/api/monitores/{id}", Cancelacion))).GetProperty("tipo").GetString().ShouldBe("Http");
    }

    [Fact]
    public async Task Modificar_con_datos_invalidos_no_cambia_nada()
    {
        var id = await CrearMonitorAsync("Original");

        using var respuesta = await Cliente.PutAsJsonAsync($"/api/monitores/{id}", CuerpoDeMonitor("Nuevo", intervalo: 5), Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LeerAsync(await Cliente.GetAsync($"/api/monitores/{id}", Cancelacion))).GetProperty("nombre").GetString().ShouldBe("Original");
    }

    [Fact]
    public async Task Modificar_o_borrar_lo_que_no_existe_da_404()
    {
        var inexistente = Guid.NewGuid();

        (await Cliente.PutAsJsonAsync($"/api/monitores/{inexistente}", CuerpoDeMonitor(), Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Cliente.DeleteAsync($"/api/monitores/{inexistente}", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Cliente.PostAsync($"/api/monitores/{inexistente}/pausar", null, Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Cliente.PostAsync($"/api/monitores/{inexistente}/reanudar", null, Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Pausar_y_reanudar_cambian_si_esta_activo()
    {
        var id = await CrearMonitorAsync();

        (await Cliente.PostAsync($"/api/monitores/{id}/pausar", null, Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LeerAsync(await Cliente.GetAsync($"/api/monitores/{id}", Cancelacion))).GetProperty("activo").GetBoolean().ShouldBeFalse();

        (await Cliente.PostAsync($"/api/monitores/{id}/reanudar", null, Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LeerAsync(await Cliente.GetAsync($"/api/monitores/{id}", Cancelacion))).GetProperty("activo").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Borrar_un_monitor_borra_tambien_su_historico_sus_incidentes_y_sus_avisos()
    {
        var id = await CrearMonitorAsync();
        var otro = await CrearMonitorAsync("Otro");
        await SembrarHistoricoAsync(id);
        await SembrarHistoricoAsync(otro);

        (await Cliente.DeleteAsync($"/api/monitores/{id}", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Cliente.GetAsync($"/api/monitores/{id}", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ContarAsync("resultados")).ShouldBe(1, "solo quedan los del otro monitor");
        (await ContarAsync("incidentes")).ShouldBe(1);
        (await ContarAsync("avisos")).ShouldBe(1);
        (await ContarAsync("seguimientos")).ShouldBe(1);
        (await ContarAsync("cambios_estado")).ShouldBe(1);
        (await ContarAsync("resultados_hora")).ShouldBe(1);
    }

    private async Task SembrarHistoricoAsync(Guid monitorId)
    {
        await using var db = Fabrica.NuevoContexto();
        var incidente = Vigia.Dominio.Seguimiento.Incidente.Abrir(monitorId, FabricaDeApi.Inicio, "causa", 3);
        db.Incidentes.Add(incidente);
        db.Avisos.Add(Vigia.Dominio.Avisos.Aviso.Crear(incidente.Id, monitorId, Vigia.Dominio.Avisos.TipoAviso.Caida, new Vigia.Dominio.Avisos.DestinoDeAviso(Vigia.Dominio.Avisos.CanalAviso.Correo, "a@ejemplo.com"), FabricaDeApi.Inicio));
        db.Resultados.Add(new ResultadoEntidad { MonitorId = monitorId, Momento = FabricaDeApi.Inicio, Correcto = true, LatenciaMs = 10 });
        db.Seguimientos.Add(new SeguimientoEntidad { MonitorId = monitorId, Estado = Vigia.Dominio.Monitores.EstadoMonitor.Caido, Desde = FabricaDeApi.Inicio });
        db.CambiosDeEstado.Add(new CambioDeEstadoEntidad { MonitorId = monitorId, Momento = FabricaDeApi.Inicio, Anterior = Vigia.Dominio.Monitores.EstadoMonitor.Desconocido, Nuevo = Vigia.Dominio.Monitores.EstadoMonitor.Caido });
        db.ResultadosHora.Add(new AgregadoHora { MonitorId = monitorId, Periodo = FabricaDeApi.Inicio.AddHours(-1) });
        await db.SaveChangesAsync(Cancelacion);
    }

    // --- Lo que ve el panel: estado actual ------------------------------------------------------------------

    [Fact]
    public async Task La_lista_trae_el_estado_actual_la_ultima_latencia_el_incidente_abierto_y_la_disponibilidad()
    {
        var monitor = await MonitorEnBaseDeDatosAsync("Servicio");
        var ahora = FabricaDeApi.Inicio;

        await using (var db = Fabrica.NuevoContexto())
        {
            db.Seguimientos.Add(new SeguimientoEntidad { MonitorId = monitor.Id, Estado = Vigia.Dominio.Monitores.EstadoMonitor.Caido, Desde = ahora.AddMinutes(-10), UltimaObservacion = ahora.AddMinutes(-1) });
            db.Resultados.AddRange(
                new ResultadoEntidad { MonitorId = monitor.Id, Momento = ahora.AddMinutes(-2), Correcto = true, LatenciaMs = 111 },
                new ResultadoEntidad { MonitorId = monitor.Id, Momento = ahora.AddMinutes(-1), Correcto = false, LatenciaMs = 10000, Fallo = 1 });
            var incidente = Vigia.Dominio.Seguimiento.Incidente.Abrir(monitor.Id, ahora.AddMinutes(-8), "Sin respuesta en 10 s.", 3);
            db.Incidentes.Add(incidente);

            // 30 horas en pie y 10 caído: 75 % exacto.
            db.ResultadosHora.Add(new AgregadoHora { MonitorId = monitor.Id, Periodo = ahora.AddDays(-2), SegOperativo = 30 * 3600, SegCaido = 10 * 3600 });
            await db.SaveChangesAsync(Cancelacion);
        }

        var lista = await LeerAsync(await Cliente.GetAsync("/api/monitores", Cancelacion));

        var dto = lista.EnumerateArray().Single();
        dto.GetProperty("estado").GetString().ShouldBe("Caido");
        dto.GetProperty("estadoDesde").GetDateTimeOffset().ShouldBe(ahora.AddMinutes(-10));
        dto.GetProperty("ultimaComprobacion").GetDateTimeOffset().ShouldBe(ahora.AddMinutes(-1));
        dto.GetProperty("ultimaLatenciaMs").ValueKind.ShouldBe(JsonValueKind.Null, "la última comprobación fue un fallo: su latencia es el tiempo que se esperó, no dice nada");
        dto.GetProperty("disponibilidad30Dias").GetDecimal().ShouldBe(75m);
        dto.GetProperty("incidenteAbierto").GetProperty("causa").GetString().ShouldBe("Sin respuesta en 10 s.");
    }

    [Fact]
    public async Task La_ultima_latencia_es_la_de_la_ultima_comprobacion_si_fue_correcta()
    {
        var monitor = await MonitorEnBaseDeDatosAsync();

        await using (var db = Fabrica.NuevoContexto())
        {
            db.Resultados.AddRange(
                new ResultadoEntidad { MonitorId = monitor.Id, Momento = FabricaDeApi.Inicio.AddMinutes(-5), Correcto = true, LatenciaMs = 500 },
                new ResultadoEntidad { MonitorId = monitor.Id, Momento = FabricaDeApi.Inicio.AddMinutes(-1), Correcto = true, LatenciaMs = 123 });
            await db.SaveChangesAsync(Cancelacion);
        }

        var lista = await LeerAsync(await Cliente.GetAsync("/api/monitores", Cancelacion));

        lista.EnumerateArray().Single().GetProperty("ultimaLatenciaMs").GetInt32().ShouldBe(123);
    }

    [Fact]
    public async Task Sin_datos_no_se_inventa_una_disponibilidad()
    {
        await CrearMonitorAsync();

        var dto = (await LeerAsync(await Cliente.GetAsync("/api/monitores", Cancelacion))).EnumerateArray().Single();

        dto.GetProperty("disponibilidad30Dias").ValueKind.ShouldBe(JsonValueKind.Null, "sin datos no hay disponibilidad, no un 100 % inventado");
        dto.GetProperty("ultimaLatenciaMs").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task La_lista_se_puede_filtrar_por_grupo()
    {
        var grupo = await CrearGrupoAsync();
        var dentro = await CrearMonitorAsync("Dentro", grupoId: grupo);
        await CrearMonitorAsync("Fuera");

        var lista = await LeerAsync(await Cliente.GetAsync($"/api/monitores?grupo={grupo}", Cancelacion));

        lista.EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ShouldBe([dentro]);
    }

    // --- El worker se entera ---------------------------------------------------------------------------------

    [Fact]
    public async Task Cada_cambio_de_monitores_avisa_al_worker_por_notify()
    {
        await using var observador = await ObservarAsync(Notificaciones.CambioDeMonitores);

        var id = await CrearMonitorAsync();
        await observador.EsperarAsync(1);

        (await Cliente.PutAsJsonAsync($"/api/monitores/{id}", CuerpoDeMonitor("Nuevo"), Cancelacion)).EnsureSuccessStatusCode();
        await observador.EsperarAsync(2);

        (await Cliente.PostAsync($"/api/monitores/{id}/pausar", null, Cancelacion)).EnsureSuccessStatusCode();
        await observador.EsperarAsync(3);

        (await Cliente.PostAsync($"/api/monitores/{id}/reanudar", null, Cancelacion)).EnsureSuccessStatusCode();
        await observador.EsperarAsync(4);

        (await Cliente.DeleteAsync($"/api/monitores/{id}", Cancelacion)).EnsureSuccessStatusCode();
        await observador.EsperarAsync(5);
    }

    [Fact]
    public async Task Un_cambio_rechazado_no_avisa_a_nadie()
    {
        await using var observador = await ObservarAsync(Notificaciones.CambioDeMonitores);

        using var respuesta = await Cliente.PostAsJsonAsync("/api/monitores", CuerpoDeMonitor(intervalo: 1), Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await observador.NoLlegaNadaAsync();
    }

    // --- Probar ahora -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:9/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    public async Task Probar_una_direccion_interna_es_un_fallo_de_destino_bloqueado_y_no_sale_a_la_red(string url)
    {
        using var respuesta = await Cliente.PostAsJsonAsync("/api/probar", new { configuracion = new { tipo = "Http", url } }, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.OK);
        var prueba = await LeerAsync(respuesta);
        prueba.GetProperty("correcto").GetBoolean().ShouldBeFalse();
        prueba.GetProperty("fallo").GetString().ShouldBe("DestinoBloqueado");
    }

    [Fact]
    public async Task Probar_con_una_configuracion_invalida_es_un_400()
    {
        using var respuesta = await Cliente.PostAsJsonAsync("/api/probar", new { configuracion = new { tipo = "Http", url = "ftp://x" } }, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Probar_un_monitor_guardado_usa_su_configuracion_y_no_guarda_nada()
    {
        var id = await CrearMonitorAsync("Interno", Http("http://127.0.0.1:9/"));

        using var respuesta = await Cliente.PostAsync($"/api/monitores/{id}/probar", null, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LeerAsync(respuesta)).GetProperty("fallo").GetString().ShouldBe("DestinoBloqueado");
        (await ContarAsync("resultados")).ShouldBe(0, "una prueba no entra en el histórico");
        (await Cliente.PostAsync($"/api/monitores/{Guid.NewGuid()}/probar", null, Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Probar_contra_una_web_que_responde_mal_hace_un_solo_intento_sin_reintentos_ocultos()
    {
        // Si alguna capa reintentara por su cuenta (la resiliencia por defecto de los clientes HTTP),
        // la latencia y los fallos que Vigía cuenta serían mentira: aquí se comprueba que llega UNA petición.
        var peticiones = 0;
        var constructor = WebApplication.CreateSlimBuilder();
        constructor.WebHost.UseUrls("http://127.0.0.1:0");
        await using var servidor = constructor.Build();
        servidor.MapGet("/", () =>
        {
            Interlocked.Increment(ref peticiones);

            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });
        await servidor.StartAsync(Cancelacion);

        using var respuesta = await Cliente.PostAsJsonAsync("/api/probar", new { configuracion = new { tipo = "Http", url = servidor.Urls.First(), permitirRedPrivada = true } }, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.OK);
        var prueba = await LeerAsync(respuesta);
        prueba.GetProperty("correcto").GetBoolean().ShouldBeFalse();
        prueba.GetProperty("fallo").GetString().ShouldBe("CodigoInesperado");
        peticiones.ShouldBe(1);
    }

    [Fact]
    public async Task Probar_una_web_local_con_el_permiso_explicito_funciona()
    {
        var constructor = WebApplication.CreateSlimBuilder();
        constructor.WebHost.UseUrls("http://127.0.0.1:0");
        await using var servidor = constructor.Build();
        servidor.MapGet("/", () => "hola");
        await servidor.StartAsync(Cancelacion);

        using var respuesta = await Cliente.PostAsJsonAsync("/api/probar", new { configuracion = new { tipo = "Http", url = servidor.Urls.First(), permitirRedPrivada = true } }, Cancelacion);

        (await LeerAsync(respuesta)).GetProperty("correcto").GetBoolean().ShouldBeTrue();
    }
}
