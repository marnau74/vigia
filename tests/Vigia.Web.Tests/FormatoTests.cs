using System.Text.Json;

using Shouldly;

using Vigia.Contratos;
using Vigia.Dominio.Monitores;
using Vigia.Web.Components.Pages;
using Vigia.Web.Servicios;

namespace Vigia.Web.Tests;

public class FormatoTests
{
    [Theory]
    [InlineData(99.95, "99,95 %")]
    [InlineData(100, "100 %")]
    [InlineData(0, "0 %")]
    [InlineData(92.5, "92,5 %")]
    [InlineData(99.9, "99,9 %")]
    public void Los_porcentajes_llevan_coma_decimal_y_sin_ceros_sobrantes(double valor, string esperado)
    {
        Formato.Porcentaje((decimal)valor).ShouldBe(esperado);
    }

    [Fact]
    public void Sin_datos_se_escribe_una_raya_y_no_un_cien_por_cien()
    {
        Formato.Porcentaje(null).ShouldBe("—");
        Formato.Latencia((int?)null).ShouldBe("—");
        Formato.Latencia((double?)null).ShouldBe("—");
        Formato.Fecha(null).ShouldBe("—");
    }

    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(87, "87 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1000, "1 s")]
    [InlineData(1500, "1,5 s")]
    [InlineData(12340, "12,34 s")]
    public void Las_latencias_pasan_a_segundos_desde_mil_milisegundos(int milisegundos, string esperado)
    {
        Formato.Latencia(milisegundos).ShouldBe(esperado);
    }

    [Fact]
    public void Las_latencias_decimales_se_redondean()
    {
        Formato.Latencia((double?)87.6).ShouldBe("88 ms");
    }

    [Fact]
    public void Las_fechas_van_siempre_en_utc_y_lo_dicen_aunque_vengan_con_otra_zona()
    {
        Formato.Fecha(new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.FromHours(2))).ShouldBe("15 oct 2026, 10:30 UTC");
        Formato.Fecha(new DateTimeOffset(2027, 1, 3, 0, 5, 0, TimeSpan.Zero)).ShouldBe("3 ene 2027, 00:05 UTC");
        Formato.Dia(new DateOnly(2026, 12, 25)).ShouldBe("25 dic 2026");
        Formato.Hora(new DateTimeOffset(2026, 10, 15, 9, 5, 0, TimeSpan.Zero)).ShouldBe("09:05");
    }

    [Theory]
    [InlineData(2, "ahora mismo")]
    [InlineData(30, "hace 30 s")]
    [InlineData(180, "hace 3 min")]
    [InlineData(7300, "hace 2 h")]
    [InlineData(200000, "hace 2 d")]
    public void El_tiempo_transcurrido_se_dice_en_la_unidad_mayor(int segundos, string esperado)
    {
        var ahora = new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

        Formato.Hace(ahora.AddSeconds(-segundos), ahora).ShouldBe(esperado);
        Formato.Hace(null, ahora).ShouldBe("nunca");
    }

    [Fact]
    public void Las_duraciones_usan_las_dos_unidades_mayores()
    {
        Formato.Duracion(192).ShouldBe("3 min 12 s");
        Formato.Duracion(7500).ShouldBe("2 h 5 min");
    }

    [Theory]
    [InlineData(EstadoMonitor.Operativo, "Operativo", "ok")]
    [InlineData(EstadoMonitor.Degradado, "Lento", "aviso")]
    [InlineData(EstadoMonitor.Sospechoso, "Comprobando", "aviso")]
    [InlineData(EstadoMonitor.Caido, "Caído", "caido")]
    [InlineData(EstadoMonitor.Mantenimiento, "Mantenimiento", "mantenimiento")]
    [InlineData(EstadoMonitor.Desconocido, "Sin datos", "desconocido")]
    public void Cada_estado_tiene_su_palabra_y_su_clase(EstadoMonitor estado, string texto, string clase)
    {
        Textos.Estado(estado).ShouldBe(texto);
        Textos.Clase(estado).ShouldBe(clase);
    }

    [Fact]
    public void Todos_los_estados_estan_cubiertos_por_un_texto_propio()
    {
        foreach (var estado in Enum.GetValues<EstadoMonitor>())
        {
            Textos.Estado(estado).ShouldNotBeNullOrWhiteSpace();
        }

        Enum.GetValues<EstadoGeneral>().Select(Textos.General).Distinct().Count().ShouldBe(Enum.GetValues<EstadoGeneral>().Length);
    }

