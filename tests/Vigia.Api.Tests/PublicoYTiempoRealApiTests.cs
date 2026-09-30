using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Vigia.Api.Contratos;
using Vigia.Datos.Persistencia;
using Vigia.Dominio.Monitores;
using Vigia.Dominio.Seguimiento;

namespace Vigia.Api.Tests;

public class EstadoGeneralTests
{
    [Theory]
    [InlineData(new EstadoMonitor[] { }, EstadoGeneral.SinDatos)]
    [InlineData(new[] { EstadoMonitor.Desconocido }, EstadoGeneral.SinDatos)]
    [InlineData(new[] { EstadoMonitor.Operativo, EstadoMonitor.Operativo }, EstadoGeneral.Operativo)]
    [InlineData(new[] { EstadoMonitor.Operativo, EstadoMonitor.Desconocido }, EstadoGeneral.Operativo)]
    [InlineData(new[] { EstadoMonitor.Operativo, EstadoMonitor.Sospechoso }, EstadoGeneral.Operativo)]
    [InlineData(new[] { EstadoMonitor.Operativo, EstadoMonitor.Degradado }, EstadoGeneral.Degradado)]
    [InlineData(new[] { EstadoMonitor.Operativo, EstadoMonitor.Caido }, EstadoGeneral.IncidenteParcial)]
    [InlineData(new[] { EstadoMonitor.Degradado, EstadoMonitor.Caido }, EstadoGeneral.IncidenteParcial)]
    [InlineData(new[] { EstadoMonitor.Caido, EstadoMonitor.Caido }, EstadoGeneral.Caido)]
    [InlineData(new[] { EstadoMonitor.Caido, EstadoMonitor.Desconocido }, EstadoGeneral.Caido)]
    [InlineData(new[] { EstadoMonitor.Operativo, EstadoMonitor.Mantenimiento }, EstadoGeneral.Mantenimiento)]
    [InlineData(new[] { EstadoMonitor.Mantenimiento, EstadoMonitor.Caido }, EstadoGeneral.IncidenteParcial)]
    public void El_estado_general_es_el_peor_de_los_confirmados(EstadoMonitor[] estados, EstadoGeneral esperado)
    {
        EstadoGeneralDe.Calcular(estados).ShouldBe(esperado);
    }
}

public class PublicoApiTests : BaseApiTest
{
    private static readonly DateTimeOffset Ahora = FabricaDeApi.Inicio;

    private async Task<HttpResponseMessage> PaginaAsync(string slug)
    {
        // Sin sesión: la ve cualquiera.
        using var anonimo = Fabrica.CreateClient();

        return await anonimo.GetAsync($"/api/publico/estado/{slug}", Cancelacion);
    }

    private async Task PonerEstadoAsync(Vigia.Dominio.Monitores.Monitor monitor, EstadoMonitor estado)
    {
        await using var db = Fabrica.NuevoContexto();
        db.Seguimientos.Add(new SeguimientoEntidad { MonitorId = monitor.Id, Estado = estado, Desde = Ahora.AddHours(-1) });
        await db.SaveChangesAsync(Cancelacion);
    }

    [Fact]
    public async Task La_pagina_de_un_grupo_publico_se_ve_sin_sesion_con_estado_general_servicios_barras_e_incidentes()
    {
        var grupo = await CrearGrupoAsync("produccion", "Producción", publico: true);
        var web = await MonitorEnBaseDeDatosAsync("Web", grupo);
        var api = await MonitorEnBaseDeDatosAsync("API", grupo);
        await MonitorEnBaseDeDatosAsync("Pausado", grupo, activo: false);
        await MonitorEnBaseDeDatosAsync("De otro grupo");
        await PonerEstadoAsync(web, EstadoMonitor.Operativo);
        await PonerEstadoAsync(api, EstadoMonitor.Caido);

        await using (var db = Fabrica.NuevoContexto())
        {
            db.ResultadosDia.Add(new AgregadoDia { MonitorId = web.Id, Periodo = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), SegOperativo = 86400 });
            var reciente = Incidente.Abrir(api.Id, Ahora.AddMinutes(-20), "Error interno del servidor", 3);
            var cerrado = Incidente.Abrir(web.Id, Ahora.AddDays(-3), "Conexión rechazada", 3);
            cerrado.Cerrar(Ahora.AddDays(-3).AddMinutes(10), MotivoDeCierre.Recuperado);
            var viejo = Incidente.Abrir(web.Id, Ahora.AddDays(-45), "Muy antiguo", 3);
            viejo.Cerrar(Ahora.AddDays(-45).AddHours(1), MotivoDeCierre.Recuperado);
            db.Incidentes.AddRange(reciente, cerrado, viejo);
            await db.SaveChangesAsync(Cancelacion);
        }

