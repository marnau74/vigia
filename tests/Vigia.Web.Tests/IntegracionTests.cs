using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Shouldly;

using Vigia.Contratos;
using Vigia.Datos.Persistencia;
using Vigia.Dominio.Monitores;
using Vigia.Dominio.Seguimiento;
using Vigia.Web.Servicios;

using MonitorDeDominio = Vigia.Dominio.Monitores.Monitor;

namespace Vigia.Web.Tests;

public abstract class IntegracionTest : IAsyncLifetime
{
    /// <summary>El HTML tal como lo vería una persona: Blazor escribe las letras con acento como entidades («&#xF3;»).</summary>
    protected static async Task<string> HtmlAsync(HttpResponseMessage respuesta) => WebUtility.HtmlDecode(await respuesta.Content.ReadAsStringAsync(Cancelacion));

    /// <summary>A dónde redirige, como ruta y consulta (las redirecciones llegan como direcciones absolutas).</summary>
    protected static string Destino(HttpResponseMessage respuesta) => respuesta.Headers.Location is { IsAbsoluteUri: true } absoluta ? absoluta.PathAndQuery : respuesta.Headers.Location!.OriginalString;

    protected static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    protected EntornoCompleto Entorno { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Entorno = await EntornoCompleto.CrearAsync();

    public async ValueTask DisposeAsync()
    {
        await Entorno.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected async Task<(Grupo Grupo, MonitorDeDominio Web, MonitorDeDominio Api)> SembrarGrupoPublicoAsync(bool publico = true)
    {
        var ahora = Entorno.Reloj.GetUtcNow();
        var grupo = Grupo.Crear("produccion", "Producción", publico).Valor;
        var web = MonitorDeDominio.Crear("Web", new ConfiguracionHttp(new Uri("https://interno.ejemplo/secreto")), TimeSpan.FromSeconds(60), 3, null, grupo.Id, ahora.AddDays(-100)).Valor;
        var api = MonitorDeDominio.Crear("API", new ConfiguracionHttp(new Uri("https://api.interna.ejemplo/")), TimeSpan.FromSeconds(60), 3, null, grupo.Id, ahora.AddDays(-100)).Valor;

        await using var db = Entorno.NuevoContexto();
        db.Grupos.Add(grupo);
        db.Monitores.AddRange(web, api);
        db.Seguimientos.AddRange(
            new SeguimientoEntidad { MonitorId = web.Id, Estado = EstadoMonitor.Operativo, Desde = ahora.AddHours(-3) },
            new SeguimientoEntidad { MonitorId = api.Id, Estado = EstadoMonitor.Caido, Desde = ahora.AddMinutes(-20) });
        db.Incidentes.Add(Incidente.Abrir(api.Id, ahora.AddMinutes(-20), "Conexión rechazada a 10.0.0.7:5432", 3));
        await db.SaveChangesAsync(Cancelacion);

        return (grupo, web, api);
    }
}

public class PaginaPublicaIntegracionTests : IntegracionTest
{
    [Fact]
    public async Task La_pagina_de_estado_se_dibuja_en_el_servidor_sin_sesion_con_los_datos_de_la_api()
    {
        await SembrarGrupoPublicoAsync();
        using var cliente = Entorno.ClienteWeb();

        using var respuesta = await cliente.GetAsync("/estado/produccion", Cancelacion);
        var html = await HtmlAsync(respuesta);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("<h1>Producción</h1>");
        html.ShouldContain("Hay servicios caídos");
        html.ShouldContain("Web");
        html.ShouldContain("API");
        html.ShouldContain("En curso");
        html.ShouldContain("http-equiv=\"refresh\"");
        html.ShouldContain("noindex");
        html.ShouldContain("lang=\"es\"");
    }

    [Fact]
    public async Task La_pagina_publica_no_filtra_direcciones_causas_ni_identificadores_internos()
    {
        var (_, web, api) = await SembrarGrupoPublicoAsync();
        using var cliente = Entorno.ClienteWeb();

        var html = await HtmlAsync(await cliente.GetAsync("/estado/produccion", Cancelacion));

        html.ShouldNotContain("interno.ejemplo", Case.Insensitive);
        html.ShouldNotContain("api.interna", Case.Insensitive);
        html.ShouldNotContain("10.0.0.7", Case.Insensitive);
        html.ShouldNotContain("Conexión rechazada", Case.Insensitive);
        html.ShouldNotContain(web.Id.ToString(), Case.Insensitive);
        html.ShouldNotContain(api.Id.ToString(), Case.Insensitive);
    }

    [Fact]
    public async Task Un_grupo_privado_y_uno_inexistente_dan_404_con_la_misma_pagina()
    {
        await SembrarGrupoPublicoAsync(publico: false);
        using var cliente = Entorno.ClienteWeb();

        using var privado = await cliente.GetAsync("/estado/produccion", Cancelacion);
        using var inexistente = await cliente.GetAsync("/estado/no-existe", Cancelacion);

        privado.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        inexistente.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await HtmlAsync(privado)).ShouldBe(await HtmlAsync(inexistente));
    }

    [Fact]
    public async Task Una_ruta_que_no_existe_da_404()
    {
        using var cliente = Entorno.ClienteWeb();

        using var respuesta = await cliente.GetAsync("/no/existe/esta/ruta", Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task La_pagina_publica_lleva_las_barras_accesibles_y_el_texto_de_estado()
    {
        await SembrarGrupoPublicoAsync();
        using var cliente = Entorno.ClienteWeb();

        var html = await HtmlAsync(await cliente.GetAsync("/estado/produccion", Cancelacion));

        html.ShouldContain("role=\"img\"");
        html.ShouldContain("Disponibilidad diaria de los últimos 90 días");
        html.ShouldContain("Saltar al contenido");
        html.ShouldContain("Operativo");
        html.ShouldContain("Caído");
    }

    [Fact]
    public async Task Los_ficheros_estaticos_se_sirven_y_el_css_respeta_el_modo_oscuro_y_el_movimiento_reducido()
    {
        using var cliente = Entorno.ClienteWeb();

        var css = await (await cliente.GetAsync("/app.css", Cancelacion)).Content.ReadAsStringAsync(Cancelacion);

        css.ShouldContain("prefers-color-scheme: dark");
        css.ShouldContain("prefers-reduced-motion");
        (await cliente.GetAsync("/favicon.svg", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

public class SesionIntegracionTests : IntegracionTest
{
    [Theory]
    [InlineData("/", "%2F")]
    [InlineData("/grupos", "%2Fgrupos")]
    [InlineData("/mantenimientos", "%2Fmantenimientos")]
    [InlineData("/monitores/nuevo", "%2Fmonitores%2Fnuevo")]
    public async Task Sin_sesion_el_panel_lleva_a_entrar_y_recuerda_a_donde_volver(string ruta, string destino)
    {
        using var cliente = Entorno.ClienteWeb();

        using var respuesta = await cliente.GetAsync(ruta, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Destino(respuesta).ShouldBe($"/entrar?volver={destino}");
    }

    [Fact]
    public async Task Entrar_con_la_contrasena_correcta_crea_una_cookie_de_sesion_segura_y_vuelve_al_inicio()
    {
        using var cliente = Entorno.ClienteWeb();
        using var formulario = await cliente.GetAsync("/entrar", Cancelacion);
        var token = EntornoCompleto.LeerToken(await formulario.Content.ReadAsStringAsync(Cancelacion));
        token.ShouldNotBeNullOrEmpty("el formulario lleva su protección contra falsificación");

        using var peticion = new HttpRequestMessage(HttpMethod.Post, "/entrar")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token!, ["Modelo.Contrasena"] = FabricaDeLaApi.Contrasena, ["_handler"] = "entrar" }),
        };
        peticion.Headers.Add("Cookie", EntornoCompleto.Galleta(formulario));
        using var respuesta = await cliente.SendAsync(peticion, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Destino(respuesta).ShouldBe("/");
        var cookie = respuesta.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("vigia.sesion=", StringComparison.Ordinal));
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=lax", Case.Insensitive);
        cookie.ShouldNotContain("expires=", Case.Insensitive, "es una cookie de sesión del navegador; su caducidad va dentro del ticket cifrado (ver el test de caducidad)");
    }

    [Fact]
    public async Task Con_la_cookie_se_ve_el_panel_y_la_cookie_no_contiene_el_token_en_claro()
    {
        using var cliente = Entorno.ClienteWeb();
        var galleta = await EntornoCompleto.EntrarAsync(cliente);

        using var peticion = new HttpRequestMessage(HttpMethod.Get, "/");
        peticion.Headers.Add("Cookie", galleta);
        using var respuesta = await cliente.SendAsync(peticion, Cancelacion);
        var html = await HtmlAsync(respuesta);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Saltar al contenido");
        html.ShouldContain("Panel");
        galleta.ShouldNotContain("eyJ", Case.Sensitive, "un JWT empieza por «eyJ»: la cookie va cifrada");
        html.ShouldNotContain("eyJ", Case.Sensitive, "el token de la API nunca llega al navegador dentro de la página");
    }

    [Fact]
    public async Task La_sesion_caduca_a_la_hora_igual_que_el_token_de_la_api()
    {
        using var cliente = Entorno.ClienteWeb();
        var galleta = await EntornoCompleto.EntrarAsync(cliente);

        async Task<HttpStatusCode> PedirPanelAsync()
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Get, "/");
            peticion.Headers.Add("Cookie", galleta);
            using var respuesta = await cliente.SendAsync(peticion, Cancelacion);

            return respuesta.StatusCode;
        }

        (await PedirPanelAsync()).ShouldBe(HttpStatusCode.OK);

        Entorno.Reloj.Advance(TimeSpan.FromMinutes(59));
        (await PedirPanelAsync()).ShouldBe(HttpStatusCode.OK);

        Entorno.Reloj.Advance(TimeSpan.FromMinutes(2));
        (await PedirPanelAsync()).ShouldBe(HttpStatusCode.Redirect, "con el token caducado, la cookie ya no vale aunque el navegador la conserve");
    }

    [Fact]
    public async Task Una_contrasena_incorrecta_no_entra_y_lo_dice_sin_crear_cookie()
    {
        using var cliente = Entorno.ClienteWeb();
        using var formulario = await cliente.GetAsync("/entrar", Cancelacion);
        var token = EntornoCompleto.LeerToken(await formulario.Content.ReadAsStringAsync(Cancelacion))!;

        using var peticion = new HttpRequestMessage(HttpMethod.Post, "/entrar")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["Modelo.Contrasena"] = "no-es-esta", ["_handler"] = "entrar" }),
        };
        peticion.Headers.Add("Cookie", EntornoCompleto.Galleta(formulario));
        using var respuesta = await cliente.SendAsync(peticion, Cancelacion);
        var html = await HtmlAsync(respuesta);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Contraseña incorrecta.");
        html.ShouldContain("role=\"alert\"");
        (respuesta.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : []).ShouldNotContain(c => c.StartsWith("vigia.sesion=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enviar_el_formulario_sin_la_proteccion_antifalsificacion_se_rechaza()
    {
        using var cliente = Entorno.ClienteWeb();

        using var respuesta = await cliente.PostAsync(
            "/entrar",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Modelo.Contrasena"] = FabricaDeLaApi.Contrasena, ["_handler"] = "entrar" }),
            Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/grupos", "/grupos")]
    [InlineData("//otra.web", "/")]
    [InlineData("https://otra.web/", "/")]
    [InlineData("/\\otra.web", "/")]
    public async Task Despues_de_entrar_solo_se_vuelve_a_rutas_de_la_propia_web(string volver, string esperado)
    {
        using var cliente = Entorno.ClienteWeb();
        using var formulario = await cliente.GetAsync("/entrar", Cancelacion);
        var token = EntornoCompleto.LeerToken(await formulario.Content.ReadAsStringAsync(Cancelacion))!;

        using var peticion = new HttpRequestMessage(HttpMethod.Post, $"/entrar?volver={Uri.EscapeDataString(volver)}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["Modelo.Contrasena"] = FabricaDeLaApi.Contrasena, ["_handler"] = "entrar" }),
        };
        peticion.Headers.Add("Cookie", EntornoCompleto.Galleta(formulario));
        using var respuesta = await cliente.SendAsync(peticion, Cancelacion);

        Destino(respuesta).ShouldBe(esperado);
    }

    [Fact]
    public async Task Salir_exige_la_proteccion_antifalsificacion_y_borra_la_cookie()
    {
        using var cliente = Entorno.ClienteWeb();
        var galleta = await EntornoCompleto.EntrarAsync(cliente);

        using var panel = new HttpRequestMessage(HttpMethod.Get, "/");
        panel.Headers.Add("Cookie", galleta);
        using var respuestaPanel = await cliente.SendAsync(panel, Cancelacion);
        var token = EntornoCompleto.LeerToken(await respuestaPanel.Content.ReadAsStringAsync(Cancelacion));
        token.ShouldNotBeNullOrEmpty("el botón «Salir» del panel lleva su token");

        using var sinToken = new HttpRequestMessage(HttpMethod.Post, "/salir") { Content = new FormUrlEncodedContent(new Dictionary<string, string>()) };
        sinToken.Headers.Add("Cookie", galleta);
        using var rechazada = await cliente.SendAsync(sinToken, Cancelacion);
        rechazada.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "un POST ajeno (falsificado desde otra web) no cierra la sesión");

        using var conToken = new HttpRequestMessage(HttpMethod.Post, "/salir") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token! }) };
        conToken.Headers.Add("Cookie", EntornoCompleto.Galleta(respuestaPanel) is { Length: > 0 } extra ? $"{galleta}; {extra}" : galleta);
        using var salida = await cliente.SendAsync(conToken, Cancelacion);

        salida.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Destino(salida).ShouldBe("/entrar");
        salida.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith("vigia.sesion=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Salir_por_un_enlace_get_no_existe()
    {
        using var cliente = Entorno.ClienteWeb();
        var galleta = await EntornoCompleto.EntrarAsync(cliente);
        using var peticion = new HttpRequestMessage(HttpMethod.Get, "/salir");
        peticion.Headers.Add("Cookie", galleta);

        using var respuesta = await cliente.SendAsync(peticion, Cancelacion);

        respuesta.StatusCode.ShouldNotBe(HttpStatusCode.Redirect, "cerrar la sesión con un GET permitiría cerrarla desde una imagen de otra web");
    }

    [Fact]
    public async Task Cinco_contrasenas_malas_seguidas_bloquean_y_la_web_lo_explica_sin_romperse()
    {
        using var cliente = Entorno.ClienteWeb();
        string html = string.Empty;

        for (var i = 0; i < 6; i++)
        {
            using var formulario = await cliente.GetAsync("/entrar", Cancelacion);
            var token = EntornoCompleto.LeerToken(await formulario.Content.ReadAsStringAsync(Cancelacion))!;
            using var peticion = new HttpRequestMessage(HttpMethod.Post, "/entrar")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["Modelo.Contrasena"] = "mala", ["_handler"] = "entrar" }),
            };
            peticion.Headers.Add("Cookie", EntornoCompleto.Galleta(formulario));
            using var respuesta = await cliente.SendAsync(peticion, Cancelacion);
            html = await respuesta.Content.ReadAsStringAsync(Cancelacion);
        }

        html.ShouldContain("Demasiados intentos seguidos");
    }
}

