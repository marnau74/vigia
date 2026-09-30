using System.Net;
using System.Net.NetworkInformation;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Vigia.Comprobaciones.Dns;
using Vigia.Comprobaciones.Red;
using Vigia.Comprobaciones.Tests.Apoyo;
using Vigia.Comprobaciones.Tls;
using Vigia.Dominio.Monitores;

namespace Vigia.Comprobaciones.Tests;

public sealed class ComprobadorTlsTests : IDisposable
{
    private readonly Entorno _entorno = new();

    private static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _entorno.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<ResultadoComprobacion> Comprobar(ConfiguracionTls configuracion) =>
        _entorno.Obtener<ComprobadorTls>().ComprobarAsync(configuracion, Cancelacion);

    private static ConfiguracionTls Contra(ServidorWeb servidor, string host = "127.0.0.1") =>
        new(host) { Puerto = servidor.Puerto, PermitirRedPrivada = true, VerificarCertificado = false };

    private X509Certificado Certificado(string nombre = "127.0.0.1", int diasDesde = -1, int diasHasta = 30) =>
        new(Certificados.Autofirmado(nombre, _entorno.Reloj.GetUtcNow().AddDays(diasDesde), _entorno.Reloj.GetUtcNow().AddDays(diasHasta)));

    [Fact]
    public async Task Un_certificado_valido_devuelve_los_dias_que_le_quedan_el_asunto_y_el_protocolo()
    {
        using var certificado = Certificado(diasHasta: 30);
        await using var servidor = await ServidorWeb.IniciarAsync(app => { }, certificado.Valor);

        var resultado = await Comprobar(Contra(servidor));

        resultado.Correcto.ShouldBeTrue();
        resultado.Detalles["asunto"].ShouldBe("127.0.0.1");
        resultado.Detalles["dias_restantes"].ShouldBe("29");
        resultado.Detalles["protocolo"].ShouldStartWith("Tls");
        resultado.Detalles["caduca"].ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Los_dias_restantes_se_cuentan_con_el_reloj_inyectado()
    {
        using var certificado = Certificado(diasHasta: 30);
        await using var servidor = await ServidorWeb.IniciarAsync(app => { }, certificado.Valor);

        _entorno.Reloj.Advance(TimeSpan.FromDays(24));
        var resultado = await Comprobar(Contra(servidor));

        resultado.Correcto.ShouldBeTrue();
        resultado.Detalles["dias_restantes"].ShouldBe("5");
    }

    [Fact]
    public async Task Un_certificado_caducado_es_un_fallo_con_la_fecha_en_el_mensaje_aunque_no_se_verifique_la_cadena()
    {
        using var certificado = Certificado(diasDesde: -60, diasHasta: -3);
        await using var servidor = await ServidorWeb.IniciarAsync(app => { }, certificado.Valor);

        var resultado = await Comprobar(Contra(servidor));

        resultado.Correcto.ShouldBeFalse();
        resultado.Fallo.ShouldBe(TipoFallo.Tls);
        resultado.Error!.ShouldContain("caducó el");
        resultado.Detalles["dias_restantes"].ShouldBe("-3");
    }

    [Fact]
    public async Task Un_certificado_que_todavia_no_es_valido_es_un_fallo()
    {
        using var certificado = Certificado(diasDesde: 5, diasHasta: 60);
        await using var servidor = await ServidorWeb.IniciarAsync(app => { }, certificado.Valor);

        var resultado = await Comprobar(Contra(servidor));

        resultado.Fallo.ShouldBe(TipoFallo.Tls);
        resultado.Error!.ShouldContain("no es válido hasta");
    }

    [Fact]
    public async Task Un_certificado_autofirmado_falla_si_se_verifica_la_cadena()
    {
        using var certificado = Certificado();
        await using var servidor = await ServidorWeb.IniciarAsync(app => { }, certificado.Valor);

        var resultado = await Comprobar(Contra(servidor) with { VerificarCertificado = true });

        resultado.Fallo.ShouldBe(TipoFallo.Tls);
        resultado.Error!.ShouldContain("de confianza");
    }

    [Fact]
    public async Task Un_certificado_para_otro_nombre_se_detecta()
    {
        using var certificado = Certificado(nombre: "otro-sitio.example");
        await using var servidor = await ServidorWeb.IniciarAsync(app => { }, certificado.Valor);

        var resultado = await Comprobar(Contra(servidor) with { VerificarCertificado = true });

        resultado.Fallo.ShouldBe(TipoFallo.Tls);
        resultado.Error!.ShouldContain("no es válido para");
    }

    [Fact]
    public async Task Un_servidor_sin_tls_es_un_fallo_de_tls_y_no_un_error_del_programa()
    {
        await using var servidor = await ServidorWeb.IniciarAsync(app => { });

        var resultado = await Comprobar(Contra(servidor));

        resultado.Correcto.ShouldBeFalse();
        resultado.Fallo.ShouldBe(TipoFallo.Tls);
    }

    [Fact]
    public async Task Un_servidor_que_no_contesta_al_saludo_agota_el_tiempo_sin_esperar_de_verdad()
    {
        using var mudo = new ServidorTcpMudo();

        var tarea = Comprobar(new ConfiguracionTls("127.0.0.1") { Puerto = mudo.Puerto, PermitirRedPrivada = true, TiempoMaximo = TimeSpan.FromSeconds(8) });
        await Task.Delay(300, Cancelacion); // deja que la conexión y el saludo empiecen
        _entorno.Reloj.Advance(TimeSpan.FromSeconds(8));
        var resultado = await tarea.WaitAsync(TimeSpan.FromSeconds(10), Cancelacion);

        resultado.Fallo.ShouldBe(TipoFallo.TiempoAgotado);
    }

    [Fact]
    public async Task Un_puerto_cerrado_es_conexion_rechazada()
    {
        var resultado = await Comprobar(new ConfiguracionTls("127.0.0.1") { Puerto = PuertoCerrado.Nuevo(), PermitirRedPrivada = true });

        resultado.Fallo.ShouldBe(TipoFallo.ConexionRechazada);
    }

    [Fact]
    public async Task Los_destinos_internos_se_bloquean_tambien_en_tls()
    {
        var resultado = await Comprobar(new ConfiguracionTls("169.254.169.254"));

        resultado.Fallo.ShouldBe(TipoFallo.DestinoBloqueado);
    }

    /// <summary>Envuelve un certificado para liberarlo con <c>using</c> sin repetir el tipo largo.</summary>
    private sealed class X509Certificado(System.Security.Cryptography.X509Certificates.X509Certificate2 valor) : IDisposable
    {
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Valor { get; } = valor;

        public void Dispose() => Valor.Dispose();
    }
}

public sealed class ComprobadorDnsTests : IDisposable
{
    private readonly Entorno _entorno = new();

