using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Shouldly;

using Vigia.Datos.Persistencia;

namespace Vigia.Api.Tests;

public class GruposApiTests : BaseApiTest
{
    [Fact]
    public async Task Crear_un_grupo_devuelve_201_y_aparece_en_la_lista_con_su_numero_de_monitores()
    {
        using var creado = await Cliente.PostAsJsonAsync("/api/grupos", new { slug = "produccion", nombre = "  Producción ", publico = true }, Cancelacion);

        creado.StatusCode.ShouldBe(HttpStatusCode.Created);
        var grupo = await LeerAsync(creado);
        grupo.GetProperty("nombre").GetString().ShouldBe("Producción");
        creado.Headers.Location!.ToString().ShouldBe($"/api/grupos/{grupo.GetProperty("id").GetGuid()}");

        await CrearMonitorAsync("Uno", grupoId: grupo.GetProperty("id").GetGuid());
        await CrearMonitorAsync("Dos", grupoId: grupo.GetProperty("id").GetGuid());
        await CrearMonitorAsync("Suelto");

        var lista = await LeerAsync(await Cliente.GetAsync("/api/grupos", Cancelacion));
        var unico = lista.EnumerateArray().Single();
        unico.GetProperty("slug").GetString().ShouldBe("produccion");
        unico.GetProperty("monitores").GetInt32().ShouldBe(2);
    }

    [Theory]
    [InlineData("A", "Nombre", "grupo.slug_invalido")]
    [InlineData("Mayusculas", "Nombre", "grupo.slug_invalido")]
    [InlineData("con espacios", "Nombre", "grupo.slug_invalido")]
    [InlineData("../../etc", "Nombre", "grupo.slug_invalido")]
    [InlineData("-empieza", "Nombre", "grupo.slug_invalido")]
    [InlineData("valido", "", "grupo.nombre_invalido")]
    public async Task Los_datos_del_grupo_se_validan(string slug, string nombre, string codigo)
    {
        using var respuesta = await Cliente.PostAsJsonAsync("/api/grupos", new { slug, nombre, publico = true }, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await LeerAsync(respuesta)).GetProperty("codigo").GetString().ShouldBe(codigo);
    }

    [Fact]
    public async Task Un_identificador_repetido_es_un_409()
    {
        await CrearGrupoAsync("produccion");

        using var respuesta = await Cliente.PostAsJsonAsync("/api/grupos", new { slug = "produccion", nombre = "Otro", publico = false }, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await LeerAsync(respuesta)).GetProperty("codigo").GetString().ShouldBe("grupo.slug_repetido");
        (await ContarAsync("grupos")).ShouldBe(1);
    }

    [Fact]
    public async Task Modificar_cambia_el_nombre_y_la_visibilidad_pero_no_el_identificador()
    {
        var id = await CrearGrupoAsync("produccion", "Producción", publico: false);

        using var respuesta = await Cliente.PutAsJsonAsync($"/api/grupos/{id}", new { nombre = "Sistemas", publico = true }, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.OK);
        var grupo = await LeerAsync(respuesta);
        grupo.GetProperty("nombre").GetString().ShouldBe("Sistemas");
        grupo.GetProperty("publico").GetBoolean().ShouldBeTrue();
        grupo.GetProperty("slug").GetString().ShouldBe("produccion");
    }