public class ClienteDeApiIntegracionTests : IntegracionTest
{
    private ClienteDeApi Cliente(string? token)
    {
        var http = new HttpClient(Entorno.Api.Server.CreateHandler()) { BaseAddress = new Uri("http://api.prueba/") };

        return new ClienteDeApi(http, new EstadoDeAutenticacionFalso(token));
    }

    private async Task<string> TokenAsync()
    {
        var respuesta = await Cliente(null).AccederAsync(FabricaDeLaApi.Contrasena, Cancelacion);

        return respuesta.Valor!.Token;
    }

    [Fact]
    public async Task Acceder_devuelve_el_token_y_cuando_caduca()
    {
        var respuesta = await Cliente(null).AccederAsync(FabricaDeLaApi.Contrasena, Cancelacion);

        respuesta.EsExito.ShouldBeTrue();
        respuesta.Valor!.Token.ShouldNotBeNullOrEmpty();
        respuesta.Valor.ExpiraEn.ShouldBe(Entorno.Reloj.GetUtcNow().AddHours(1), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Una_contrasena_incorrecta_es_un_error_con_su_codigo_y_no_una_sesion_caducada()
    {
        var respuesta = await Cliente(null).AccederAsync("mala", Cancelacion);

        respuesta.EsExito.ShouldBeFalse();
        respuesta.SesionCaducada.ShouldBeFalse();
        respuesta.Error!.Codigo.ShouldBe("acceso.contrasena_incorrecta");
        respuesta.Error.Mensaje.ShouldBe("Contraseña incorrecta.");
    }

    [Fact]
    public async Task Sin_token_o_con_un_token_falso_las_llamadas_privadas_dicen_que_la_sesion_caduco()
    {
        (await Cliente(null).MonitoresAsync(Cancelacion)).SesionCaducada.ShouldBeTrue();
        (await Cliente("esto.no.es-un-token").MonitoresAsync(Cancelacion)).SesionCaducada.ShouldBeTrue();
    }

    [Fact]
    public async Task Con_token_se_crea_lista_modifica_y_borra_un_monitor_de_punta_a_punta()
    {
        var cliente = Cliente(await TokenAsync());
        var configuracion = JsonSerializer.SerializeToElement(new { tipo = "Http", url = "https://ejemplo.com/" });

        var creado = await cliente.CrearMonitorAsync(new CuerpoMonitor("Mi web", configuracion, 60, 3, null, null), Cancelacion);
        creado.EsExito.ShouldBeTrue(creado.Error?.Mensaje);
        creado.Valor!.Estado.ShouldBe(EstadoMonitor.Desconocido);

        (await cliente.MonitoresAsync(Cancelacion)).Valor!.Select(m => m.Nombre).ShouldBe(["Mi web"]);

        var modificado = await cliente.ModificarMonitorAsync(creado.Valor.Id, new CuerpoMonitor("Otro nombre", configuracion, 120, 2, 1500, null), Cancelacion);
        modificado.Valor!.Nombre.ShouldBe("Otro nombre");
        modificado.Valor.UmbralLentoMs.ShouldBe(1500);

        (await cliente.CambiarActivoAsync(creado.Valor.Id, false, Cancelacion)).EsExito.ShouldBeTrue();
        (await cliente.MonitorAsync(creado.Valor.Id, Cancelacion)).Valor!.Activo.ShouldBeFalse();

        (await cliente.BorrarMonitorAsync(creado.Valor.Id, Cancelacion)).EsExito.ShouldBeTrue();
        (await cliente.MonitorAsync(creado.Valor.Id, Cancelacion)).Error!.Codigo.ShouldBe("no_encontrado");
    }

    [Fact]
    public async Task Los_errores_de_validacion_de_la_api_llegan_con_su_codigo_y_su_mensaje()
    {
        var cliente = Cliente(await TokenAsync());
        var configuracion = JsonSerializer.SerializeToElement(new { tipo = "Http", url = "https://ejemplo.com/" });

        var respuesta = await cliente.CrearMonitorAsync(new CuerpoMonitor("X", configuracion, 5, 3, null, null), Cancelacion);

        respuesta.EsExito.ShouldBeFalse();
        respuesta.Error!.Codigo.ShouldBe("monitor.intervalo_invalido");
        respuesta.Error.Mensaje.ShouldContain("30 segundos");
    }

    [Fact]
    public async Task Grupos_y_mantenimiento_funcionan_de_punta_a_punta()
    {
        var cliente = Cliente(await TokenAsync());
        var configuracion = JsonSerializer.SerializeToElement(new { tipo = "Tcp", host = "ejemplo.com", puerto = 443 });
        var grupo = (await cliente.CrearGrupoAsync(new CuerpoGrupo("produccion", "Producción", true), Cancelacion)).Valor!;
        var monitor = (await cliente.CrearMonitorAsync(new CuerpoMonitor("Puerto", configuracion, 60, 3, null, grupo.Id), Cancelacion)).Valor!;

        (await cliente.GruposAsync(Cancelacion)).Valor!.Single().Monitores.ShouldBe(1);
        (await cliente.CrearGrupoAsync(new CuerpoGrupo("produccion", "Repetido", true), Cancelacion)).Error!.Codigo.ShouldBe("grupo.slug_repetido");

        var ahora = Entorno.Reloj.GetUtcNow();
        var ventana = await cliente.CrearMantenimientoAsync(new CuerpoMantenimiento([monitor.Id], ahora.AddHours(1), ahora.AddHours(2), "Migración"), Cancelacion);
        ventana.EsExito.ShouldBeTrue(ventana.Error?.Mensaje);
        (await cliente.MantenimientosAsync(Cancelacion)).Valor!.Single().Motivo.ShouldBe("Migración");
        (await cliente.BorrarMantenimientoAsync(ventana.Valor!.Id, Cancelacion)).EsExito.ShouldBeTrue();
        (await cliente.BorrarGrupoAsync(grupo.Id, Cancelacion)).EsExito.ShouldBeTrue();
    }

    [Fact]
    public async Task Probar_una_direccion_interna_dice_que_esta_bloqueada_a_traves_de_la_web()
    {
        var cliente = Cliente(await TokenAsync());

        var prueba = await cliente.ProbarAsync(JsonSerializer.SerializeToElement(new { tipo = "Http", url = "http://169.254.169.254/" }), Cancelacion);

        prueba.Valor!.Correcto.ShouldBeFalse();
        prueba.Valor.Fallo.ShouldBe("DestinoBloqueado");
    }

    [Fact]
    public async Task El_historico_vacio_de_un_monitor_nuevo_se_lee_sin_errores()
    {
        var cliente = Cliente(await TokenAsync());
        var monitor = (await cliente.CrearMonitorAsync(new CuerpoMonitor("M", JsonSerializer.SerializeToElement(new { tipo = "Icmp", host = "ejemplo.com" }), 60, 3, null, null), Cancelacion)).Valor!;

        (await cliente.BarrasAsync(monitor.Id, 90, Cancelacion)).Valor!.Count.ShouldBe(90);
        (await cliente.LatenciaAsync(monitor.Id, 24, Cancelacion)).Valor!.ShouldBeEmpty();
        (await cliente.DisponibilidadAsync(monitor.Id, Cancelacion)).Valor!.Ultimos30Dias.ShouldBeNull();
        (await cliente.IncidentesAsync(monitor.Id, false, Cancelacion)).Valor!.ShouldBeEmpty();
        (await cliente.IncidentesAsync(null, true, Cancelacion)).Valor!.ShouldBeEmpty();
    }

    [Fact]
    public async Task La_pagina_publica_se_lee_sin_token_y_los_enums_llegan_bien()
    {
        await SembrarGrupoPublicoAsync();

        var respuesta = await Cliente(null).EstadoPublicoAsync("produccion", Cancelacion);

        respuesta.EsExito.ShouldBeTrue();
        respuesta.Valor!.General.ShouldBe(EstadoGeneral.IncidenteParcial);
        respuesta.Valor.Servicios.Select(s => s.Estado).Order().ShouldBe([EstadoMonitor.Operativo, EstadoMonitor.Caido]);
        (await Cliente(null).EstadoPublicoAsync("no-existe", Cancelacion)).Error!.Codigo.ShouldBe("no_encontrado");
    }

    [Fact]
    public async Task Si_la_api_no_responde_se_presenta_como_un_error_normal_y_no_como_una_excepcion()
    {
        var http = new HttpClient(new ManejadorQueFalla()) { BaseAddress = new Uri("http://api.prueba/") };
        var cliente = new ClienteDeApi(http, new EstadoDeAutenticacionFalso("token"));

        var respuesta = await cliente.MonitoresAsync(Cancelacion);

        respuesta.EsExito.ShouldBeFalse();
        respuesta.Error!.Codigo.ShouldBe("api_no_disponible");
        respuesta.Error.Mensaje.ShouldContain("No se puede contactar con la API");
    }

    [Fact]
    public async Task Una_respuesta_que_no_tiene_el_formato_esperado_no_rompe_la_pagina()
    {
        var http = new HttpClient(new ManejadorQueDevuelve(HttpStatusCode.OK, "esto no es json")) { BaseAddress = new Uri("http://api.prueba/") };
        var cliente = new ClienteDeApi(http, new EstadoDeAutenticacionFalso("token"));

        var respuesta = await cliente.MonitoresAsync(Cancelacion);

        respuesta.Error!.Codigo.ShouldBe("respuesta_ilegible");
    }

    [Fact]
    public async Task Un_error_500_sin_formato_se_explica_con_su_codigo_http()
    {
        var http = new HttpClient(new ManejadorQueDevuelve(HttpStatusCode.InternalServerError, "<html>boom</html>")) { BaseAddress = new Uri("http://api.prueba/") };
        var cliente = new ClienteDeApi(http, new EstadoDeAutenticacionFalso("token"));

        var respuesta = await cliente.MonitoresAsync(Cancelacion);

        respuesta.Error!.Codigo.ShouldBe("http_500");
        respuesta.Error.Mensaje.ShouldNotContain("boom");
    }

    private sealed class ManejadorQueFalla : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("conexión rechazada");
    }

    private sealed class ManejadorQueDevuelve(HttpStatusCode estado, string cuerpo) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(estado) { Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "text/plain") });
    }
}

