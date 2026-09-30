using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Shouldly;

using Vigia.Dominio.Avisos;
using Vigia.Worker.Avisos;

namespace Vigia.Worker.Tests;

/// <summary>Un servidor local que hace de API de Telegram: anota lo que recibe y responde lo que el test quiera.</summary>
public sealed class ServidorTelegramFalso : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ServidorTelegramFalso(WebApplication app, Uri url)
    {
        _app = app;
        Url = url;
    }

    public Uri Url { get; }

    public ConcurrentQueue<(string Ruta, JsonElement Cuerpo)> Peticiones { get; } = new();

    public int Estado { get; set; } = 200;

    public string Respuesta { get; set; } = """{"ok":true}""";

    public static async Task<ServidorTelegramFalso> IniciarAsync()
    {
        var constructor = WebApplication.CreateSlimBuilder();
        constructor.WebHost.UseUrls("http://127.0.0.1:0");
        var app = constructor.Build();

        ServidorTelegramFalso? servidor = null;

        app.MapPost("/{**ruta}", async (HttpContext contexto, string ruta) =>
        {
            var cuerpo = await JsonSerializer.DeserializeAsync<JsonElement>(contexto.Request.Body);
            servidor!.Peticiones.Enqueue((ruta, cuerpo));

            return Results.Content(servidor.Respuesta, "application/json", statusCode: servidor.Estado);
        });

        await app.StartAsync();
        servidor = new ServidorTelegramFalso(app, new Uri(app.Urls.First()));

        return servidor;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

public sealed class CanalTelegramTests : IAsyncLifetime
{
    private const string Token = "123456:TOKEN-SECRETO-DE-PRUEBA";

    private ServidorTelegramFalso _servidor = null!;
    private HttpClient _http = null!;

    private static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _servidor = await ServidorTelegramFalso.IniciarAsync();
        _http = new HttpClient();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _servidor.DisposeAsync();
    }

    private CanalTelegram Canal(string? token = Token, Uri? url = null) =>
        new(_http, Options.Create(new OpcionesDeAvisos { TokenTelegram = token, UrlTelegram = url ?? _servidor.Url }));

    private static readonly TextoDeAviso Texto = new("[Vigía] «Mi web» está caído", "«Mi web» no responde.\n\nCausa: Sin respuesta en 10 s.");

    [Fact]
    public async Task Envia_el_mensaje_al_chat_por_la_ruta_del_bot()
    {
        await Canal().EnviarAsync("99", Texto, Cancelacion);

        var (ruta, cuerpo) = _servidor.Peticiones.ShouldHaveSingleItem();
        ruta.ShouldBe($"bot{Token}/sendMessage");
        cuerpo.GetProperty("chat_id").GetString().ShouldBe("99");
        cuerpo.GetProperty("text").GetString().ShouldBe("[Vigía] «Mi web» está caído\n\n«Mi web» no responde.\n\nCausa: Sin respuesta en 10 s.");
    }

    [Fact]
    public async Task Un_mensaje_demasiado_largo_se_recorta_al_limite_de_telegram()
    {
        await Canal().EnviarAsync("99", new TextoDeAviso("Asunto", new string('x', 10_000)), Cancelacion);

        _servidor.Peticiones.Single().Cuerpo.GetProperty("text").GetString()!.Length.ShouldBe(CanalTelegram.LongitudMaxima);
    }

    [Fact]
    public async Task Un_rechazo_de_telegram_explica_el_motivo_sin_incluir_el_token()
    {
        _servidor.Estado = 403;
        _servidor.Respuesta = """{"ok":false,"error_code":403,"description":"Forbidden: bot was blocked by the user"}""";

        var error = await Should.ThrowAsync<AvisoNoEnviadoException>(() => Canal().EnviarAsync("99", Texto, Cancelacion));

        error.Message.ShouldBe("Telegram respondió 403: Forbidden: bot was blocked by the user");
        error.Message.ShouldNotContain(Token);
    }

    [Fact]
    public async Task Una_respuesta_de_error_sin_json_tambien_se_entiende()
    {
        _servidor.Estado = 502;
        _servidor.Respuesta = "<html>Bad gateway</html>";

        var error = await Should.ThrowAsync<AvisoNoEnviadoException>(() => Canal().EnviarAsync("99", Texto, Cancelacion));

        error.Message.ShouldStartWith("Telegram respondió 502");
        error.Message.ShouldNotContain("<html>");
    }

    [Fact]
    public async Task Si_no_se_puede_contactar_el_error_no_filtra_el_token()
    {
        // Un puerto en el que no escucha nadie.
        var error = await Should.ThrowAsync<AvisoNoEnviadoException>(() => Canal(url: new Uri("http://127.0.0.1:1/")).EnviarAsync("99", Texto, Cancelacion));

        error.Message.ShouldStartWith("No se pudo contactar con Telegram");
        error.Message.ShouldNotContain(Token);
        error.ToString().ShouldNotContain(Token, Case.Sensitive, "ni en la excepción completa");
    }

    [Fact]
    public async Task Sin_token_no_se_envia_nada()
    {
        var error = await Should.ThrowAsync<AvisoNoEnviadoException>(() => Canal(token: null).EnviarAsync("99", Texto, Cancelacion));

        error.Message.ShouldContain("TokenTelegram");
        _servidor.Peticiones.ShouldBeEmpty();
    }
}

/// <summary>El envío por SMTP de verdad, contra Mailpit (el mismo servidor de pruebas que usa el entorno local): se envía y se lee de su bandeja.</summary>
public sealed class CanalCorreoTests : IAsyncLifetime
{
    private const int PuertoSmtp = 1025;
    private const int PuertoWeb = 8025;