    [Fact]
    public async Task Modificar_con_un_nombre_invalido_es_un_400_y_un_grupo_inexistente_un_404()
    {
        var id = await CrearGrupoAsync();

        (await Cliente.PutAsJsonAsync($"/api/grupos/{id}", new { nombre = " ", publico = true }, Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Cliente.PutAsJsonAsync($"/api/grupos/{Guid.NewGuid()}", new { nombre = "X", publico = true }, Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Borrar_un_grupo_no_borra_sus_monitores_solo_los_deja_sin_grupo()
    {
        var grupo = await CrearGrupoAsync();
        var monitor = await CrearMonitorAsync("Web", grupoId: grupo);

        (await Cliente.DeleteAsync($"/api/grupos/{grupo}", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var dto = await LeerAsync(await Cliente.GetAsync($"/api/monitores/{monitor}", Cancelacion));
        dto.GetProperty("grupoId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await Cliente.DeleteAsync($"/api/grupos/{grupo}", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

public class MantenimientoApiTests : BaseApiTest
{
    private static readonly DateTimeOffset Inicio = FabricaDeApi.Inicio;

    [Fact]
    public async Task Crear_una_ventana_devuelve_201_y_aparece_entre_las_vigentes()
    {
        var monitor = await CrearMonitorAsync();

        using var respuesta = await Cliente.PostAsJsonAsync("/api/mantenimientos", new { monitorIds = new[] { monitor }, inicio = Inicio.AddHours(1), fin = Inicio.AddHours(3), motivo = "Migración de base de datos" }, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.Created);
        var lista = await LeerAsync(await Cliente.GetAsync("/api/mantenimientos", Cancelacion));
        var ventana = lista.EnumerateArray().Single();
        ventana.GetProperty("motivo").GetString().ShouldBe("Migración de base de datos");
        ventana.GetProperty("monitorIds").EnumerateArray().Select(x => x.GetGuid()).ShouldBe([monitor]);
    }

    [Fact]
    public async Task Las_ventanas_ya_terminadas_no_aparecen_en_la_lista_de_vigentes()
    {
        var monitor = await CrearMonitorAsync();
        (await Cliente.PostAsJsonAsync("/api/mantenimientos", new { monitorIds = new[] { monitor }, inicio = Inicio.AddHours(1), fin = Inicio.AddHours(2), motivo = "Reinicio" }, Cancelacion)).EnsureSuccessStatusCode();

        Fabrica.Reloj.Advance(TimeSpan.FromHours(3));

        // Tres horas después la sesión anterior ya caducó: se entra de nuevo.
        using var sesionNueva = await Fabrica.ClienteAutenticadoAsync();

        (await LeerAsync(await sesionNueva.GetAsync("/api/mantenimientos", Cancelacion))).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Los_datos_de_la_ventana_se_validan_con_las_reglas_del_dominio()
    {
        var monitor = await CrearMonitorAsync();

        async Task<JsonElement> Enviar(object cuerpo)
        {
            using var respuesta = await Cliente.PostAsJsonAsync("/api/mantenimientos", cuerpo, Cancelacion);
            respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            return await LeerAsync(respuesta);
        }

        (await Enviar(new { monitorIds = new[] { monitor }, inicio = Inicio.AddHours(2), fin = Inicio.AddHours(1), motivo = "x" })).GetProperty("codigo").GetString().ShouldBe("mantenimiento.periodo_invalido");
        (await Enviar(new { monitorIds = new[] { monitor }, inicio = Inicio, fin = Inicio.AddDays(40), motivo = "x" })).GetProperty("codigo").GetString().ShouldBe("mantenimiento.periodo_invalido");
        (await Enviar(new { monitorIds = new[] { monitor }, inicio = Inicio, fin = Inicio.AddHours(1), motivo = " " })).GetProperty("codigo").GetString().ShouldBe("mantenimiento.motivo_invalido");
        (await Enviar(new { monitorIds = Array.Empty<Guid>(), inicio = Inicio, fin = Inicio.AddHours(1), motivo = "x" })).GetProperty("codigo").GetString().ShouldBe("mantenimiento.sin_monitores");
        (await Enviar(new { monitorIds = new[] { Guid.NewGuid() }, inicio = Inicio, fin = Inicio.AddHours(1), motivo = "x" })).GetProperty("codigo").GetString().ShouldBe("mantenimiento.monitor_inexistente");

        (await ContarAsync("ventanas_mantenimiento")).ShouldBe(0);
    }

    [Fact]
    public async Task Una_ventana_se_puede_cancelar_y_cancelar_una_inexistente_da_404()
    {
        var monitor = await CrearMonitorAsync();
        using var creada = await Cliente.PostAsJsonAsync("/api/mantenimientos", new { monitorIds = new[] { monitor }, inicio = Inicio, fin = Inicio.AddHours(1), motivo = "x" }, Cancelacion);
        var id = (await LeerAsync(creada)).GetProperty("id").GetGuid();

        (await Cliente.DeleteAsync($"/api/mantenimientos/{id}", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Cliente.DeleteAsync($"/api/mantenimientos/{id}", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ContarAsync("ventanas_mantenimiento")).ShouldBe(0);
    }

    [Fact]
    public async Task Crear_y_cancelar_una_ventana_avisa_al_worker()
    {
        var monitor = await CrearMonitorAsync();
        await using var observador = await ObservarAsync(Notificaciones.CambioDeMonitores);

        using var creada = await Cliente.PostAsJsonAsync("/api/mantenimientos", new { monitorIds = new[] { monitor }, inicio = Inicio, fin = Inicio.AddHours(1), motivo = "x" }, Cancelacion);
        await observador.EsperarAsync(1);

        (await Cliente.DeleteAsync($"/api/mantenimientos/{(await LeerAsync(creada)).GetProperty("id").GetGuid()}", Cancelacion)).EnsureSuccessStatusCode();
        await observador.EsperarAsync(2);
    }
}