    private static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _entorno.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<ResultadoComprobacion> Comprobar(ConfiguracionDns configuracion) =>
        _entorno.Obtener<ComprobadorDns>().ComprobarAsync(configuracion, Cancelacion);

    private static ConfiguracionDns Pregunta(ServidorDns servidor, string nombre, TipoRegistroDns tipo) =>
        new(nombre, tipo) { Servidor = servidor.Direccion, PermitirRedPrivada = true };

    [Fact]
    public async Task Un_registro_a_se_resuelve_y_se_devuelve()
    {
        using var servidor = new ServidorDns((nombre, tipo) => RespuestaDns.Con(RegistroDns.DeA("93.184.216.34")));

        var resultado = await Comprobar(Pregunta(servidor, "www.ejemplo.test", TipoRegistroDns.A));

        resultado.Correcto.ShouldBeTrue();
        resultado.Detalles["registros"].ShouldBe("93.184.216.34");
        resultado.Detalles["servidor"].ShouldBe("127.0.0.1");
    }

    [Fact]
    public async Task Varios_registros_salen_ordenados_para_poder_comparar_dos_resultados_como_dos_textos()
    {
        using var servidor = new ServidorDns((_, _) => RespuestaDns.Con(RegistroDns.DeA("93.184.216.35"), RegistroDns.DeA("93.184.216.34"), RegistroDns.DeA("8.8.8.8")));

        var resultado = await Comprobar(Pregunta(servidor, "www.ejemplo.test", TipoRegistroDns.A));

        resultado.Detalles["registros"].ShouldBe("8.8.8.8; 93.184.216.34; 93.184.216.35");
    }

