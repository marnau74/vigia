using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Shouldly;

using Vigia.Api.Seguridad;

namespace Vigia.Api.Tests;

public class ContrasenasTests
{
    [Fact]
    public void Un_hash_verifica_la_contrasena_correcta_y_solo_esa()
    {
        var hash = Contrasenas.Hash("correcta-horse-battery");

        Contrasenas.Verificar(hash, "correcta-horse-battery").ShouldBeTrue();
        Contrasenas.Verificar(hash, "Correcta-horse-battery").ShouldBeFalse();
        Contrasenas.Verificar(hash, string.Empty).ShouldBeFalse();
    }

    [Fact]
    public void Dos_hashes_de_la_misma_contrasena_son_distintos_por_la_sal()
    {
        Contrasenas.Hash("misma").ShouldNotBe(Contrasenas.Hash("misma"));
    }

    [Fact]
    public void El_hash_no_contiene_la_contrasena()
    {
        Contrasenas.Hash("secreto-visible").ShouldNotContain("secreto-visible");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-es-un-hash")]
    [InlineData("AQAAAAEAACcQ")]
    public void Un_hash_mal_copiado_no_deja_entrar_ni_rompe_nada(string hash)
    {
        Contrasenas.Verificar(hash, "lo-que-sea").ShouldBeFalse();
    }

    [Fact]
    public void No_se_puede_hashear_una_contrasena_vacia()
    {
        Should.Throw<ArgumentException>(() => Contrasenas.Hash(string.Empty));
    }
}

public class AccesoTests
{
    private static readonly string[] Publicos = ["/api/acceso", "/api/publico/estado/{slug}"];

    private static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> Entrar(HttpClient cliente, string? contrasena) =>
        cliente.PostAsJsonAsync("/api/acceso", new { contrasena }, Cancelacion);

    private static string TokenCon(string clave, string emisor = "vigia", string audiencia = OpcionesAcceso.Audiencia, string algoritmo = SecurityAlgorithms.HmacSha256, DateTime? expira = null)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", "admin")]),
            Issuer = emisor,
            Audience = audiencia,
            Expires = expira ?? FabricaDeApi.Inicio.UtcDateTime.AddMinutes(10),
            NotBefore = FabricaDeApi.Inicio.UtcDateTime.AddMinutes(-1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(clave)), algoritmo),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static async Task<HttpStatusCode> ListarConAsync(FabricaDeApi fabrica, string token)
    {
        using var cliente = fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var respuesta = await cliente.GetAsync("/api/monitores", Cancelacion);

        return respuesta.StatusCode;
    }

    [Fact]
    public async Task Con_la_contrasena_correcta_se_recibe_un_token_que_abre_la_api()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();
        using var cliente = fabrica.CreateClient();

        using var respuesta = await Entrar(cliente, FabricaDeApi.ContrasenaDePrueba);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>(Cancelacion);
        cuerpo.GetProperty("expiraEn").GetDateTimeOffset().ShouldBe(FabricaDeApi.Inicio.AddHours(1));

        (await ListarConAsync(fabrica, cuerpo.GetProperty("token").GetString()!)).ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("otra-contrasena")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Una_contrasena_incorrecta_da_401_con_el_mismo_mensaje_para_todas(string? contrasena)
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();
        using var cliente = fabrica.CreateClient();

