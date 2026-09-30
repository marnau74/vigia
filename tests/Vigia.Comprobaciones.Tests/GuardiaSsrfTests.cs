using System.Net;

using Shouldly;

using Vigia.Comprobaciones.Red;
using Vigia.Comprobaciones.Tests.Apoyo;

namespace Vigia.Comprobaciones.Tests;

/// <summary>Cada rango que se bloquea, con su prueba: si alguien «simplifica» la lista, falla.</summary>
public class RangosBloqueadosTests
{
    public static TheoryData<string> Bloqueadas => new()
    {
        // Loopback, esta red y broadcast
        "127.0.0.1", "127.255.255.254", "0.0.0.0", "0.1.2.3", "255.255.255.255",
        // Redes privadas: los dos extremos de cada rango
        "10.0.0.1", "10.255.255.255", "172.16.0.1", "172.31.255.255", "192.168.0.1", "192.168.255.255",
        // Enlace local y metadatos de las nubes (AWS, GCP, Azure, DigitalOcean: 169.254.169.254)
        "169.254.0.1", "169.254.169.254", "169.254.255.255",
        // Otros metadatos: Alibaba (100.100.100.200), Oracle (192.0.0.192), Azure WireServer (168.63.129.16)
        "100.64.0.1", "100.100.100.200", "100.127.255.255", "192.0.0.192", "168.63.129.16",
        // Documentación, pruebas, multidifusión y reservadas
        "192.0.2.1", "198.51.100.1", "203.0.113.1", "198.18.0.1", "198.19.255.255", "192.88.99.1", "224.0.0.1", "239.255.255.250", "240.0.0.1",
        // IPv6
        "::1", "::", "fe80::1", "fe80::ffff:1", "fc00::1", "fd00:ec2::254", "fdff::1", "ff02::1", "2001:db8::1", "2001:0:4136:e378:8000:63bf:3fff:fdd2", "fec0::1",
        // IPv4 escrita dentro de IPv6 (el truco para saltarse una lista que solo mira IPv4)
        "::ffff:127.0.0.1", "::ffff:10.0.0.1", "::ffff:169.254.169.254", "64:ff9b::7f00:1", "64:ff9b::a9fe:a9fe", "2002:7f00:1::1", "2002:a9fe:a9fe::1",
    };

    public static TheoryData<string> Permitidas => new()
    {
        "8.8.8.8", "1.1.1.1", "93.184.216.34", "142.250.184.14",
        // Justo fuera de los rangos privados
        "172.15.255.255", "172.32.0.1", "11.0.0.1", "9.255.255.255", "192.167.255.255", "192.169.0.1", "100.63.255.255", "100.128.0.1", "169.253.255.255", "169.255.0.1", "198.17.255.255", "198.20.0.1",
        // IPv6 públicas
        "2606:4700:4700::1111", "2001:4860:4860::8888",
        // IPv4 pública dentro de IPv6
        "::ffff:8.8.8.8", "64:ff9b::808:808", "2002:808:808::1",
    };

    [Theory]
    [MemberData(nameof(Bloqueadas))]
    public void Las_direcciones_internas_estan_bloqueadas(string texto)
    {
        var direccion = IPAddress.Parse(texto);

        RangosBloqueados.EstaBloqueada(direccion).ShouldBeTrue($"{texto} debería estar bloqueada");
        RangosBloqueados.Motivo(direccion).ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(Permitidas))]
    public void Las_direcciones_publicas_estan_permitidas(string texto)
    {
        RangosBloqueados.EstaBloqueada(IPAddress.Parse(texto)).ShouldBeFalse($"{texto} debería estar permitida");
    }

    [Fact]
    public void El_motivo_de_una_ipv4_escrita_dentro_de_ipv6_lo_dice()
    {
        RangosBloqueados.Motivo(IPAddress.Parse("::ffff:169.254.169.254"))!.ShouldContain("escrita dentro de una dirección IPv6");
    }
}

public class GuardiaDeDestinosTests
{
    private readonly ResolvedorFalso _resolvedor = new();
    private readonly GuardiaDeDestinos _guardia;

    public GuardiaDeDestinosTests() => _guardia = new GuardiaDeDestinos(_resolvedor);

    private Task<IReadOnlyList<IPAddress>> Resolver(string host, bool permitirRedPrivada = false) =>
        _guardia.ResolverPermitidasAsync(host, permitirRedPrivada, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Un_nombre_que_resuelve_a_una_direccion_publica_se_permite()
    {
        _resolvedor.Asignar("publico.test", "93.184.216.34");

        (await Resolver("publico.test")).ShouldBe([IPAddress.Parse("93.184.216.34")]);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fd00:ec2::254")]
    public async Task Un_nombre_que_resuelve_a_una_direccion_interna_se_bloquea_y_el_error_lo_explica(string direccion)
    {
        _resolvedor.Asignar("trampa.test", direccion);

        var error = await Should.ThrowAsync<DestinoBloqueadoException>(() => Resolver("trampa.test"));

        error.Message.ShouldContain("trampa.test");
        error.Message.ShouldContain(direccion);
    }

    [Fact]
    public async Task Si_una_sola_de_las_respuestas_es_interna_se_bloquea_todo_el_destino()
    {
        // Un nombre que mezcla una dirección pública y otra interna es la señal de un ataque: no se elige «la buena».
        _resolvedor.Asignar("mezcla.test", "93.184.216.34", "127.0.0.1");

        await Should.ThrowAsync<DestinoBloqueadoException>(() => Resolver("mezcla.test"));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("169.254.169.254")]
    [InlineData("2130706433")] // 127.0.0.1 escrita como un solo número
    [InlineData("0x7f000001")] // ídem, en hexadecimal
    public async Task Una_direccion_ip_escrita_directamente_tambien_pasa_por_la_lista(string host)
    {
        await Should.ThrowAsync<DestinoBloqueadoException>(() => Resolver(host));
        _resolvedor.Llamadas(host).ShouldBe(0, "una IP no necesita DNS");
    }

    [Fact]
    public async Task Con_el_permiso_explicito_se_puede_apuntar_a_la_red_privada()
    {
        _resolvedor.Asignar("nas.casa", "192.168.1.20");

        (await Resolver("nas.casa", permitirRedPrivada: true)).ShouldBe([IPAddress.Parse("192.168.1.20")]);
        (await Resolver("127.0.0.1", permitirRedPrivada: true)).ShouldBe([IPAddress.Loopback]);
    }

    [Fact]
    public async Task Un_nombre_que_no_existe_no_es_un_bloqueo_sino_un_fallo_de_resolucion()
    {
        await Should.ThrowAsync<NoSeResuelveException>(() => Resolver("no-existe.test"));
    }

    [Fact]
    public async Task Un_nombre_sin_ninguna_direccion_es_un_fallo_de_resolucion()
    {
        _resolvedor.AsignarSecuencia("vacio.test", _ => []);

        await Should.ThrowAsync<NoSeResuelveException>(() => Resolver("vacio.test"));
    }

    [Fact]
    public async Task Cada_llamada_resuelve_el_nombre_una_sola_vez()
    {
        // Es lo que impide el DNS rebinding: quien conecta usa las direcciones ya validadas, sin volver a preguntar.
        _resolvedor.Asignar("publico.test", "93.184.216.34");

        await Resolver("publico.test");

        _resolvedor.Llamadas("publico.test").ShouldBe(1);
    }
}