    [Fact]
    public async Task Cada_tipo_de_registro_se_lee_como_corresponde()
    {
        using var servidor = new ServidorDns((_, tipo) => tipo switch
        {
            ServidorDns.Aaaa => RespuestaDns.Con(RegistroDns.DeAaaa("2606:4700:4700::1111")),
            ServidorDns.Cname => RespuestaDns.Con(RegistroDns.DeCname("destino.ejemplo.test.")),
            ServidorDns.Mx => RespuestaDns.Con(RegistroDns.DeMx(20, "Correo2.Ejemplo.test."), RegistroDns.DeMx(10, "correo1.ejemplo.test")),
            ServidorDns.Txt => RespuestaDns.Con(RegistroDns.DeTxt("v=spf1 -all")),
            _ => RespuestaDns.SinRegistros,
        });

        (await Comprobar(Pregunta(servidor, "ejemplo.test", TipoRegistroDns.Aaaa))).Detalles["registros"].ShouldBe("2606:4700:4700::1111");
        (await Comprobar(Pregunta(servidor, "www.ejemplo.test", TipoRegistroDns.Cname))).Detalles["registros"].ShouldBe("destino.ejemplo.test");
        (await Comprobar(Pregunta(servidor, "ejemplo.test", TipoRegistroDns.Mx))).Detalles["registros"].ShouldBe("10 correo1.ejemplo.test; 20 correo2.ejemplo.test");
        (await Comprobar(Pregunta(servidor, "ejemplo.test", TipoRegistroDns.Txt))).Detalles["registros"].ShouldBe("v=spf1 -all");
    }

    [Fact]
    public async Task Los_registros_esperados_se_comparan_sin_importar_el_orden_ni_las_mayusculas()
    {
        using var servidor = new ServidorDns((_, _) => RespuestaDns.Con(RegistroDns.DeMx(20, "correo2.ejemplo.test."), RegistroDns.DeMx(10, "correo1.ejemplo.test.")));

        var resultado = await Comprobar(Pregunta(servidor, "ejemplo.test", TipoRegistroDns.Mx) with { Esperados = ["20 CORREO2.ejemplo.test", "10 correo1.ejemplo.test."] });

        resultado.Correcto.ShouldBeTrue();
    }

    [Fact]
    public async Task Si_la_respuesta_no_es_la_esperada_es_un_fallo_que_dice_cual_era_cada_una()
    {
        // El caso que quiere detectar el monitor: alguien cambió el registro A de la web (o se lo cambiaron).
        using var servidor = new ServidorDns((_, _) => RespuestaDns.Con(RegistroDns.DeA("203.0.113.9")));

        var resultado = await Comprobar(Pregunta(servidor, "www.ejemplo.test", TipoRegistroDns.A) with { Esperados = ["93.184.216.34"] });

        resultado.Correcto.ShouldBeFalse();
        resultado.Fallo.ShouldBe(TipoFallo.RegistroInesperado);
        resultado.Error!.ShouldContain("93.184.216.34");
        resultado.Error!.ShouldContain("203.0.113.9");
        resultado.Detalles["registros"].ShouldBe("203.0.113.9");
    }

    [Fact]
    public async Task Un_nombre_que_no_existe_es_nxdomain()
    {
        using var servidor = new ServidorDns((_, _) => RespuestaDns.NoExiste);

        var resultado = await Comprobar(Pregunta(servidor, "no-existe.ejemplo.test", TipoRegistroDns.A));

        resultado.Correcto.ShouldBeFalse();
        resultado.Fallo.ShouldBe(TipoFallo.ResolucionDns);
        resultado.Error!.ShouldContain("NXDOMAIN");
    }