        using var respuesta = await Entrar(cliente, contrasena);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>(Cancelacion);
        problema.GetProperty("codigo").GetString().ShouldBe("acceso.contrasena_incorrecta");
        problema.GetProperty("title").GetString().ShouldBe("Contraseña incorrecta.");
    }

    [Fact]
    public async Task Sin_contrasena_configurada_no_se_puede_entrar_y_se_dice_por_que()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync(conAcceso: false);
        using var cliente = fabrica.CreateClient();

        using var respuesta = await Entrar(cliente, "cualquiera");

        respuesta.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await respuesta.Content.ReadFromJsonAsync<JsonElement>(Cancelacion)).GetProperty("codigo").GetString().ShouldBe("acceso.no_configurado");
    }

    [Fact]
    public async Task Tras_cinco_intentos_en_un_minuto_se_bloquea_la_fuerza_bruta()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();
        using var cliente = fabrica.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            using var intento = await Entrar(cliente, "equivocada");
            intento.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var sexto = await Entrar(cliente, FabricaDeApi.ContrasenaDePrueba);

        sexto.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "ni siquiera la contraseña buena entra durante el bloqueo");
    }

    // --- Los tokens ----------------------------------------------------------------------------------

    [Fact]
    public async Task Sin_token_la_api_privada_responde_401()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();
        using var cliente = fabrica.CreateClient();

        (await cliente.GetAsync("/api/monitores", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Un_token_caduca_a_la_hora()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();
        using var cliente = await fabrica.ClienteAutenticadoAsync();
        (await cliente.GetAsync("/api/monitores", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.OK);

        fabrica.Reloj.Advance(TimeSpan.FromMinutes(59));
        (await cliente.GetAsync("/api/monitores", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.OK);

        fabrica.Reloj.Advance(TimeSpan.FromMinutes(2));
        (await cliente.GetAsync("/api/monitores", Cancelacion)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Un_token_de_prueba_bien_formado_y_con_la_clave_buena_es_aceptado()
    {
        // Control de los casos negativos de abajo: si este fallara, los demás pasarían por la razón equivocada.
        await using var fabrica = await FabricaDeApi.CrearAsync();

        (await ListarConAsync(fabrica, TokenCon(FabricaDeApi.ClaveDePrueba))).ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("otra-clave-de-firma-distinta-de-al-menos-32-caracteres", "vigia", OpcionesAcceso.Audiencia)]
    [InlineData(FabricaDeApi.ClaveDePrueba, "otro-emisor", OpcionesAcceso.Audiencia)]
    [InlineData(FabricaDeApi.ClaveDePrueba, "vigia", "otra-audiencia")]
    public async Task Un_token_firmado_con_otra_clave_o_para_otro_emisor_o_audiencia_se_rechaza(string clave, string emisor, string audiencia)
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();

        (await ListarConAsync(fabrica, TokenCon(clave, emisor, audiencia))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Un_token_firmado_con_otro_algoritmo_se_rechaza()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();

        (await ListarConAsync(fabrica, TokenCon(FabricaDeApi.ClaveDePrueba, algoritmo: SecurityAlgorithms.HmacSha384))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Un_token_sin_firma_alg_none_se_rechaza()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();

        static string Base64(string texto) => Convert.ToBase64String(Encoding.UTF8.GetBytes(texto)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expira = new DateTimeOffset(FabricaDeApi.Inicio.UtcDateTime.AddMinutes(10)).ToUnixTimeSeconds();
        var cabecera = Base64("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var carga = Base64($"{{\"sub\":\"admin\",\"iss\":\"vigia\",\"aud\":\"vigia-panel\",\"exp\":{expira}}}");

        (await ListarConAsync(fabrica, $"{cabecera}.{carga}.")).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Un_token_caducado_firmado_con_la_clave_buena_tambien_se_rechaza()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();

        (await ListarConAsync(fabrica, TokenCon(FabricaDeApi.ClaveDePrueba, expira: FabricaDeApi.Inicio.UtcDateTime.AddMinutes(-5)))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Un_texto_que_no_es_un_token_se_rechaza()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();

        (await ListarConAsync(fabrica, "esto.no.es-un-token")).ShouldBe(HttpStatusCode.Unauthorized);
    }

    // --- La garantía de conjunto -------------------------------------------------------------------------

    [Fact]
    public async Task Todos_los_endpoints_de_la_api_menos_los_publicos_exigen_sesion()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();
        using var cliente = fabrica.CreateClient();
        var rutas = fabrica.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal))
            .ToList();

        rutas.Count.ShouldBeGreaterThan(20, "si la lista está vacía, este test no prueba nada");

        foreach (var ruta in rutas.Where(r => !Publicos.Contains(r.RoutePattern.RawText)))
        {
            var url = Regex.Replace(ruta.RoutePattern.RawText!, @"\{[^}]+\}", Guid.NewGuid().ToString());

            foreach (var metodo in ruta.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            {
                using var peticion = new HttpRequestMessage(new HttpMethod(metodo), url) { Content = JsonContent.Create(new { }) };
                using var respuesta = await cliente.SendAsync(peticion, Cancelacion);

                respuesta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{metodo} {ruta.RoutePattern.RawText} debería exigir sesión");
            }
        }
    }

    [Fact]
    public async Task El_hub_de_tiempo_real_tambien_exige_sesion()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();
        using var cliente = fabrica.CreateClient();

        using var respuesta = await cliente.PostAsync("/hubs/panel/negotiate?negotiateVersion=1", content: null, Cancelacion);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Las_rutas_que_no_existen_bajo_api_no_revelan_nada_sin_sesion()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();
        using var cliente = fabrica.CreateClient();

        (await cliente.GetAsync("/api/no-existe", Cancelacion)).StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);
    }
}