    private readonly IContainer _mailpit = new ContainerBuilder("axllent/mailpit:latest")
        .WithPortBinding(PuertoSmtp, assignRandomHostPort: true)
        .WithPortBinding(PuertoWeb, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(PuertoWeb).ForPath("/api/v1/info")))
        .Build();

    private readonly HttpClient _http = new();

    private static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _mailpit.StartAsync();
        _http.BaseAddress = new Uri($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(PuertoWeb)}");
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _mailpit.DisposeAsync();
    }

    private CanalCorreo Canal(int? puerto = null) => new(Options.Create(new OpcionesSmtp
    {
        Servidor = _mailpit.Hostname,
        Puerto = puerto ?? _mailpit.GetMappedPublicPort(PuertoSmtp),
        Remitente = "vigia@monitor.example",
        NombreRemitente = "Vigía",
    }));

    [Fact]
    public async Task Un_aviso_llega_a_la_bandeja_con_asunto_texto_y_acentos()
    {
        var texto = new TextoDeAviso("[Vigía] «Mi web» está caído", "«Mi web» no responde.\n\nCaído desde: 2026-10-01 01:14:05 UTC\nCausa: Conexión rechazada");

        await Canal().EnviarAsync("guardia@ejemplo.com", texto, Cancelacion);

        var lista = await _http.GetFromJsonAsync<JsonElement>("/api/v1/messages", Cancelacion);
        lista.GetProperty("total").GetInt32().ShouldBe(1);

        var resumen = lista.GetProperty("messages")[0];
        resumen.GetProperty("Subject").GetString().ShouldBe("[Vigía] «Mi web» está caído");
        resumen.GetProperty("To")[0].GetProperty("Address").GetString().ShouldBe("guardia@ejemplo.com");
        resumen.GetProperty("From").GetProperty("Address").GetString().ShouldBe("vigia@monitor.example");
        resumen.GetProperty("From").GetProperty("Name").GetString().ShouldBe("Vigía");

        var mensaje = await _http.GetFromJsonAsync<JsonElement>($"/api/v1/message/{resumen.GetProperty("ID").GetString()}", Cancelacion);
        mensaje.GetProperty("Text").GetString()!.ShouldContain("Causa: Conexión rechazada");
        mensaje.GetProperty("Text").GetString()!.ShouldContain("«Mi web» no responde.");
    }

    [Fact]
    public async Task Una_direccion_que_no_es_un_correo_falla_con_un_error_de_aviso()
    {
        var error = await Should.ThrowAsync<AvisoNoEnviadoException>(() => Canal().EnviarAsync("esto no es un correo", new TextoDeAviso("a", "b"), Cancelacion));

        error.Message.ShouldStartWith("No se pudo enviar el correo");
    }

    [Fact]
    public async Task Un_servidor_que_no_responde_falla_con_un_error_de_aviso_y_no_con_una_excepcion_de_red()
    {
        var error = await Should.ThrowAsync<AvisoNoEnviadoException>(() => Canal(puerto: 1).EnviarAsync("guardia@ejemplo.com", new TextoDeAviso("a", "b"), Cancelacion));

        error.Message.ShouldStartWith("No se pudo enviar el correo");
    }

    [Fact]
    public async Task Sin_servidor_configurado_no_se_intenta_enviar()
    {
        var canal = new CanalCorreo(Options.Create(new OpcionesSmtp()));

        var error = await Should.ThrowAsync<AvisoNoEnviadoException>(() => canal.EnviarAsync("guardia@ejemplo.com", new TextoDeAviso("a", "b"), Cancelacion));

        error.Message.ShouldContain("Correo:Servidor");
    }

    [Fact]
    public async Task Una_cancelacion_no_se_confunde_con_un_fallo_del_servidor()
    {
        using var cancelada = new CancellationTokenSource();
        await cancelada.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Canal().EnviarAsync("guardia@ejemplo.com", new TextoDeAviso("a", "b"), cancelada.Token));
    }
}

public class DestinosDeAvisoTests
{
    private static DestinosDeAviso Destinos(OpcionesDeAvisos avisos, OpcionesSmtp? correo = null) =>
        new(Options.Create(avisos), Options.Create(correo ?? new OpcionesSmtp()));

    [Fact]
    public void Sin_nada_configurado_no_hay_destinos()
    {
        Destinos(new OpcionesDeAvisos()).Lista.ShouldBeEmpty();
    }

    [Fact]
    public void Los_destinatarios_de_correo_solo_cuentan_si_hay_servidor()
    {
        var avisos = new OpcionesDeAvisos { Destinatarios = ["a@ejemplo.com"] };

        Destinos(avisos).Lista.ShouldBeEmpty();
        Destinos(avisos, new OpcionesSmtp { Servidor = "smtp.ejemplo.com" }).Lista.ShouldBe([new DestinoDeAviso(CanalAviso.Correo, "a@ejemplo.com")]);
    }

    [Fact]
    public void Los_chats_de_telegram_solo_cuentan_si_hay_token()
    {
        Destinos(new OpcionesDeAvisos { ChatsTelegram = ["1"] }).Lista.ShouldBeEmpty();
        Destinos(new OpcionesDeAvisos { ChatsTelegram = ["1"], TokenTelegram = "t" }).Lista.ShouldBe([new DestinoDeAviso(CanalAviso.Telegram, "1")]);
    }

    [Fact]
    public void Se_limpian_los_vacios_los_espacios_y_los_repetidos()
    {
        var avisos = new OpcionesDeAvisos { Destinatarios = [" a@ejemplo.com ", "", "A@EJEMPLO.COM", "  ", "b@ejemplo.com"] };

        Destinos(avisos, new OpcionesSmtp { Servidor = "s" }).Lista.Select(d => d.Direccion).ShouldBe(["a@ejemplo.com", "b@ejemplo.com"]);
    }
}