    [Theory]
    [InlineData(EstadoGeneral.Operativo, EstadoMonitor.Operativo)]
    [InlineData(EstadoGeneral.Degradado, EstadoMonitor.Degradado)]
    [InlineData(EstadoGeneral.IncidenteParcial, EstadoMonitor.Caido)]
    [InlineData(EstadoGeneral.Caido, EstadoMonitor.Caido)]
    [InlineData(EstadoGeneral.Mantenimiento, EstadoMonitor.Mantenimiento)]
    [InlineData(EstadoGeneral.SinDatos, EstadoMonitor.Desconocido)]
    public void El_estado_general_se_dibuja_con_la_forma_de_su_representante(EstadoGeneral general, EstadoMonitor esperado)
    {
        Textos.Representante(general).ShouldBe(esperado);
    }

    private static MonitorDto Monitor(string tipo, string configuracion) =>
        new(Guid.NewGuid(), "M", tipo, JsonDocument.Parse(configuracion).RootElement, 60, 3, null, null, true, EstadoMonitor.Operativo, null, null, null, null, null);

    [Fact]
    public void El_destino_se_lee_de_la_configuracion_de_cada_tipo()
    {
        Textos.Destino(Monitor("Http", "{\"tipo\":\"Http\",\"url\":\"https://ejemplo.com/\"}")).ShouldBe("https://ejemplo.com/");
        Textos.Destino(Monitor("Tcp", "{\"tipo\":\"Tcp\",\"host\":\"db.ejemplo.com\",\"puerto\":5432}")).ShouldBe("db.ejemplo.com:5432");
        Textos.Destino(Monitor("Dns", "{\"tipo\":\"Dns\",\"nombre\":\"ejemplo.com\",\"registro\":\"mx\"}")).ShouldBe("ejemplo.com (mx)");
        Textos.Destino(Monitor("Icmp", "{\"tipo\":\"Icmp\",\"host\":\"10.0.0.1\"}")).ShouldBe("10.0.0.1");
        Textos.Destino(Monitor("Tls", "{}")).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/monitores/123", "/monitores/123")]
    [InlineData("/grupos?x=1", "/grupos?x=1")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("//otra.web", "/")]
    [InlineData("/\\otra.web", "/")]
    [InlineData("https://otra.web", "/")]
    [InlineData("otra.web", "/")]
    [InlineData("javascript:alert(1)", "/")]
    public void Tras_entrar_solo_se_vuelve_a_rutas_de_esta_misma_web(string? volver, string esperado)
    {
        Entrar.DestinoSeguro(volver).ShouldBe(esperado);
    }
}

public class ModeloDeMonitorTests
{
    private static JsonElement Configuracion(ModeloDeMonitor modelo) => modelo.Configuracion();

    [Fact]
    public void Un_monitor_http_genera_la_configuracion_que_espera_la_api()
    {
        var modelo = new ModeloDeMonitor { Tipo = "Http", Url = "  https://ejemplo.com/salud ", PalabraClave = " ok ", CodigosEsperados = "200-299", TiempoMaximoSegundos = 8 };

        var c = Configuracion(modelo);

        c.GetProperty("tipo").GetString().ShouldBe("Http");
        c.GetProperty("url").GetString().ShouldBe("https://ejemplo.com/salud");
        c.GetProperty("palabraClave").GetString().ShouldBe("ok");
        c.GetProperty("codigosEsperados").GetString().ShouldBe("200-299");
        c.GetProperty("tiempoMaximo").GetString().ShouldBe("00:00:08");
        c.TryGetProperty("permitirRedPrivada", out _).ShouldBeFalse("por defecto no se pide nada que debilite la seguridad");
    }

    [Fact]
    public void Una_palabra_clave_vacia_no_se_envia()
    {
        Configuracion(new ModeloDeMonitor { Tipo = "Http", PalabraClave = "   " }).TryGetProperty("palabraClave", out _).ShouldBeFalse();
    }