        using var respuesta = await PaginaAsync("produccion");

        respuesta.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pagina = await LeerAsync(respuesta);
        pagina.GetProperty("grupo").GetString().ShouldBe("Producción");
        pagina.GetProperty("general").GetString().ShouldBe("IncidenteParcial");
        var servicios = pagina.GetProperty("servicios").EnumerateArray().ToList();
        servicios.Select(s => s.GetProperty("nombre").GetString()).ShouldBe(["API", "Web"], "solo los activos del grupo, por nombre");
        servicios[0].GetProperty("estado").GetString().ShouldBe("Caido");
        servicios[1].GetProperty("barras").GetArrayLength().ShouldBe(90);
        servicios[1].GetProperty("disponibilidad90Dias").GetDecimal().ShouldBe(100m);
        servicios[0].GetProperty("disponibilidad90Dias").ValueKind.ShouldBe(JsonValueKind.Null);

        var incidentes = pagina.GetProperty("incidentes").EnumerateArray().ToList();
        incidentes.Select(i => i.GetProperty("servicio").GetString()).ShouldBe(["API", "Web"], "abiertos y de los últimos 30 días; el de hace 45 días no");
        incidentes[0].GetProperty("cerradoEn").ValueKind.ShouldBe(JsonValueKind.Null);
        incidentes[1].GetProperty("duracionSegundos").GetDouble().ShouldBe(600);
    }

    [Fact]
    public async Task La_pagina_publica_no_filtra_direcciones_configuraciones_ni_mensajes_de_error()
    {
        var grupo = await CrearGrupoAsync("produccion", "Producción", publico: true);
        var monitor = await MonitorEnBaseDeDatosAsync("Web", grupo);
        await PonerEstadoAsync(monitor, EstadoMonitor.Caido);
        await using (var db = Fabrica.NuevoContexto())
        {
            db.Incidentes.Add(Incidente.Abrir(monitor.Id, Ahora.AddMinutes(-5), "Conexión rechazada a 10.0.0.7:5432 con la clave hunter2", 3));
            await db.SaveChangesAsync(Cancelacion);
        }

        using var respuesta = await PaginaAsync("produccion");
        var texto = await respuesta.Content.ReadAsStringAsync(Cancelacion);

        texto.ShouldNotContain("interno.ejemplo", Case.Insensitive, "la dirección vigilada es privada");
        texto.ShouldNotContain("secreto", Case.Insensitive);
        texto.ShouldNotContain("10.0.0.7", Case.Insensitive, "la causa del incidente puede contener datos internos");
        texto.ShouldNotContain("hunter2", Case.Insensitive);
        texto.ShouldNotContain(monitor.Id.ToString(), Case.Insensitive, "ni los identificadores internos");
        texto.ShouldNotContain("configuracion", Case.Insensitive);
    }

    [Fact]
    public async Task Un_grupo_privado_y_uno_que_no_existe_son_indistinguibles()
    {
        await CrearGrupoAsync("interno", "Interno", publico: false);

        using var privado = await PaginaAsync("interno");
        using var inexistente = await PaginaAsync("no-existe");

        privado.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        inexistente.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        static string SinTraza(string cuerpo) => System.Text.RegularExpressions.Regex.Replace(cuerpo, "\"traceId\":\"[^\"]*\"", string.Empty);

        SinTraza(await privado.Content.ReadAsStringAsync(Cancelacion)).ShouldBe(SinTraza(await inexistente.Content.ReadAsStringAsync(Cancelacion)), "la respuesta no revela que el grupo exista");
    }

    [Theory]
    [InlineData("MAYUSCULAS")]
    [InlineData("con_guion_bajo")]
    [InlineData("a")]
    [InlineData("%27%20OR%201=1--")]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    public async Task Un_identificador_con_forma_invalida_es_un_404_sin_tocar_la_base_de_datos(string slug)
    {
        using var respuesta = await PaginaAsync(slug);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task La_respuesta_se_puede_guardar_en_cache_unos_segundos()
    {
        await CrearGrupoAsync("produccion", "Producción", publico: true);

        using var respuesta = await PaginaAsync("produccion");

        respuesta.Headers.CacheControl!.Public.ShouldBeTrue();
        respuesta.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Un_grupo_publico_sin_datos_todavia_dice_sin_datos_y_no_inventa_un_operativo()
    {
        var grupo = await CrearGrupoAsync("nuevo", "Nuevo", publico: true);
        await MonitorEnBaseDeDatosAsync("Recien creado", grupo);

        var pagina = await LeerAsync(await PaginaAsync("nuevo"));

        pagina.GetProperty("general").GetString().ShouldBe("SinDatos");
        pagina.GetProperty("servicios")[0].GetProperty("estado").GetString().ShouldBe("Desconocido");
    }

    [Fact]
    public async Task Hacerlo_privado_cierra_la_pagina_al_momento()
    {
        var grupo = await CrearGrupoAsync("produccion", "Producción", publico: true);
        (await PaginaAsync("produccion")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Cliente.PutAsJsonAsync($"/api/grupos/{grupo}", new { nombre = "Producción", publico = false }, Cancelacion)).EnsureSuccessStatusCode();

        (await PaginaAsync("produccion")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task La_pagina_publica_tiene_limite_de_peticiones_por_cliente()
    {
        await CrearGrupoAsync("produccion", "Producción", publico: true);
        using var anonimo = Fabrica.CreateClient();
        var estados = new List<HttpStatusCode>();

        for (var i = 0; i < 125; i++)
        {
            using var respuesta = await anonimo.GetAsync("/api/publico/estado/produccion", Cancelacion);
            estados.Add(respuesta.StatusCode);
        }

        estados.Take(120).ShouldAllBe(e => e == HttpStatusCode.OK);
        estados.Skip(120).ShouldAllBe(e => e == HttpStatusCode.TooManyRequests);
    }
}

public class TiempoRealApiTests : BaseApiTest
{
    private static readonly ComprobacionPublicada Ejemplo = new(Guid.Parse("11111111-2222-3333-4444-555555555555"), FabricaDeApi.Inicio, true, 123, EstadoMonitor.Operativo, null);

    private async Task<HubConnection> ConectarAsync(string? token)
    {
        var conexion = new HubConnectionBuilder()
            .WithUrl(
                new Uri(Fabrica.Server.BaseAddress, "/hubs/panel"),
                opciones =>
                {
                    opciones.HttpMessageHandlerFactory = _ => Fabrica.Server.CreateHandler();
                    opciones.Transports = HttpTransportType.LongPolling;
                    opciones.AccessTokenProvider = () => Task.FromResult(token);
                })
            .AddJsonProtocol(protocolo => protocolo.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        await conexion.StartAsync(Cancelacion);

        return conexion;
    }

    private async Task NotificarAsync(string carga)
    {
        await using var db = Fabrica.NuevoContexto();
        await db.NotificarAsync(Notificaciones.ComprobacionGuardada, carga, Cancelacion);
    }

    /// <summary>El oyente del servidor arranca a la vez que la API: se insiste hasta que llega el primer mensaje (un aviso anterior a su LISTEN se pierde, y eso es lo normal).</summary>
    private async Task<JsonElement> EnviarHastaRecibirAsync(ConcurrentQueue<JsonElement> recibidos, string carga)
    {
        var limite = DateTime.UtcNow.AddSeconds(15);

        while (recibidos.IsEmpty)
        {
            if (DateTime.UtcNow > limite)
            {
                throw new TimeoutException("El panel no recibió el mensaje.");
            }

            await NotificarAsync(carga);
            await Task.Delay(150, Cancelacion);
        }

        return recibidos.First();
    }

    [Fact]
    public async Task Una_comprobacion_guardada_por_el_worker_llega_al_panel_conectado_por_signalr()
    {
        var token = await Fabrica.TokenAsync();
        await using var conexion = await ConectarAsync(token);
        var recibidos = new ConcurrentQueue<JsonElement>();
        conexion.On<JsonElement>("MonitorActualizado", mensaje => recibidos.Enqueue(mensaje));

        var mensaje = await EnviarHastaRecibirAsync(recibidos, Ejemplo.Serializar());

        mensaje.GetProperty("monitorId").GetGuid().ShouldBe(Ejemplo.MonitorId);
        mensaje.GetProperty("correcto").GetBoolean().ShouldBeTrue();
        mensaje.GetProperty("latenciaMs").GetInt32().ShouldBe(123);
        mensaje.GetProperty("estado").GetString().ShouldBe("Operativo", "los estados llegan con su nombre");
    }

    [Fact]
    public async Task Un_incidente_abierto_llega_marcado_para_que_el_panel_avise()
    {
        await using var conexion = await ConectarAsync(await Fabrica.TokenAsync());
        var recibidos = new ConcurrentQueue<JsonElement>();
        conexion.On<JsonElement>("MonitorActualizado", mensaje => recibidos.Enqueue(mensaje));

        var mensaje = await EnviarHastaRecibirAsync(recibidos, (Ejemplo with { Correcto = false, Estado = EstadoMonitor.Caido, Incidente = "abierto" }).Serializar());

        mensaje.GetProperty("estado").GetString().ShouldBe("Caido");
        mensaje.GetProperty("incidente").GetString().ShouldBe("abierto");
    }

    [Fact]
    public async Task Todos_los_paneles_conectados_reciben_cada_mensaje()
    {
        var token = await Fabrica.TokenAsync();
        await using var uno = await ConectarAsync(token);
        await using var dos = await ConectarAsync(token);
        var deUno = new ConcurrentQueue<JsonElement>();
        var deDos = new ConcurrentQueue<JsonElement>();
        uno.On<JsonElement>("MonitorActualizado", m => deUno.Enqueue(m));
        dos.On<JsonElement>("MonitorActualizado", m => deDos.Enqueue(m));

        await EnviarHastaRecibirAsync(deUno, Ejemplo.Serializar());
        var limite = DateTime.UtcNow.AddSeconds(10);

        while (deDos.IsEmpty && DateTime.UtcNow < limite)
        {
            await Task.Delay(50, Cancelacion);
        }

        deDos.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Una_carga_que_no_se_entiende_se_ignora_y_el_servicio_sigue_funcionando()
    {
        await using var conexion = await ConectarAsync(await Fabrica.TokenAsync());
        var recibidos = new ConcurrentQueue<JsonElement>();
        conexion.On<JsonElement>("MonitorActualizado", mensaje => recibidos.Enqueue(mensaje));

        // Primero se asegura de que el oyente ya escucha, y entonces se manda basura.
        await EnviarHastaRecibirAsync(recibidos, Ejemplo.Serializar());
        recibidos.Clear();

        await NotificarAsync("esto no es json");
        await NotificarAsync("{\"monitorId\":\"no-es-un-guid\"}");
        await NotificarAsync(Ejemplo.Serializar());

        var limite = DateTime.UtcNow.AddSeconds(10);

        while (recibidos.IsEmpty && DateTime.UtcNow < limite)
        {
            await Task.Delay(50, Cancelacion);
        }

        recibidos.Count.ShouldBe(1, "la basura no llega al panel y el mensaje bueno de después sí");
    }

    [Fact]
    public async Task Sin_token_o_con_un_token_falso_no_se_puede_conectar_al_hub()
    {
        await Should.ThrowAsync<HttpRequestException>(() => ConectarAsync(token: null));
        await Should.ThrowAsync<HttpRequestException>(() => ConectarAsync("esto.no.es-un-token"));
    }

    [Fact]
    public async Task El_token_en_la_direccion_solo_vale_para_el_hub_y_no_para_el_resto_de_la_api()
    {
        var token = await Fabrica.TokenAsync();
        using var anonimo = Fabrica.CreateClient();

        using var respuesta = await anonimo.GetAsync($"/api/monitores?access_token={token}", Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Las_notificaciones_no_se_acumulan_sin_limite_si_nadie_las_lee()
    {
        // Sin paneles conectados el reenvío sigue drenando su cola: mandar muchas no debe tumbar nada.
        for (var i = 0; i < 50; i++)
        {
            await NotificarAsync(Ejemplo.Serializar());
        }

        (await Cliente.GetAsync("/api/monitores", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Una_carga_demasiado_grande_se_rechaza_antes_de_enviarse()
    {
        await using var db = Fabrica.NuevoContexto();

        await Should.ThrowAsync<ArgumentException>(() => db.NotificarAsync(Notificaciones.ComprobacionGuardada, new string('x', Notificaciones.LongitudMaximaDeCarga + 1), Cancelacion));
    }

    [Fact]
    public async Task La_carga_publicada_por_el_worker_se_lee_de_vuelta_igual()
    {
        var leida = ComprobacionPublicada.Leer(Ejemplo.Serializar());

        leida.ShouldBe(Ejemplo);
        ComprobacionPublicada.Leer("no es json").ShouldBeNull();
        ComprobacionPublicada.Leer("{\"monitorId\":\"x\"}").ShouldBeNull();
        (await Fabrica.NuevoContexto().Database.CanConnectAsync(Cancelacion)).ShouldBeTrue();
    }
}
