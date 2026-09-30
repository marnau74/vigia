using Shouldly;

using Vigia.Dominio.Mantenimiento;
using Vigia.Dominio.Monitores;

using MonitorDeDominio = Vigia.Dominio.Monitores.Monitor;

namespace Vigia.Dominio.Tests;

public class MonitorTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static ConfiguracionHttp Web(string url = "https://ejemplo.com/") => new(new Uri(url));

    private static Vigia.Dominio.Comun.Resultado<MonitorDeDominio> Crear(
        string nombre = "Mi web",
        ConfiguracionMonitor? configuracion = null,
        int segundos = 60,
        int fallos = 3,
        TimeSpan? umbral = null) =>
        MonitorDeDominio.Crear(nombre, configuracion ?? Web(), TimeSpan.FromSeconds(segundos), fallos, umbral, null, Ahora);

    [Fact]
    public void Un_monitor_valido_se_crea_activo_con_sus_datos()
    {
        var monitor = Crear("  Mi web  ", umbral: TimeSpan.FromSeconds(2)).Valor;

        monitor.Nombre.ShouldBe("Mi web");
        monitor.Tipo.ShouldBe(TipoMonitor.Http);
        monitor.Intervalo.ShouldBe(TimeSpan.FromSeconds(60));
        monitor.FallosParaIncidente.ShouldBe(3);
        monitor.UmbralLento.ShouldBe(TimeSpan.FromSeconds(2));
        monitor.Activo.ShouldBeTrue();
        monitor.CreadoEn.ShouldBe(Ahora);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void El_nombre_es_obligatorio(string nombre)
    {
        Crear(nombre).Error.Codigo.ShouldBe("monitor.nombre_invalido");
    }

    [Fact]
    public void El_nombre_no_pasa_de_cien_caracteres()
    {
        Crear(new string('a', 100)).EsExito.ShouldBeTrue();
        Crear(new string('a', 101)).Error.Codigo.ShouldBe("monitor.nombre_invalido");
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(86400, true)]
    [InlineData(86401, false)]
    [InlineData(0, false)]
    public void El_intervalo_esta_entre_treinta_segundos_y_un_dia(int segundos, bool valido)
    {
        var resultado = Crear(segundos: segundos, configuracion: Web() with { TiempoMaximo = TimeSpan.FromSeconds(10) });

        resultado.EsExito.ShouldBe(valido);

        if (!valido)
        {
            resultado.Error.Codigo.ShouldBe("monitor.intervalo_invalido");
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void Los_fallos_para_abrir_un_incidente_estan_entre_uno_y_diez(int fallos, bool valido)
    {
        Crear(fallos: fallos).EsExito.ShouldBe(valido);
    }

    [Fact]
    public void El_tiempo_maximo_debe_ser_menor_que_el_intervalo_para_que_las_comprobaciones_no_se_solapen()
    {
        Crear(segundos: 30, configuracion: Web() with { TiempoMaximo = TimeSpan.FromSeconds(30) }).Error.Codigo.ShouldBe("monitor.tiempo_maximo_invalido");
        Crear(segundos: 30, configuracion: Web() with { TiempoMaximo = TimeSpan.FromSeconds(29) }).EsExito.ShouldBeTrue();
        Crear(segundos: 600, configuracion: Web() with { TiempoMaximo = TimeSpan.FromSeconds(61) }).Error.Codigo.ShouldBe("monitor.tiempo_maximo_invalido");
        Crear(configuracion: Web() with { TiempoMaximo = TimeSpan.Zero }).Error.Codigo.ShouldBe("monitor.tiempo_maximo_invalido");
    }

    [Fact]
    public void El_umbral_de_lentitud_debe_ser_positivo_y_menor_que_el_tiempo_maximo()
    {
        Crear(umbral: TimeSpan.FromSeconds(9)).EsExito.ShouldBeTrue();
        Crear(umbral: TimeSpan.FromSeconds(10)).Error.Codigo.ShouldBe("monitor.umbral_lento_invalido");
        Crear(umbral: TimeSpan.Zero).Error.Codigo.ShouldBe("monitor.umbral_lento_invalido");
    }

    // --- Configuración por tipo --------------------------------------------------------------

    [Theory]
    [InlineData("https://ejemplo.com/", true)]
    [InlineData("http://ejemplo.com:8080/salud?x=1", true)]
    [InlineData("http://192.168.1.10/", true)]
    [InlineData("ftp://ejemplo.com/", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://usuario:clave@ejemplo.com/", false)]
    [InlineData("https://usuario@ejemplo.com/", false)]
    public void La_url_debe_ser_http_o_https_sin_credenciales(string url, bool valida)
    {
        Crear(configuracion: Web(url)).EsExito.ShouldBe(valida);
    }

    [Fact]
    public void Una_url_relativa_no_vale()
    {
        Crear(configuracion: new ConfiguracionHttp(new Uri("/salud", UriKind.Relative))).Error.Codigo.ShouldBe("monitor.configuracion_invalida");
    }

    [Fact]
    public void Las_opciones_de_http_tienen_limites()
    {
        Crear(configuracion: Web() with { Metodo = "POST" }).EsFallo.ShouldBeTrue();
        Crear(configuracion: Web() with { Metodo = "HEAD" }).EsExito.ShouldBeTrue();
        Crear(configuracion: Web() with { MaxRedirecciones = 11 }).EsFallo.ShouldBeTrue();
        Crear(configuracion: Web() with { MaxRedirecciones = 0 }).EsExito.ShouldBeTrue();
        Crear(configuracion: Web() with { PalabraClave = "   " }).EsFallo.ShouldBeTrue();
        Crear(configuracion: Web() with { PalabraClave = new string('x', 201) }).EsFallo.ShouldBeTrue();
        Crear(configuracion: Web() with { PalabraClave = "Bienvenido" }).EsExito.ShouldBeTrue();
    }

    [Theory]
    [InlineData("ejemplo.com", true)]
    [InlineData("www.ejemplo.co.uk", true)]
    [InlineData("a-b.c-d.example", true)]
    [InlineData("ejemplo.com.", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("localhost", true)]
    [InlineData("", false)]
    [InlineData("http://ejemplo.com", false)]
    [InlineData("ejemplo.com/ruta", false)]
    [InlineData("ejemplo.com:443", false)]
    [InlineData("con espacio.com", false)]
    [InlineData("-empieza.com", false)]
    [InlineData("termina-.com", false)]
    [InlineData("doble..punto.com", false)]
    [InlineData("caracter_raro.com", false)]
    public void Un_equipo_es_un_nombre_de_dominio_o_una_direccion_ip_y_nada_mas(string host, bool valido)
    {
        ValidacionDeConfiguracion.EsHostValido(host).ShouldBe(valido);
    }

    [Fact]
    public void Una_etiqueta_de_dominio_de_mas_de_63_caracteres_no_vale()
    {
        ValidacionDeConfiguracion.EsHostValido(new string('a', 63) + ".com").ShouldBeTrue();
        ValidacionDeConfiguracion.EsHostValido(new string('a', 64) + ".com").ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(443, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    [InlineData(-1, false)]
    public void El_puerto_esta_entre_1_y_65535_en_tcp_y_tls(int puerto, bool valido)
    {
        Crear(configuracion: new ConfiguracionTcp("ejemplo.com", puerto)).EsExito.ShouldBe(valido);
        Crear(configuracion: new ConfiguracionTls("ejemplo.com") { Puerto = puerto }).EsExito.ShouldBe(valido);
    }

    [Fact]
    public void Icmp_solo_necesita_un_equipo_valido()
    {
        Crear(configuracion: new ConfiguracionIcmp("ejemplo.com")).EsExito.ShouldBeTrue();
        Crear(configuracion: new ConfiguracionIcmp("no valido!")).EsFallo.ShouldBeTrue();
    }

    [Fact]
    public void Dns_pide_un_nombre_de_dominio_y_valida_el_servidor_y_los_esperados()
    {
        Crear(configuracion: new ConfiguracionDns("ejemplo.com", TipoRegistroDns.A)).EsExito.ShouldBeTrue();
        Crear(configuracion: new ConfiguracionDns("93.184.216.34", TipoRegistroDns.A)).EsFallo.ShouldBeTrue();
        Crear(configuracion: new ConfiguracionDns("ejemplo.com", (TipoRegistroDns)99)).EsFallo.ShouldBeTrue();
        Crear(configuracion: new ConfiguracionDns("ejemplo.com", TipoRegistroDns.A) { Servidor = "8.8.8.8" }).EsExito.ShouldBeTrue();
        Crear(configuracion: new ConfiguracionDns("ejemplo.com", TipoRegistroDns.A) { Servidor = "127.0.0.1:5353" }).EsExito.ShouldBeTrue();
        Crear(configuracion: new ConfiguracionDns("ejemplo.com", TipoRegistroDns.A) { Servidor = "dns.example.com" }).EsFallo.ShouldBeTrue();
        Crear(configuracion: new ConfiguracionDns("ejemplo.com", TipoRegistroDns.A) { Esperados = ["93.184.216.34"] }).EsExito.ShouldBeTrue();
        Crear(configuracion: new ConfiguracionDns("ejemplo.com", TipoRegistroDns.A) { Esperados = [" "] }).EsFallo.ShouldBeTrue();
    }

    // --- Modificar ---------------------------------------------------------------------------

    [Fact]
    public void Se_puede_modificar_un_monitor_con_las_mismas_reglas_que_al_crearlo()
    {
        var monitor = Crear().Valor;
        var grupo = Guid.NewGuid();

        var resultado = monitor.Modificar("Otra web", Web("https://otra.example/"), TimeSpan.FromSeconds(120), 2, null, grupo);

        resultado.EsExito.ShouldBeTrue();
        monitor.Nombre.ShouldBe("Otra web");
        monitor.Intervalo.ShouldBe(TimeSpan.FromSeconds(120));
        monitor.FallosParaIncidente.ShouldBe(2);
        monitor.GrupoId.ShouldBe(grupo);
        ((ConfiguracionHttp)monitor.Configuracion).Url.Host.ShouldBe("otra.example");
    }

    [Fact]
    public void Una_modificacion_invalida_no_cambia_nada()
    {
        var monitor = Crear().Valor;

        monitor.Modificar("Otra", Web(), TimeSpan.FromSeconds(5), 3, null, null).Error.Codigo.ShouldBe("monitor.intervalo_invalido");

        monitor.Nombre.ShouldBe("Mi web");
        monitor.Intervalo.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Un_monitor_no_puede_cambiar_de_tipo()
    {
        var monitor = Crear().Valor;

        monitor.Modificar("Mi web", new ConfiguracionTcp("ejemplo.com", 80), TimeSpan.FromSeconds(60), 3, null, null).Error.Codigo.ShouldBe("monitor.cambio_de_tipo");
    }

    [Fact]
    public void Pausar_y_reanudar()
    {
        var monitor = Crear().Valor;

        monitor.Pausar();
        monitor.Activo.ShouldBeFalse();
        monitor.Reanudar();
        monitor.Activo.ShouldBeTrue();
    }
}

public class GrupoTests
{
    [Theory]
    [InlineData("produccion", true)]
    [InlineData("mis-webs", true)]
    [InlineData("a1", true)]
    [InlineData("a", false)]
    [InlineData("Mayusculas", false)]
    [InlineData("con espacio", false)]
    [InlineData("-empieza", false)]
    [InlineData("doble--guion", false)]
    [InlineData("", false)]
    public void El_identificador_del_grupo_es_apto_para_una_url(string slug, bool valido)
    {
        Grupo.Crear(slug, "Grupo", publico: true).EsExito.ShouldBe(valido);
    }

    [Fact]
    public void El_nombre_es_obligatorio_y_se_limpia()
    {
        Grupo.Crear("produccion", "  Producción  ", true).Valor.Nombre.ShouldBe("Producción");
        Grupo.Crear("produccion", " ", true).Error.Codigo.ShouldBe("grupo.nombre_invalido");
    }
}

public class VentanaMantenimientoTests
{
    private static readonly DateTimeOffset Inicio = new(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);

    private readonly Guid _uno = Guid.NewGuid();
    private readonly Guid _dos = Guid.NewGuid();

    [Fact]
    public void Una_ventana_valida_se_crea_con_los_monitores_sin_repetir()
    {
        var ventana = VentanaMantenimiento.Crear([_uno, _uno, _dos], Inicio, Inicio.AddHours(2), "  Migración de la base de datos ").Valor;

        ventana.MonitorIds.ShouldBe([_uno, _dos]);
        ventana.Motivo.ShouldBe("Migración de la base de datos");
    }

    [Fact]
    public void Cubre_desde_el_inicio_incluido_hasta_el_fin_excluido()
    {
        var ventana = VentanaMantenimiento.Crear([_uno], Inicio, Inicio.AddHours(2), "Reinicio").Valor;

        ventana.Cubre(_uno, Inicio.AddSeconds(-1)).ShouldBeFalse();
        ventana.Cubre(_uno, Inicio).ShouldBeTrue();
        ventana.Cubre(_uno, Inicio.AddHours(1)).ShouldBeTrue();
        ventana.Cubre(_uno, Inicio.AddHours(2).AddTicks(-1)).ShouldBeTrue();
        ventana.Cubre(_uno, Inicio.AddHours(2)).ShouldBeFalse();
    }

    [Fact]
    public void Solo_afecta_a_los_monitores_indicados()
    {
        var ventana = VentanaMantenimiento.Crear([_uno], Inicio, Inicio.AddHours(2), "Reinicio").Valor;

        ventana.Cubre(_dos, Inicio.AddHours(1)).ShouldBeFalse();
    }

    [Fact]
    public void Varias_ventanas_se_comprueban_juntas()
    {
        var ventanas = new[]
        {
            VentanaMantenimiento.Crear([_uno], Inicio, Inicio.AddHours(1), "A").Valor,
            VentanaMantenimiento.Crear([_dos], Inicio.AddHours(5), Inicio.AddHours(6), "B").Valor,
        };

        VentanaMantenimiento.AlgunaCubre(ventanas, _uno, Inicio.AddMinutes(30)).ShouldBeTrue();
        VentanaMantenimiento.AlgunaCubre(ventanas, _uno, Inicio.AddHours(5).AddMinutes(30)).ShouldBeFalse();
        VentanaMantenimiento.AlgunaCubre(ventanas, _dos, Inicio.AddHours(5).AddMinutes(30)).ShouldBeTrue();
        VentanaMantenimiento.AlgunaCubre([], _uno, Inicio).ShouldBeFalse();
    }

    [Fact]
    public void Las_ventanas_invalidas_se_rechazan_con_su_motivo()
    {
        VentanaMantenimiento.Crear([], Inicio, Inicio.AddHours(1), "x").Error.Codigo.ShouldBe("mantenimiento.sin_monitores");
        VentanaMantenimiento.Crear([_uno], Inicio, Inicio, "x").Error.Codigo.ShouldBe("mantenimiento.periodo_invalido");
        VentanaMantenimiento.Crear([_uno], Inicio, Inicio.AddHours(-1), "x").Error.Codigo.ShouldBe("mantenimiento.periodo_invalido");
        VentanaMantenimiento.Crear([_uno], Inicio, Inicio.AddDays(31), "x").Error.Codigo.ShouldBe("mantenimiento.periodo_invalido");
        VentanaMantenimiento.Crear([_uno], Inicio, Inicio.AddDays(30), "x").EsExito.ShouldBeTrue();
        VentanaMantenimiento.Crear([_uno], Inicio, Inicio.AddHours(1), " ").Error.Codigo.ShouldBe("mantenimiento.motivo_invalido");
        VentanaMantenimiento.Crear([_uno], Inicio, Inicio.AddHours(1), new string('x', 201)).Error.Codigo.ShouldBe("mantenimiento.motivo_invalido");
    }
}