    [Fact]
    public void El_permiso_de_red_privada_solo_se_envia_si_se_marca()
    {
        Configuracion(new ModeloDeMonitor { PermitirRedPrivada = true }).GetProperty("permitirRedPrivada").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Cada_tipo_genera_solo_sus_campos()
    {
        var tcp = Configuracion(new ModeloDeMonitor { Tipo = "Tcp", Host = " db.ejemplo.com ", Puerto = 5432, Url = "https://no-deberia-salir/" });
        tcp.GetProperty("host").GetString().ShouldBe("db.ejemplo.com");
        tcp.GetProperty("puerto").GetInt32().ShouldBe(5432);
        tcp.TryGetProperty("url", out _).ShouldBeFalse();

        var dns = Configuracion(new ModeloDeMonitor { Tipo = "Dns", NombreDns = "ejemplo.com", RegistroDns = "Mx", EsperadosDns = "mx1.ejemplo.com, mx2.ejemplo.com;\nmx3.ejemplo.com", ServidorDns = "1.1.1.1" });
        dns.GetProperty("registro").GetString().ShouldBe("Mx");
        dns.GetProperty("esperados").EnumerateArray().Select(e => e.GetString()).ShouldBe(["mx1.ejemplo.com", "mx2.ejemplo.com", "mx3.ejemplo.com"]);
        dns.GetProperty("servidor").GetString().ShouldBe("1.1.1.1");

        Configuracion(new ModeloDeMonitor { Tipo = "Tls", Host = "ejemplo.com", Puerto = 8443 }).GetProperty("puerto").GetInt32().ShouldBe(8443);
        Configuracion(new ModeloDeMonitor { Tipo = "Icmp", Host = "ejemplo.com" }).TryGetProperty("puerto", out _).ShouldBeFalse();
    }

    [Fact]
    public void El_cuerpo_lleva_los_datos_comunes_recortados()
    {
        var modelo = new ModeloDeMonitor { Nombre = "  Mi web  ", IntervaloSegundos = 120, FallosParaIncidente = 2, UmbralLentoMs = 1500, GrupoId = Guid.NewGuid() };

        var cuerpo = modelo.Cuerpo();

        cuerpo.Nombre.ShouldBe("Mi web");
        cuerpo.IntervaloSegundos.ShouldBe(120);
        cuerpo.FallosParaIncidente.ShouldBe(2);
        cuerpo.UmbralLentoMs.ShouldBe(1500);
        cuerpo.GrupoId.ShouldBe(modelo.GrupoId);
    }

    [Theory]
    [InlineData("Http")]
    [InlineData("Tls")]
    [InlineData("Dns")]
    [InlineData("Tcp")]
    [InlineData("Icmp")]
    public void Editar_un_monitor_y_volver_a_guardarlo_no_cambia_su_configuracion(string tipo)
    {
        var original = new ModeloDeMonitor
        {
            Nombre = "X",
            Tipo = tipo,
            Url = "https://ejemplo.com/",
            PalabraClave = "hola",
            CodigosEsperados = "200",
            Host = "ejemplo.com",
            Puerto = 8080,
            NombreDns = "ejemplo.com",
            RegistroDns = "Txt",
            EsperadosDns = "a, b",
            ServidorDns = "9.9.9.9",
            TiempoMaximoSegundos = 7,
            PermitirRedPrivada = true,
            VerificarCertificado = false,
        };
        var dto = new MonitorDto(Guid.NewGuid(), "X", tipo, Configuracion(original), 90, 4, 800, null, true, EstadoMonitor.Operativo, null, null, null, null, null);

        var devuelto = ModeloDeMonitor.De(dto);

        Configuracion(devuelto).GetRawText().ShouldBe(Configuracion(original).GetRawText());
        devuelto.IntervaloSegundos.ShouldBe(90);
        devuelto.FallosParaIncidente.ShouldBe(4);
        devuelto.UmbralLentoMs.ShouldBe(800);
    }

    [Fact]
    public void El_registro_dns_que_devuelve_la_api_en_minusculas_se_normaliza_para_el_selector()
    {
        var dto = new MonitorDto(Guid.NewGuid(), "D", "Dns", JsonDocument.Parse("{\"tipo\":\"Dns\",\"nombre\":\"x.com\",\"registro\":\"aaaa\"}").RootElement, 60, 3, null, null, true, EstadoMonitor.Operativo, null, null, null, null, null);

        ModeloDeMonitor.De(dto).RegistroDns.ShouldBe("Aaaa");
        ModeloDeMonitor.RegistrosDns.ShouldContain("Aaaa");
    }
}
