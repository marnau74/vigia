using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Vigia.Comprobaciones.Http;
using Vigia.Comprobaciones.Red;
using Vigia.Comprobaciones.Tests.Apoyo;
using Vigia.Dominio.Monitores;

namespace Vigia.Comprobaciones.Tests;

public class ComprobadorHttpTests : IDisposable
{
    private readonly Entorno _entorno = new();

    public void Dispose()
    {
        _entorno.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    private Task<ResultadoComprobacion> Comprobar(ConfiguracionHttp configuracion) =>
        _entorno.Obtener<ComprobadorHttp>().ComprobarAsync(configuracion, Cancelacion);

    private static ConfiguracionHttp Local(int puerto, string ruta = "/") =>
        new(new Uri($"http://127.0.0.1:{puerto}{ruta}")) { PermitirRedPrivada = true };

    // --- Éxito -------------------------------------------------------------------------------

    [Fact]
    public async Task Una_web_que_responde_200_es_correcta_y_devuelve_los_detalles()
    {
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", () => "hola mundo"));

        var resultado = await Comprobar(Local(servidor.Puerto));

        resultado.Correcto.ShouldBeTrue();
        resultado.Fallo.ShouldBe(TipoFallo.Ninguno);
        resultado.Error.ShouldBeNull();
        resultado.Detalles["codigo"].ShouldBe("200");
        resultado.Detalles["redirecciones"].ShouldBe("0");
        resultado.Detalles.ShouldContainKey("primer_byte_ms");
        resultado.Detalles.ShouldContainKey("dns_ms");
        resultado.Detalles.ShouldContainKey("conexion_ms");
    }

    [Fact]
    public async Task Se_identifica_con_su_propio_user_agent()
    {
        string? recibido = null;
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", (HttpContext c) =>
        {
            recibido = c.Request.Headers.UserAgent.ToString();
            return "ok";
        }));

        await Comprobar(Local(servidor.Puerto));