    [Fact]
    public async Task Un_fallo_del_servidor_dns_es_un_fallo_de_resolucion()
    {
        using var servidor = new ServidorDns((_, _) => RespuestaDns.FalloDelServidor);

        var resultado = await Comprobar(Pregunta(servidor, "ejemplo.test", TipoRegistroDns.A));

        resultado.Fallo.ShouldBe(TipoFallo.ResolucionDns);
    }

    [Fact]
    public async Task Un_nombre_sin_registros_del_tipo_pedido_es_un_fallo()
    {
        using var servidor = new ServidorDns((_, _) => RespuestaDns.SinRegistros);

        var resultado = await Comprobar(Pregunta(servidor, "ejemplo.test", TipoRegistroDns.Mx));

        resultado.Fallo.ShouldBe(TipoFallo.ResolucionDns);
        resultado.Error!.ShouldContain("no tiene registros MX");
    }

    [Fact]
    public async Task Un_servidor_dns_que_no_contesta_agota_el_tiempo()
    {
        using var servidor = new ServidorDns((_, _) => null);

        var tarea = Comprobar(Pregunta(servidor, "ejemplo.test", TipoRegistroDns.A) with { TiempoMaximo = TimeSpan.FromSeconds(6) });
        while (servidor.Consultas == 0)
        {
            await Task.Delay(20, Cancelacion);
        }

        _entorno.Reloj.Advance(TimeSpan.FromSeconds(6));
        var resultado = await tarea.WaitAsync(TimeSpan.FromSeconds(10), Cancelacion);

        resultado.Fallo.ShouldBe(TipoFallo.TiempoAgotado);
    }

    [Fact]
    public async Task Un_servidor_dns_interno_se_bloquea_sin_permiso()
    {
        using var servidor = new ServidorDns((_, _) => RespuestaDns.Con(RegistroDns.DeA("1.2.3.4")));

        var resultado = await Comprobar(new ConfiguracionDns("ejemplo.test", TipoRegistroDns.A) { Servidor = servidor.Direccion });

        resultado.Fallo.ShouldBe(TipoFallo.DestinoBloqueado);
        servidor.Consultas.ShouldBe(0, "ni siquiera se le preguntó");
    }

    [Fact]
    public async Task Un_servidor_dns_que_no_es_una_ip_se_rechaza()
    {
        var resultado = await Comprobar(new ConfiguracionDns("ejemplo.test", TipoRegistroDns.A) { Servidor = "dns.interno.example" });

        resultado.Correcto.ShouldBeFalse();
        resultado.Error!.ShouldContain("dirección IP");
    }
}

public sealed class ComprobadorTcpTests : IDisposable
{
    private readonly Entorno _entorno = new();

