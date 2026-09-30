using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Vigia.Api.Seguridad;
using Vigia.Datos.Persistencia;
using Vigia.Tests.Comunes;
using Vigia.Web.Servicios;

namespace Vigia.Web.Tests;

public static class ServidorCompartido
{
    private static readonly SemaphoreSlim Arranque = new(1, 1);
    private static ServidorPostgres? _servidor;

    public static async Task<string> NuevaBaseDeDatosAsync()
    {
        await Arranque.WaitAsync();

        try
        {
            _servidor ??= new ServidorPostgres();
        }
        finally
        {
            Arranque.Release();
        }

        return await _servidor.CrearBaseDeDatosAsync();
    }
}

public sealed class FabricaDeLaApi(string cadena, FakeTimeProvider reloj) : WebApplicationFactory<Vigia.Api.ReferenciaApi>
{
    public const string Contrasena = "una-contrasena-de-prueba-larga";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("ConnectionStrings:vigia", cadena);
        builder.UseSetting("Acceso:HashContrasena", Contrasenas.Hash(Contrasena));
        builder.UseSetting("Acceso:ClaveJwt", "clave-de-firma-de-pruebas-de-al-menos-32-caracteres");
        builder.ConfigureTestServices(servicios =>
        {
            servicios.RemoveAll<TimeProvider>();
            servicios.AddSingleton<TimeProvider>(reloj);
        });
    }
}

/// <summary>La web de verdad (Kestrel en memoria) hablando con la API de verdad, y esta con PostgreSQL: de punta a punta.</summary>
public sealed class EntornoCompleto : IAsyncDisposable
{
    private EntornoCompleto(string cadena, FakeTimeProvider reloj)
    {
        Cadena = cadena;
        Reloj = reloj;
        Api = new FabricaDeLaApi(cadena, reloj);
        Web = new WebApplicationFactory<ReferenciaWeb>().WithWebHostBuilder(constructor =>
        {
            constructor.UseSetting("Api:Url", "http://api.prueba/");
            constructor.ConfigureTestServices(servicios =>
            {
                servicios.RemoveAll<TimeProvider>();
                servicios.AddSingleton<TimeProvider>(reloj);

                // La web habla con la API por la memoria del servidor de pruebas, sin red.
                servicios.AddHttpClient<IClienteDeApi, ClienteDeApi>().ConfigurePrimaryHttpMessageHandler(() => Api.Server.CreateHandler());
                servicios.AddKeyedSingleton<Func<HttpMessageHandler>>("manejador-tiempo-real", (_, _) => () => Api.Server.CreateHandler());
            });
        });
    }

    public string Cadena { get; }

    public FakeTimeProvider Reloj { get; }

    public FabricaDeLaApi Api { get; }

    public WebApplicationFactory<ReferenciaWeb> Web { get; }

    public static async Task<EntornoCompleto> CrearAsync()
    {
        var entorno = new EntornoCompleto(await ServidorCompartido.NuevaBaseDeDatosAsync(), new FakeTimeProvider(DateTimeOffset.UtcNow));

        await using var db = entorno.NuevoContexto();
        await new Particiones(db).AsegurarAsync(entorno.Reloj.GetUtcNow(), TestContext.Current.CancellationToken);

        return entorno;
    }

    public VigiaDbContext NuevoContexto() => ServidorPostgres.CrearContexto(Cadena);

    /// <summary>Un cliente de la web que no sigue redirecciones (para verlas) y conserva las cookies que se le den a mano.</summary>
    public HttpClient ClienteWeb() => Web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    /// <summary>La cabecera «Cookie» con lo que dejaron las respuestas anteriores.</summary>
    public static string Galleta(params HttpResponseMessage[] respuestas) =>
        string.Join("; ", respuestas.SelectMany(r => r.Headers.TryGetValues("Set-Cookie", out var valores) ? valores : [])
            .Select(v => v.Split(';')[0])
            .Where(v => !v.EndsWith('=')));

    public static string? LeerToken(string html) =>
        Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1] is { Success: true } g ? g.Value : null;

    /// <summary>Entra por el formulario y devuelve la cookie de sesión.</summary>
    public static async Task<string> EntrarAsync(HttpClient cliente, string? volver = null)
    {
        var ct = TestContext.Current.CancellationToken;
        using var formulario = await cliente.GetAsync("/entrar", ct);
        var token = LeerToken(await formulario.Content.ReadAsStringAsync(ct))!;

        using var peticion = new HttpRequestMessage(HttpMethod.Post, volver is null ? "/entrar" : $"/entrar?volver={Uri.EscapeDataString(volver)}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Modelo.Contrasena"] = FabricaDeLaApi.Contrasena,
                ["_handler"] = "entrar",
            }),
        };
        peticion.Headers.Add("Cookie", Galleta(formulario));
        using var respuesta = await cliente.SendAsync(peticion, ct);

        return Galleta(formulario, respuesta);
    }

    public async ValueTask DisposeAsync()
    {
        await Web.DisposeAsync();
        await Api.DisposeAsync();
    }
}

/// <summary>Un proveedor de estado de autenticación con (o sin) el token de la API, como el que tiene un circuito de Blazor con sesión.</summary>
public sealed class EstadoDeAutenticacionFalso(string? token) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var identidad = token is null
            ? new System.Security.Claims.ClaimsIdentity()
            : new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(ClienteDeApi.ClaimDelToken, token)], "prueba");

        return Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(identidad)));
    }
}