public class TiempoRealIntegracionTests : IntegracionTest
{
    private ConexionEnVivo Conexion(string? token) =>
        new(Options.Create(new OpcionesApi { Url = new Uri("http://api.prueba/") }), new EstadoDeAutenticacionFalso(token), () => Entorno.Api.Server.CreateHandler());

    private async Task<string> TokenAsync() =>
        (await new ClienteDeApi(new HttpClient(Entorno.Api.Server.CreateHandler()) { BaseAddress = new Uri("http://api.prueba/") }, new EstadoDeAutenticacionFalso(null)).AccederAsync(FabricaDeLaApi.Contrasena, Cancelacion)).Valor!.Token;

    private async Task NotificarAsync(ComprobacionPublicada carga)
    {
        await using var db = Entorno.NuevoContexto();
        await db.NotificarAsync(Notificaciones.ComprobacionGuardada, carga.Serializar(), Cancelacion);
    }

    [Fact]
    public async Task Lo_que_publica_el_worker_llega_a_la_web_con_el_mismo_formato_de_punta_a_punta()
    {
        await using var conexion = Conexion(await TokenAsync());
        var recibidas = new ConcurrentQueue<ComprobacionEnVivo>();
        conexion.Recibida += mensaje =>
        {
            recibidas.Enqueue(mensaje);

            return Task.CompletedTask;
        };
        await conexion.IniciarAsync(Cancelacion);
        conexion.Conectada.ShouldBeTrue();
        var id = Guid.NewGuid();
        var carga = new ComprobacionPublicada(id, Entorno.Reloj.GetUtcNow(), false, 10000, EstadoMonitor.Caido, "abierto");

        // El oyente de la API arranca a la vez que ella: se insiste hasta que llega el primero.
        var limite = DateTime.UtcNow.AddSeconds(15);

        while (recibidas.IsEmpty && DateTime.UtcNow < limite)
        {
            await NotificarAsync(carga);
            await Task.Delay(150, Cancelacion);
        }

        var mensaje = recibidas.ShouldHaveSingleItem().ShouldNotBeNull();
        mensaje.MonitorId.ShouldBe(id);
        mensaje.Correcto.ShouldBeFalse();
        mensaje.LatenciaMs.ShouldBe(10000);
        mensaje.Estado.ShouldBe(EstadoMonitor.Caido);
        mensaje.Incidente.ShouldBe("abierto");
    }

    [Fact]
    public async Task Sin_token_no_hay_tiempo_real_pero_el_panel_no_se_rompe()
    {
        var estados = new List<bool>();
        await using var conexion = Conexion(token: null);
        conexion.EstadoCambiado += conectada =>
        {
            estados.Add(conectada);

            return Task.CompletedTask;
        };

        await conexion.IniciarAsync(Cancelacion);

        conexion.Conectada.ShouldBeFalse();
        estados.ShouldBe([false]);
    }

    [Fact]
    public async Task Iniciar_dos_veces_no_abre_dos_conexiones()
    {
        await using var conexion = Conexion(await TokenAsync());

        await conexion.IniciarAsync(Cancelacion);
        await conexion.IniciarAsync(Cancelacion);

        conexion.Conectada.ShouldBeTrue();
    }
}