    private static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _entorno.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Un_puerto_abierto_es_correcto_y_dice_a_que_ip_conecto()
    {
        using var servidor = new ServidorTcpMudo();

        var resultado = await _entorno.Obtener<ComprobadorTcp>().ComprobarAsync(new ConfiguracionTcp("127.0.0.1", servidor.Puerto) { PermitirRedPrivada = true }, Cancelacion);

        resultado.Correcto.ShouldBeTrue();
        resultado.Detalles["ip"].ShouldBe("127.0.0.1");
        resultado.Detalles["puerto"].ShouldBe(servidor.Puerto.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Un_puerto_cerrado_es_conexion_rechazada()
    {
        var resultado = await _entorno.Obtener<ComprobadorTcp>().ComprobarAsync(new ConfiguracionTcp("127.0.0.1", PuertoCerrado.Nuevo()) { PermitirRedPrivada = true }, Cancelacion);

        resultado.Correcto.ShouldBeFalse();
        resultado.Fallo.ShouldBe(TipoFallo.ConexionRechazada);
    }

    [Fact]
    public async Task Un_destino_que_no_contesta_agota_el_tiempo_sin_esperar_de_verdad()
    {
        var conector = new ConectorColgado();
        using var entorno = new Entorno(s => s.AddSingleton<IConector>(conector));
        entorno.Resolvedor.Asignar("lento.test", "93.184.216.34");

        var tarea = entorno.Obtener<ComprobadorTcp>().ComprobarAsync(new ConfiguracionTcp("lento.test", 22) { TiempoMaximo = TimeSpan.FromSeconds(3) }, Cancelacion);
        await conector.Intentando.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancelacion);
        entorno.Reloj.Advance(TimeSpan.FromSeconds(3));
        var resultado = await tarea.WaitAsync(TimeSpan.FromSeconds(10), Cancelacion);

        resultado.Fallo.ShouldBe(TipoFallo.TiempoAgotado);
        resultado.Latencia.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Si_un_nombre_tiene_varias_direcciones_basta_con_que_responda_una()
    {
        using var servidor = new ServidorTcpMudo();
        var conector = new ConectorQueFallaPara(["93.184.216.34"], servidor.Puerto);
        using var entorno = new Entorno(s => s.AddSingleton<IConector>(conector));
        entorno.Resolvedor.Asignar("doble.test", "93.184.216.34", "93.184.216.35");

        var resultado = await entorno.Obtener<ComprobadorTcp>().ComprobarAsync(new ConfiguracionTcp("doble.test", 443), Cancelacion);

        resultado.Correcto.ShouldBeTrue();
        resultado.Detalles["ip"].ShouldBe("93.184.216.35");
        conector.Intentos.Select(i => i.ToString()).ShouldBe(["93.184.216.34", "93.184.216.35"]);
    }

    [Fact]
    public async Task Si_todas_las_direcciones_fallan_es_un_fallo()
    {
        var conector = new ConectorQueFallaPara(["93.184.216.34", "93.184.216.35"], 1);
        using var entorno = new Entorno(s => s.AddSingleton<IConector>(conector));
        entorno.Resolvedor.Asignar("caido.test", "93.184.216.34", "93.184.216.35");

        var resultado = await entorno.Obtener<ComprobadorTcp>().ComprobarAsync(new ConfiguracionTcp("caido.test", 443), Cancelacion);

        resultado.Fallo.ShouldBe(TipoFallo.ConexionRechazada);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    public async Task Los_destinos_internos_se_bloquean_sin_permiso_y_no_se_conecta(string ip)
    {
        var conector = new ConectorColgado();
        using var entorno = new Entorno(s => s.AddSingleton<IConector>(conector));

        var resultado = await entorno.Obtener<ComprobadorTcp>().ComprobarAsync(new ConfiguracionTcp(ip, 22), Cancelacion).WaitAsync(TimeSpan.FromSeconds(10), Cancelacion);

        resultado.Fallo.ShouldBe(TipoFallo.DestinoBloqueado);
        conector.Intentando.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Un_nombre_que_no_existe_es_un_fallo_de_resolucion()
    {
        var resultado = await _entorno.Obtener<ComprobadorTcp>().ComprobarAsync(new ConfiguracionTcp("no-existe.test", 22), Cancelacion);

        resultado.Fallo.ShouldBe(TipoFallo.ResolucionDns);
    }
}

public sealed class ComprobadorIcmpTests
{
    private static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    private static Entorno ConPing(EnviadorPingFalso ping) => new(s => s.AddSingleton<IEnviadorPing>(ping));

    [Fact]
    public async Task Un_ping_con_respuesta_es_correcto_y_su_latencia_es_el_tiempo_de_ida_y_vuelta()
    {
        var ping = new EnviadorPingFalso(_ => Task.FromResult(new RespuestaPing(true, TimeSpan.FromMilliseconds(23), "Success")));
        using var entorno = ConPing(ping);
        entorno.Resolvedor.Asignar("servidor.test", "93.184.216.34");

        var resultado = await entorno.Obtener<ComprobadorIcmp>().ComprobarAsync(new ConfiguracionIcmp("servidor.test"), Cancelacion);

        resultado.Correcto.ShouldBeTrue();
        resultado.Latencia.ShouldBe(TimeSpan.FromMilliseconds(23));
        resultado.Detalles["ip"].ShouldBe("93.184.216.34");
        ping.Destinos.ShouldBe([IPAddress.Parse("93.184.216.34")]);
    }

    [Fact]
    public async Task Un_ping_sin_respuesta_es_un_fallo_con_el_estado()
    {
        var ping = new EnviadorPingFalso(_ => Task.FromResult(new RespuestaPing(false, TimeSpan.Zero, "TimedOut")));
        using var entorno = ConPing(ping);
        entorno.Resolvedor.Asignar("servidor.test", "93.184.216.34");

        var resultado = await entorno.Obtener<ComprobadorIcmp>().ComprobarAsync(new ConfiguracionIcmp("servidor.test"), Cancelacion);

        resultado.Correcto.ShouldBeFalse();
        resultado.Error!.ShouldContain("TimedOut");
    }

    [Fact]
    public async Task Si_el_entorno_no_permite_enviar_pings_lo_dice_en_lugar_de_dar_el_equipo_por_caido_sin_mas()
    {
        var ping = new EnviadorPingFalso(_ => throw new PingException("Error al enviar", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AccessDenied)));
        using var entorno = ConPing(ping);
        entorno.Resolvedor.Asignar("servidor.test", "93.184.216.34");

        var resultado = await entorno.Obtener<ComprobadorIcmp>().ComprobarAsync(new ConfiguracionIcmp("servidor.test"), Cancelacion);

        resultado.Correcto.ShouldBeFalse();
        resultado.Error!.ShouldContain("CAP_NET_RAW");
    }

    [Fact]
    public async Task Los_destinos_internos_se_bloquean_sin_permiso_y_no_se_envia_ningun_ping()
    {
        var ping = new EnviadorPingFalso(_ => Task.FromResult(new RespuestaPing(true, TimeSpan.Zero, "Success")));
        using var entorno = ConPing(ping);

        var resultado = await entorno.Obtener<ComprobadorIcmp>().ComprobarAsync(new ConfiguracionIcmp("192.168.1.1"), Cancelacion);

        resultado.Fallo.ShouldBe(TipoFallo.DestinoBloqueado);
        ping.Destinos.ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_ping_real_al_propio_equipo_funciona_si_el_entorno_lo_permite()
    {
        using var entorno = new Entorno();

        var resultado = await entorno.Obtener<ComprobadorIcmp>().ComprobarAsync(new ConfiguracionIcmp("127.0.0.1") { PermitirRedPrivada = true }, Cancelacion);

        // En algunos entornos (contenedores sin permiso de red cruda) no se puede enviar un ping: no es un fallo del código.
        Assert.SkipWhen(resultado.Error?.Contains("CAP_NET_RAW", StringComparison.Ordinal) == true, "Este entorno no permite enviar pings.");

        resultado.Correcto.ShouldBeTrue();
    }
}

public class EjecutorComprobacionesTests
{
    [Fact]
    public async Task Cada_configuracion_va_a_su_comprobador()
    {
        using var servidor = new ServidorTcpMudo();
        using var entorno = new Entorno();
        var ejecutor = entorno.Obtener<EjecutorComprobaciones>();

        var resultado = await ejecutor.EjecutarAsync(new ConfiguracionTcp("127.0.0.1", servidor.Puerto) { PermitirRedPrivada = true }, TestContext.Current.CancellationToken);

        resultado.Correcto.ShouldBeTrue();
    }

    [Fact]
    public void Estan_registrados_los_cinco_tipos()
    {
        using var entorno = new Entorno();

        entorno.Obtener<IEnumerable<IComprobador>>().Select(c => c.Tipo).ShouldBe(Enum.GetValues<TipoMonitor>(), ignoreOrder: true);
    }

    [Fact]
    public async Task Un_comprobador_rechaza_una_configuracion_de_otro_tipo()
    {
        using var entorno = new Entorno();

        await Should.ThrowAsync<ArgumentException>(() =>
            entorno.Obtener<ComprobadorTcp>().ComprobarAsync(new ConfiguracionIcmp("127.0.0.1"), TestContext.Current.CancellationToken));
    }
}