        recibido.ShouldBe("Vigia/1.0");
    }

    [Fact]
    public async Task No_envia_cookies_ni_usa_el_proxy_del_sistema()
    {
        string? cookie = null;
        await using var servidor = await ServidorWeb.IniciarAsync(app =>
        {
            app.MapGet("/", (HttpContext c) =>
            {
                c.Response.Cookies.Append("sesion", "abc");
                return "ok";
            });
            app.MapGet("/otra", (HttpContext c) =>
            {
                cookie = c.Request.Headers.Cookie.ToString();
                return "ok";
            });
        });

        await Comprobar(Local(servidor.Puerto));
        await Comprobar(Local(servidor.Puerto, "/otra"));

        cookie.ShouldBeNullOrEmpty();
    }

    // --- Códigos esperados -------------------------------------------------------------------

    [Theory]
    [InlineData(404, "100-399", false)]
    [InlineData(500, "100-399", false)]
    [InlineData(404, "404", true)]
    [InlineData(204, "200-299", true)]
    [InlineData(503, "200,503", true)]
    [InlineData(200, "201-299", false)]
    public async Task El_codigo_de_la_respuesta_se_compara_con_los_esperados(int codigo, string esperados, bool correcto)
    {
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", () => Results.StatusCode(codigo)));
        CodigosHttpEsperados.TryParse(esperados, out var codigos).ShouldBeTrue();

        var resultado = await Comprobar(Local(servidor.Puerto) with { CodigosEsperados = codigos });

        resultado.Correcto.ShouldBe(correcto);

        if (!correcto)
        {
            resultado.Fallo.ShouldBe(TipoFallo.CodigoInesperado);
            resultado.Error!.ShouldContain(codigo.ToString(System.Globalization.CultureInfo.InvariantCulture));
            resultado.Error!.ShouldContain(esperados);
        }
    }

    // --- Palabra clave -----------------------------------------------------------------------

    [Fact]
    public async Task La_palabra_clave_se_busca_sin_distinguir_mayusculas()
    {
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", () => "<h1>Bienvenido a MI TIENDA</h1>"));

        (await Comprobar(Local(servidor.Puerto) with { PalabraClave = "mi tienda" })).Correcto.ShouldBeTrue();
    }

    [Fact]
    public async Task Si_la_palabra_no_esta_es_un_fallo_aunque_el_codigo_sea_200()
    {
        // El caso típico: la web «responde» pero muestra una página de error de la base de datos con código 200.
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", () => "Error al conectar con la base de datos"));

        var resultado = await Comprobar(Local(servidor.Puerto) with { PalabraClave = "Bienvenido" });

        resultado.Correcto.ShouldBeFalse();
        resultado.Fallo.ShouldBe(TipoFallo.PalabraClaveAusente);
        resultado.Error!.ShouldContain("Bienvenido");
        resultado.Detalles["codigo"].ShouldBe("200");
    }

    [Fact]
    public async Task Buscar_una_palabra_fuerza_get_aunque_se_pidiera_head()
    {
        string? metodo = null;
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapMethods("/", ["GET", "HEAD"], (HttpContext c) =>
        {
            metodo = c.Request.Method;
            return "contenido con palabra";
        }));

        var resultado = await Comprobar(Local(servidor.Puerto) with { Metodo = "HEAD", PalabraClave = "palabra" });

        resultado.Correcto.ShouldBeTrue();
        metodo.ShouldBe("GET");
    }

    [Fact]
    public async Task Sin_palabra_clave_se_puede_usar_head_y_no_se_descarga_el_cuerpo()
    {
        string? metodo = null;
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapMethods("/", ["GET", "HEAD"], (HttpContext c) =>
        {
            metodo = c.Request.Method;
            return "contenido";
        }));

        var resultado = await Comprobar(Local(servidor.Puerto) with { Metodo = "HEAD" });

        resultado.Correcto.ShouldBeTrue();
        metodo.ShouldBe("HEAD");
    }

    [Fact]
    public async Task Solo_se_lee_el_primer_megabyte_de_la_respuesta()
    {
        // La palabra está después del primer megabyte: no se descarga una respuesta enorme para buscarla.
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", () => new string('a', ComprobadorHttp.MaximoBytesLeidos + 1000) + "escondida"));

        var resultado = await Comprobar(Local(servidor.Puerto) with { PalabraClave = "escondida" });

        resultado.Fallo.ShouldBe(TipoFallo.PalabraClaveAusente);
    }

    [Fact]
    public async Task Una_palabra_partida_entre_dos_bloques_de_lectura_se_encuentra()
    {
        var relleno = new string('x', 16 * 1024 - 3);
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", () => relleno + "PALABRA" + "yyy"));

        (await Comprobar(Local(servidor.Puerto) with { PalabraClave = "palabra" })).Correcto.ShouldBeTrue();
    }

    [Fact]
    public async Task Una_letra_de_varios_bytes_partida_entre_dos_lecturas_no_se_pierde()
    {
        // «ñ» en UTF-8 son dos bytes (C3 B1): el servidor envía el primero, espera y envía el segundo, así que llegan
        // en lecturas distintas. Descodificar cada lectura por separado convertía la «ñ» en dos caracteres basura.
        var utf8 = System.Text.Encoding.UTF8.GetBytes("Bienvenido a España");
        var corte = Array.IndexOf(utf8, (byte)0xC3) + 1;

        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", async (HttpContext contexto) =>
        {
            contexto.Response.ContentType = "text/html; charset=utf-8";
            await contexto.Response.Body.WriteAsync(utf8.AsMemory(0, corte));
            await contexto.Response.Body.FlushAsync();
            await Task.Delay(200);
            await contexto.Response.Body.WriteAsync(utf8.AsMemory(corte));
        }));

        (await Comprobar(Local(servidor.Puerto) with { PalabraClave = "España" })).Correcto.ShouldBeTrue();
    }

    [Fact]
    public async Task La_palabra_se_busca_con_la_codificacion_que_declara_la_respuesta()
    {
        // Una página antigua en Latin-1: la «ñ» es un solo byte (F1), que en UTF-8 no significa nada.
        var latin1 = System.Text.Encoding.Latin1.GetBytes("Bienvenido a España");

        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", () => Results.Bytes(latin1, "text/html; charset=iso-8859-1")));

        (await Comprobar(Local(servidor.Puerto) with { PalabraClave = "España" })).Correcto.ShouldBeTrue();
    }

    // --- Redirecciones -----------------------------------------------------------------------

    [Fact]
    public async Task Las_redirecciones_se_siguen_y_se_evalua_la_respuesta_final()
    {
        await using var servidor = await ServidorWeb.IniciarAsync(app =>
        {
            app.MapGet("/viejo", () => Results.Redirect("/medio", permanent: true));
            app.MapGet("/medio", () => Results.Redirect("/nuevo"));
            app.MapGet("/nuevo", () => "destino final");
        });

        var resultado = await Comprobar(Local(servidor.Puerto, "/viejo") with { CodigosEsperados = Codigos("200") });

        resultado.Correcto.ShouldBeTrue();
        resultado.Detalles["redirecciones"].ShouldBe("2");
        resultado.Detalles["url_final"].ShouldEndWith("/nuevo");
    }

    [Fact]
    public async Task Un_bucle_de_redirecciones_se_corta_en_el_maximo()
    {
        var visitas = 0;
        await using var servidor = await ServidorWeb.IniciarAsync(app =>
        {
            app.MapGet("/a", () =>
            {
                Interlocked.Increment(ref visitas);
                return Results.Redirect("/b");
            });
            app.MapGet("/b", () =>
            {
                Interlocked.Increment(ref visitas);
                return Results.Redirect("/a");
            });
        });

        var resultado = await Comprobar(Local(servidor.Puerto, "/a") with { MaxRedirecciones = 3 }).WaitAsync(TimeSpan.FromSeconds(20), Cancelacion);

        resultado.Correcto.ShouldBeFalse();
        resultado.Error!.ShouldContain("3 redirecciones");
        resultado.Detalles["redirecciones"].ShouldBe("3");
        visitas.ShouldBe(4, "la petición inicial y tres redirecciones, y ni una más");
    }

    [Fact]
    public async Task Una_redireccion_a_otro_esquema_se_bloquea()
    {
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", (HttpContext c) =>
        {
            c.Response.StatusCode = 302;
            c.Response.Headers.Location = "ftp://interno.example/secreto";
        }));

        var resultado = await Comprobar(Local(servidor.Puerto));

        resultado.Fallo.ShouldBe(TipoFallo.DestinoBloqueado);
        resultado.Error!.ShouldContain("ftp");
    }

    [Fact]
    public async Task Una_redireccion_a_los_metadatos_de_la_nube_se_bloquea_aunque_la_primera_direccion_fuera_publica()
    {
        // El ataque real: una web pública (que controla el atacante) redirige a http://169.254.169.254/latest/meta-data/.
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", () => Results.Redirect("http://metadatos.test/latest/meta-data/")));
        var conector = new ConectorHaciaPuerto(servidor.Puerto);
        using var entorno = new Entorno(servicios => servicios.AddSingleton<IConector>(conector));
        entorno.Resolvedor.Asignar("publico.test", "93.184.216.34").Asignar("metadatos.test", "169.254.169.254");

        var resultado = await entorno.Obtener<ComprobadorHttp>().ComprobarAsync(new ConfiguracionHttp(new Uri("http://publico.test/")), Cancelacion);

        resultado.Correcto.ShouldBeFalse();
        resultado.Fallo.ShouldBe(TipoFallo.DestinoBloqueado);
        resultado.Error!.ShouldContain("169.254.169.254");
        conector.Peticiones.Select(p => p.Direccion.ToString()).ShouldBe(["93.184.216.34"], "nunca se llegó a abrir una conexión a la dirección de metadatos");
    }

    // --- SSRF en la primera petición ---------------------------------------------------------

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://192.168.1.1/admin")]
    [InlineData("http://2130706433/")]
    public async Task Las_direcciones_internas_se_bloquean_sin_permiso_y_no_se_conecta(string url)
    {
        var conector = new ConectorColgado();
        using var entorno = new Entorno(s => s.AddSingleton<IConector>(conector));
        entorno.Resolvedor.Asignar("localhost", "127.0.0.1", "::1");

        var resultado = await entorno.Obtener<ComprobadorHttp>().ComprobarAsync(new ConfiguracionHttp(new Uri(url)), Cancelacion).WaitAsync(TimeSpan.FromSeconds(10), Cancelacion);

        resultado.Fallo.ShouldBe(TipoFallo.DestinoBloqueado);
        resultado.Error!.ShouldContain("Está prohibido");
        conector.Intentando.Task.IsCompleted.ShouldBeFalse("no se intentó ninguna conexión");
    }

    [Theory]
    [InlineData("ftp://ejemplo.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://ejemplo.com/")]
    public async Task Solo_se_admiten_direcciones_http_y_https(string url)
    {
        var resultado = await Comprobar(new ConfiguracionHttp(new Uri(url)));

        resultado.Fallo.ShouldBe(TipoFallo.DestinoBloqueado);
        resultado.Error!.ShouldContain("http y https");
    }

    [Fact]
    public async Task Una_direccion_con_usuario_y_contrasena_se_rechaza()
    {
        var resultado = await Comprobar(new ConfiguracionHttp(new Uri("https://admin:secreto@ejemplo.com/")));

        resultado.Fallo.ShouldBe(TipoFallo.DestinoBloqueado);
        resultado.Error!.ShouldContain("usuario");
    }

    [Fact]
    public async Task Con_el_permiso_de_red_privada_se_puede_vigilar_un_servicio_interno()
    {
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", () => "panel interno"));

        (await Comprobar(Local(servidor.Puerto))).Correcto.ShouldBeTrue();
    }

    [Fact]
    public async Task El_dns_rebinding_no_funciona_porque_se_conecta_a_la_direccion_ya_validada()
    {
        // Un DNS malicioso contesta una IP pública la primera vez y una interna la segunda. Como el
        // nombre se resuelve una sola vez y se conecta a esa dirección, la segunda respuesta no se usa nunca.
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", () => "ok"));
        var conector = new ConectorHaciaPuerto(servidor.Puerto);
        using var entorno = new Entorno(servicios => servicios.AddSingleton<IConector>(conector));
        entorno.Resolvedor.AsignarSecuencia("rebinding.test", n => [IPAddress.Parse(n == 1 ? "93.184.216.34" : "127.0.0.1")]);

        var resultado = await entorno.Obtener<ComprobadorHttp>().ComprobarAsync(new ConfiguracionHttp(new Uri("http://rebinding.test/")), Cancelacion);

        resultado.Correcto.ShouldBeTrue();
        entorno.Resolvedor.Llamadas("rebinding.test").ShouldBe(1);
        conector.Peticiones.Select(p => p.Direccion.ToString()).ShouldBe(["93.184.216.34"]);
    }

    // --- Fallos de red -----------------------------------------------------------------------

    [Fact]
    public async Task Un_puerto_cerrado_es_conexion_rechazada()
    {
        var resultado = await Comprobar(Local(PuertoCerrado.Nuevo()));

        resultado.Correcto.ShouldBeFalse();
        resultado.Fallo.ShouldBe(TipoFallo.ConexionRechazada);
        resultado.Error!.ShouldContain("rechazada");
    }

    [Fact]
    public async Task Un_nombre_que_no_existe_es_un_fallo_de_resolucion_dns()
    {
        var resultado = await Comprobar(new ConfiguracionHttp(new Uri("http://no-existe.test/")));

        resultado.Correcto.ShouldBeFalse();
        resultado.Fallo.ShouldBe(TipoFallo.ResolucionDns);
    }

    [Fact]
    public async Task Una_web_que_no_responde_agota_el_tiempo_maximo_sin_esperar_de_verdad()
    {
        var entro = new TaskCompletionSource();
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", async (HttpContext c) =>
        {
            entro.SetResult();
            await Task.Delay(Timeout.Infinite, c.RequestAborted);
        }));

        var tarea = Comprobar(Local(servidor.Puerto) with { TiempoMaximo = TimeSpan.FromSeconds(5) });
        await entro.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancelacion);

        _entorno.Reloj.Advance(TimeSpan.FromSeconds(5)); // el tiempo lo mueve el test
        var resultado = await tarea.WaitAsync(TimeSpan.FromSeconds(10), Cancelacion);

        resultado.Correcto.ShouldBeFalse();
        resultado.Fallo.ShouldBe(TipoFallo.TiempoAgotado);
        resultado.Error!.ShouldContain("5 s");
        resultado.Latencia.ShouldBe(TimeSpan.FromSeconds(5), "la latencia se mide con el reloj inyectado");
    }

    [Fact]
    public async Task Si_lo_cancela_quien_lo_lanza_no_es_un_fallo_sino_una_cancelacion()
    {
        var entro = new TaskCompletionSource();
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", async (HttpContext c) =>
        {
            entro.SetResult();
            await Task.Delay(Timeout.Infinite, c.RequestAborted);
        }));
        using var cancelacion = CancellationTokenSource.CreateLinkedTokenSource(Cancelacion);

        var tarea = _entorno.Obtener<ComprobadorHttp>().ComprobarAsync(Local(servidor.Puerto), cancelacion.Token);
        await entro.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancelacion);
        await cancelacion.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => tarea);
    }

    // --- HTTPS -------------------------------------------------------------------------------

    [Fact]
    public async Task Un_certificado_autofirmado_es_un_fallo_de_tls_salvo_que_el_monitor_no_lo_verifique()
    {
        using var certificado = Certificados.Autofirmado("127.0.0.1", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        await using var servidor = await ServidorWeb.IniciarAsync(app => app.MapGet("/", () => "seguro"), certificado);
        var url = new Uri($"https://127.0.0.1:{servidor.Puerto}/");

        var estricto = await Comprobar(new ConfiguracionHttp(url) { PermitirRedPrivada = true });
        var permisivo = await Comprobar(new ConfiguracionHttp(url) { PermitirRedPrivada = true, VerificarCertificado = false });

        estricto.Correcto.ShouldBeFalse();
        estricto.Fallo.ShouldBe(TipoFallo.Tls);
        permisivo.Correcto.ShouldBeTrue();
        permisivo.Detalles["codigo"].ShouldBe("200");
    }

    // --- Utilidades --------------------------------------------------------------------------

    private static CodigosHttpEsperados Codigos(string texto)
    {
        CodigosHttpEsperados.TryParse(texto, out var codigos).ShouldBeTrue();
        return codigos;
    }
}
