using Bunit;
using Bunit.TestDoubles;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Shouldly;

using Vigia.Contratos;
using Vigia.Dominio.Monitores;
using Vigia.Web.Components.Comunes;
using Vigia.Web.Components.Pages;
using Vigia.Web.Servicios;

namespace Vigia.Web.Tests;

/// <summary>Base de los tests de páginas: API, conexión en vivo y reloj de mentira.</summary>
public abstract class PaginaTest : BunitContext
{
    protected PaginaTest()
    {
        Reloj = new FakeTimeProvider(Datos.Ahora);
        Api = new ApiFalsa();
        Vivo = new ConexionFalsa();
        Services.AddSingleton<IClienteDeApi>(Api);
        Services.AddSingleton<IConexionEnVivo>(Vivo);
        Services.AddSingleton<TimeProvider>(Reloj);
    }

    protected FakeTimeProvider Reloj { get; }

    protected ApiFalsa Api { get; }

    protected ConexionFalsa Vivo { get; }

    protected BunitNavigationManager Navegacion => Services.GetRequiredService<BunitNavigationManager>();
}

public class PanelTests : PaginaTest
{
    private static readonly Guid IdWeb = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    private void DosMonitores()
    {
        Api.Monitores = ApiFalsa.Ok<IReadOnlyList<MonitorDto>>(
        [
            Datos.Monitor("Web", id: IdWeb),
            Datos.Monitor("Base de datos", EstadoMonitor.Caido, latencia: null, disponibilidad: 97.2m),
            Datos.Monitor("Pausado", activo: false),
        ]);
        Api.Latencia = ApiFalsa.Ok<IReadOnlyList<PuntoDeLatencia>>([Datos.Punto(3, 100), Datos.Punto(2, 140), Datos.Punto(1, 90)]);
    }

    [Fact]
    public void Muestra_cada_monitor_con_su_estado_en_texto_latencia_y_disponibilidad()
    {
        DosMonitores();

        var cut = Render<Panel>();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(3));
        var filas = cut.FindAll("tbody tr");
        filas[0].TextContent.ShouldContain("Web");
        filas[0].QuerySelector(".insignia > span")!.TextContent.ShouldBe("Operativo");
        filas[0].TextContent.ShouldContain("120 ms");
        filas[0].TextContent.ShouldContain("99,95 %");
        filas[0].QuerySelector("a")!.GetAttribute("href").ShouldBe($"monitores/{IdWeb}");
        filas[1].QuerySelector(".insignia > span")!.TextContent.ShouldBe("Caído");
        filas[1].TextContent.ShouldContain("—");
        filas[2].QuerySelector(".insignia > span")!.TextContent.ShouldBe("En pausa");
        filas[2].ClassList.ShouldContain("pausado");
    }

    [Fact]
    public void La_tabla_es_accesible_con_titulos_de_columna_y_de_fila()
    {
        DosMonitores();

        var cut = Render<Panel>();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(3));
        cut.FindAll("thead th[scope=col]").Count.ShouldBe(6);
        cut.FindAll("tbody th[scope=row]").Count.ShouldBe(3);
        cut.Find("table caption").TextContent.ShouldBe("Monitores");
    }

    [Fact]
    public void Cuenta_los_monitores_por_estado_y_separa_los_pausados()
    {
        DosMonitores();

        var cut = Render<Panel>();

        cut.WaitForAssertion(() => cut.Find("ul.resumen-panel"));
        var resumen = cut.Find("ul.resumen-panel").TextContent;
        resumen.ShouldContain("1 operativos");
        resumen.ShouldContain("1 caídos");
        resumen.ShouldContain("1 en pausa");
        resumen.ShouldContain("0 con problemas");
    }

    [Fact]
    public void Dibuja_la_mini_grafica_de_24_horas_de_cada_monitor()
    {
        DosMonitores();

        var cut = Render<Panel>();

        cut.WaitForAssertion(() => cut.FindAll("svg.mini").Count.ShouldBe(3));
        Api.Veces("latencia").ShouldBe(3);
        Api.UltimasHoras.ShouldBe(24);
    }

    [Fact]
    public void Sin_monitores_invita_a_crear_el_primero()
    {
        var cut = Render<Panel>();

        cut.WaitForAssertion(() => cut.Find(".vacio h2").TextContent.ShouldBe("Todavía no vigilas nada"));
        cut.Find(".vacio a").GetAttribute("href").ShouldBe("monitores/nuevo");
        cut.FindAll("table").ShouldBeEmpty();
    }

    [Fact]
    public void Si_la_api_falla_lo_dice_en_una_alerta_y_no_se_queda_cargando()
    {
        Api.Monitores = RespuestaDeApi.Fallo<IReadOnlyList<MonitorDto>>(new ErrorDeApi("api_no_disponible", "No se puede contactar con la API."));

        var cut = Render<Panel>();

        cut.WaitForAssertion(() => cut.Find("p.error[role=alert]").TextContent.ShouldBe("No se puede contactar con la API."));
    }

    [Fact]
    public void Con_la_sesion_caducada_lleva_a_entrar_de_nuevo()
    {
        Api.Monitores = ApiFalsa.Caducada<IReadOnlyList<MonitorDto>>();

        Render<Panel>();

        Navegacion.Uri.ShouldEndWith("/entrar");
    }

    [Fact]
    public async Task Un_mensaje_en_vivo_actualiza_estado_latencia_y_ultima_comprobacion_sin_recargar()
    {
        DosMonitores();
        var cut = Render<Panel>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(3));
        var antes = Api.Veces("monitores");

        await cut.InvokeAsync(() => Vivo.EmitirAsync(new ComprobacionEnVivo(IdWeb, Datos.Ahora, true, 640, EstadoMonitor.Degradado, null)));

        cut.WaitForAssertion(() =>
        {
            var fila = cut.FindAll("tbody tr")[0];
            fila.QuerySelector(".insignia > span")!.TextContent.ShouldBe("Lento");
            fila.TextContent.ShouldContain("640 ms");
            fila.TextContent.ShouldContain("ahora mismo");
        });
        Api.Veces("monitores").ShouldBe(antes, "el mensaje basta: no se vuelve a pedir la lista");
    }

    [Fact]
    public async Task Una_caida_se_anuncia_en_una_region_para_lectores_de_pantalla()
    {
        DosMonitores();
        var cut = Render<Panel>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(3));

        await cut.InvokeAsync(() => Vivo.EmitirAsync(new ComprobacionEnVivo(IdWeb, Datos.Ahora, false, 10000, EstadoMonitor.Caido, "abierto")));

        cut.WaitForAssertion(() => cut.Find(".anuncio").TextContent.ShouldBe("«Web» está caído."));
        cut.Find(".anuncio").GetAttribute("aria-live").ShouldBe("polite");

        await cut.InvokeAsync(() => Vivo.EmitirAsync(new ComprobacionEnVivo(IdWeb, Datos.Ahora.AddMinutes(1), true, 90, EstadoMonitor.Operativo, "cerrado")));

        cut.WaitForAssertion(() => cut.Find(".anuncio").TextContent.ShouldBe("«Web» se ha recuperado."));
    }

    [Fact]
    public async Task Un_mensaje_de_un_monitor_que_no_esta_en_la_lista_se_ignora()
    {
        DosMonitores();
        var cut = Render<Panel>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(3));

        await cut.InvokeAsync(() => Vivo.EmitirAsync(new ComprobacionEnVivo(Guid.NewGuid(), Datos.Ahora, false, 1, EstadoMonitor.Caido, "abierto")));

        cut.Find(".anuncio").TextContent.ShouldBeEmpty();
        cut.FindAll(".insignia.caido").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Una_comprobacion_fallida_no_enseña_como_latencia_el_tiempo_que_se_espero()
    {
        DosMonitores();
        var cut = Render<Panel>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(3));

        await cut.InvokeAsync(() => Vivo.EmitirAsync(new ComprobacionEnVivo(IdWeb, Datos.Ahora, false, 10000, EstadoMonitor.Sospechoso, null)));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr")[0].TextContent.ShouldNotContain("10 s"));
    }

    [Fact]
    public async Task Al_reconectar_se_vuelve_a_pedir_todo_por_si_se_perdio_algo()
    {
        DosMonitores();
        var cut = Render<Panel>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(3));

        await cut.InvokeAsync(() => Vivo.ReconectarAsync());

        Api.Veces("monitores").ShouldBe(2);
    }

    [Fact]
    public async Task Dice_si_esta_en_vivo_y_lo_actualiza_al_cortarse_la_conexion()
    {
        DosMonitores();
        var cut = Render<Panel>();
        cut.WaitForAssertion(() => cut.Find(".conexion").TextContent.ShouldBe("En vivo"));

        await cut.InvokeAsync(() => Vivo.CambiarEstadoAsync(false));

        cut.Find(".conexion").TextContent.ShouldBe("Sin conexión en vivo");
        cut.Find(".conexion").GetAttribute("role").ShouldBe("status");
    }

    [Fact]
    public async Task Al_cerrar_la_pagina_se_da_de_baja_de_la_conexion_y_la_cierra()
    {
        DosMonitores();
        var cut = Render<Panel>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(3));
        Vivo.SuscriptoresDeRecibida.ShouldBe(1);

        await DisposeComponentsAsync();

        Vivo.SuscriptoresDeRecibida.ShouldBe(0);
        Vivo.Descartada.ShouldBeTrue();
    }

    [Fact]
    public void El_tiempo_transcurrido_envejece_solo_cada_15_segundos()
    {
        DosMonitores();
        var cut = Render<Panel>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr")[0].TextContent.ShouldContain("hace 1 min"));

        Reloj.Advance(TimeSpan.FromMinutes(5));

        cut.WaitForAssertion(() => cut.FindAll("tbody tr")[0].TextContent.ShouldContain("hace 6 min"));
    }

    [Fact]
    public void Los_enlaces_de_nuevo_monitor_existen_arriba_y_la_pagina_tiene_un_unico_h1()
    {
        var cut = Render<Panel>();

        cut.WaitForAssertion(() => cut.FindAll("h1").Count.ShouldBe(1));
        cut.Find("h1").TextContent.ShouldBe("Panel");
        cut.Find(".barra-titulo a.principal").GetAttribute("href").ShouldBe("monitores/nuevo");
    }
}

public class DetalleMonitorTests : PaginaTest
{
    private static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private void Preparar(IncidenteAbiertoDto? incidente = null, bool activo = true)
    {
        Api.Monitor = ApiFalsa.Ok(Datos.Monitor("Mi web", id: Id, incidente: incidente, activo: activo));
        Api.Disponibilidad = ApiFalsa.Ok(new DisponibilidadDto(100m, 99.5m, 99.95m, 99.123m));
        Api.Barras = ApiFalsa.Ok<IReadOnlyList<BarraDiaria>>([Datos.Barra(1, EstadoMonitor.Operativo), Datos.Barra(0, EstadoMonitor.Caido, 98m, 1000)]);
        Api.Latencia = ApiFalsa.Ok<IReadOnlyList<PuntoDeLatencia>>([Datos.Punto(2), Datos.Punto(1)]);
        Api.Incidentes = ApiFalsa.Ok<IReadOnlyList<IncidenteDto>>(
        [
            new IncidenteDto(Guid.NewGuid(), Id, "Mi web", Datos.Ahora.AddDays(-2), Datos.Ahora.AddDays(-2).AddMinutes(12), "Recuperado", 720, 3, "Error 500"),
        ]);
    }

    private IRenderedComponent<DetalleMonitor> Mostrar()
    {
        var cut = Render<DetalleMonitor>(p => p.Add(c => c.Id, Id));
        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldBe("Mi web"));

        return cut;
    }

    [Fact]
    public void Muestra_el_monitor_sus_disponibilidades_las_barras_la_grafica_y_los_incidentes()
    {
        Preparar();

        var cut = Mostrar();

        var cifras = cut.Find("dl.cifras").TextContent;
        cifras.ShouldContain("100 %");
        cifras.ShouldContain("99,95 %");
        cifras.ShouldContain("99,12 %");
        cut.FindAll("svg.svg-barras").Count.ShouldBe(1);
        cut.FindAll("svg.svg-grafica").Count.ShouldBe(1);
        cut.Find("#titulo-incidentes + div table").TextContent.ShouldContain("Error 500");
        cut.Find("#titulo-incidentes + div table").TextContent.ShouldContain("12 min 0 s");
    }

    [Fact]
    public void Un_incidente_en_curso_se_destaca_con_la_causa_y_la_duracion()
    {
        Preparar(new IncidenteAbiertoDto(Guid.NewGuid(), Datos.Ahora.AddMinutes(-20), "Conexión rechazada", 5));

        var cut = Mostrar();

        var caja = cut.Find("#incidente-abierto").ParentElement!;
        caja.TextContent.ShouldContain("Incidente en curso");
        caja.TextContent.ShouldContain("20 min 0 s");
        caja.TextContent.ShouldContain("5 fallos seguidos");
        caja.QuerySelector("code")!.TextContent.ShouldBe("Conexión rechazada");
    }

    [Fact]
    public void Un_monitor_que_no_existe_lo_dice_y_no_se_queda_cargando()
    {
        var cut = Render<DetalleMonitor>(p => p.Add(c => c.Id, Id));

        cut.WaitForAssertion(() => cut.Find("p.error[role=alert]").TextContent.ShouldBe("Este monitor no existe (quizá se borró)."));
        cut.FindAll("h1").ShouldBeEmpty();
    }

    [Fact]
    public void Probar_ahora_muestra_el_resultado_en_una_region_viva()
    {
        Preparar();
        var cut = Mostrar();

        cut.FindAll("button").First(b => b.TextContent.Contains("Probar ahora", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("#titulo-prueba").ParentElement!.GetAttribute("aria-live").ShouldBe("polite");
            cut.Find("#titulo-prueba").ParentElement!.TextContent.ShouldContain("Correcta en 42 ms");
        });
        Api.Veces("probar-monitor").ShouldBe(1);
    }

    [Fact]
    public void Una_prueba_fallida_enseña_la_causa()
    {
        Preparar();
        Api.Prueba = ApiFalsa.Ok(new PruebaDto(false, 0, "TiempoAgotado", "Sin respuesta en 10 s.", new Dictionary<string, string>()));
        var cut = Mostrar();

        cut.FindAll("button").First(b => b.TextContent.Contains("Probar ahora", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => cut.Find("#titulo-prueba").ParentElement!.TextContent.ShouldContain("Fallida (TiempoAgotado): Sin respuesta en 10 s."));
    }

    [Fact]
    public void Pausar_y_reanudar_llaman_a_la_api_y_recargan()
    {
        Preparar();
        var cut = Mostrar();

        cut.FindAll("button").First(b => b.TextContent == "Pausar").Click();

        cut.WaitForAssertion(() => Api.Veces("pausar").ShouldBe(1));
        cut.WaitForAssertion(() => Api.Veces("monitor").ShouldBe(2));
    }

    [Fact]
    public void Un_monitor_en_pausa_ofrece_reanudarlo()
    {
        Preparar(activo: false);
        var cut = Mostrar();

        cut.FindAll("button").ShouldContain(b => b.TextContent == "Reanudar");
        cut.Find(".insignia > span").TextContent.ShouldBe("En pausa");
    }

    [Fact]
    public void Borrar_pide_confirmacion_antes_de_hacer_nada()
    {
        Preparar();
        var cut = Mostrar();

        cut.FindAll("button").First(b => b.TextContent == "Borrar").Click();

        Api.Veces("borrar-monitor").ShouldBe(0);
        var dialogo = cut.Find("[role=alertdialog]");
        dialogo.TextContent.ShouldContain("¿Borrar «Mi web»?");
        dialogo.TextContent.ShouldContain("histórico");

        dialogo.QuerySelectorAll("button").First(b => b.TextContent == "Cancelar").Click();
        cut.FindAll("[role=alertdialog]").ShouldBeEmpty();
        Api.Veces("borrar-monitor").ShouldBe(0);
    }

    [Fact]
    public void Confirmar_el_borrado_lo_borra_y_vuelve_al_panel()
    {
        Preparar();
        var cut = Mostrar();
        cut.FindAll("button").First(b => b.TextContent == "Borrar").Click();

        cut.Find("[role=alertdialog]").QuerySelectorAll("button").First(b => b.TextContent == "Sí, borrar").Click();

        cut.WaitForAssertion(() => Navegacion.Uri.ShouldEndWith("/"));
        Api.Veces("borrar-monitor").ShouldBe(1);
    }

    [Fact]
    public void Cambiar_el_periodo_de_la_grafica_pide_esas_horas_y_marca_el_boton_activo()
    {
        Preparar();
        var cut = Mostrar();

        cut.FindAll(".selector button").First(b => b.TextContent == "7 días").Click();

        cut.WaitForAssertion(() => Api.UltimasHoras.ShouldBe(168));
        var botones = cut.FindAll(".selector button");
        botones.Single(b => b.GetAttribute("aria-pressed") == "true").TextContent.ShouldBe("7 días");
        cut.Find(".selector").GetAttribute("role").ShouldBe("group");
    }

    [Fact]
    public async Task Un_mensaje_en_vivo_del_monitor_actualiza_su_estado_y_uno_de_otro_monitor_no_hace_nada()
    {
        Preparar();
        var cut = Mostrar();

        await cut.InvokeAsync(() => Vivo.EmitirAsync(new ComprobacionEnVivo(Guid.NewGuid(), Datos.Ahora, false, 1, EstadoMonitor.Caido, "abierto")));
        cut.Find(".insignia > span").TextContent.ShouldBe("Operativo");

        await cut.InvokeAsync(() => Vivo.EmitirAsync(new ComprobacionEnVivo(Id, Datos.Ahora, true, 700, EstadoMonitor.Degradado, null)));
        cut.WaitForAssertion(() => cut.Find(".insignia > span").TextContent.ShouldBe("Lento"));
    }

    [Fact]
    public async Task Abrirse_un_incidente_recarga_la_pagina_para_enseñar_su_causa()
    {
        Preparar();
        var cut = Mostrar();
        Api.Monitor = ApiFalsa.Ok(Datos.Monitor("Mi web", EstadoMonitor.Caido, id: Id, incidente: new IncidenteAbiertoDto(Guid.NewGuid(), Datos.Ahora, "Sin respuesta en 10 s.", 3)));

        await cut.InvokeAsync(() => Vivo.EmitirAsync(new ComprobacionEnVivo(Id, Datos.Ahora, false, 10000, EstadoMonitor.Caido, "abierto")));

        cut.WaitForAssertion(() => cut.Find("#incidente-abierto").ParentElement!.TextContent.ShouldContain("Sin respuesta en 10 s."));
        cut.Find(".anuncio").TextContent.ShouldBe("«Mi web» está caído.");
    }

    [Fact]
    public void Si_una_parte_falla_el_resto_de_la_pagina_se_sigue_viendo()
    {
        Preparar();
        Api.Barras = RespuestaDeApi.Fallo<IReadOnlyList<BarraDiaria>>(new ErrorDeApi("x", "fallo"));
        Api.Disponibilidad = RespuestaDeApi.Fallo<DisponibilidadDto>(new ErrorDeApi("x", "fallo"));

        var cut = Mostrar();

        cut.Find("dl.cifras").TextContent.ShouldContain("—");
        cut.FindAll("svg.svg-grafica").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Al_cerrar_se_da_de_baja_de_la_conexion()
    {
        Preparar();
        Mostrar();

        await DisposeComponentsAsync();

        Vivo.SuscriptoresDeRecibida.ShouldBe(0);
    }
}

public class PaginaDeEstadoTests : PaginaTest
{
    private static EstadoPublico Pagina(EstadoGeneral general = EstadoGeneral.IncidenteParcial) =>
        new(
            "Producción",
            "produccion",
            general,
            Datos.Ahora,
            [
                new ServicioPublico("Web", EstadoMonitor.Operativo, 99.98m, Enumerable.Range(0, 90).Select(i => Datos.Barra(89 - i, EstadoMonitor.Operativo)).ToList()),
                new ServicioPublico("API", EstadoMonitor.Caido, null, [Datos.Barra(1, null, null), Datos.Barra(0, EstadoMonitor.Caido, 50m, 43200)]),
            ],
            [
                new IncidentePublico("API", Datos.Ahora.AddMinutes(-20), null, 1200),
                new IncidentePublico("Web", Datos.Ahora.AddDays(-3), Datos.Ahora.AddDays(-3).AddMinutes(10), 600),
            ]);

    [Fact]
    public void Enseña_el_estado_general_con_palabras_los_servicios_las_barras_y_los_incidentes()
    {
        Api.Publico = ApiFalsa.Ok(Pagina());

        var cut = Render<PaginaDeEstado>(p => p.Add(c => c.Slug, "produccion"));

        cut.Find("h1").TextContent.ShouldBe("Producción");
        cut.Find(".resumen").TextContent.ShouldContain("Hay servicios caídos");
        cut.Find(".resumen").GetAttribute("role").ShouldBe("status");
        cut.FindAll("article.servicio").Count.ShouldBe(2);
        cut.FindAll("article.servicio")[0].TextContent.ShouldContain("99,98 %");
        cut.FindAll("article.servicio")[1].TextContent.ShouldContain("—");
        cut.FindAll("svg.svg-barras").Count.ShouldBe(2);
        var incidentes = cut.Find("ul.lista-incidentes").TextContent;
        incidentes.ShouldContain("En curso");
        incidentes.ShouldContain("duró 10 min 0 s");
    }

    [Fact]
    public void Pide_la_pagina_a_la_api_una_sola_vez()
    {
        Api.Publico = ApiFalsa.Ok(Pagina());

        var cut = Render<PaginaDeEstado>(p => p.Add(c => c.Slug, "produccion"));

        // La cabecera (refresco y robots) la pinta HeadOutlet, que solo existe en la web completa: se comprueba en los tests de integración.
        Api.Veces("publico:produccion").ShouldBe(1);
    }

    [Fact]
    public void Sin_servicios_ni_incidentes_lo_dice_claramente()
    {
        Api.Publico = ApiFalsa.Ok(Pagina(EstadoGeneral.SinDatos) with { Servicios = [], Incidentes = [] });

        var cut = Render<PaginaDeEstado>(p => p.Add(c => c.Slug, "produccion"));

        cut.Markup.ShouldContain("todavía no tiene servicios");
        cut.Markup.ShouldContain("No ha habido incidentes.");
        cut.Find(".resumen").TextContent.ShouldContain("Sin datos todavía");
    }

    [Fact]
    public void Una_pagina_que_no_existe_o_no_es_publica_dice_lo_mismo()
    {
        var cut = Render<PaginaDeEstado>(p => p.Add(c => c.Slug, "interno"));

        cut.Find("h1").TextContent.ShouldBe("No encontrado");
        cut.Markup.ShouldContain("no existe o no es pública");
    }

    [Fact]
    public void Si_la_api_no_responde_no_se_hace_pasar_por_una_pagina_inexistente()
    {
        Api.Publico = RespuestaDeApi.Fallo<EstadoPublico>(new ErrorDeApi("api_no_disponible", "No se puede contactar con la API."));

        var cut = Render<PaginaDeEstado>(p => p.Add(c => c.Slug, "produccion"));

        cut.Markup.ShouldContain("No se puede contactar con la API.");
    }
}

public class FormularioDeMonitorTests : PaginaTest
{
    private static readonly string[] CamposDeTipo = ["url", "host-tls", "nombre-dns", "host-tcp", "host-icmp"];

    private IRenderedComponent<FormularioDeMonitor> Mostrar(ModeloDeMonitor? modelo = null, bool tipoFijo = false, IReadOnlyList<GrupoDto>? grupos = null, Action<CuerpoMonitor>? alGuardar = null) =>
        Render<FormularioDeMonitor>(p =>
        {
            p.Add(c => c.Modelo, modelo ?? new ModeloDeMonitor { Nombre = "Mi web" });
            p.Add(c => c.TipoFijo, tipoFijo);
            p.Add(c => c.Grupos, grupos ?? []);
            p.Add(c => c.AlGuardar, (CuerpoMonitor cuerpo) => alGuardar?.Invoke(cuerpo));
        });

    [Theory]
    [InlineData("Http", "url")]
    [InlineData("Tls", "host-tls")]
    [InlineData("Dns", "nombre-dns")]
    [InlineData("Tcp", "host-tcp")]
    [InlineData("Icmp", "host-icmp")]
    public void Cada_tipo_enseña_solo_los_campos_que_le_corresponden(string tipo, string campo)
    {
        var cut = Mostrar(new ModeloDeMonitor { Tipo = tipo });

        cut.FindAll($"#{campo}").Count.ShouldBe(1);
        CamposDeTipo.Where(c => c != campo).ShouldAllBe(otro => cut.FindAll($"#{otro}").Count == 0);
    }

    [Fact]
    public void Todos_los_campos_tienen_una_etiqueta_asociada()
    {
        var cut = Mostrar();

        foreach (var campo in cut.FindAll("input:not([type=checkbox]), select"))
        {
            var id = campo.Id;
            id.ShouldNotBeNullOrEmpty($"un campo sin id no puede tener etiqueta: {campo.OuterHtml}");
            cut.FindAll($"label[for='{id}']").Count.ShouldBe(1, $"falta la etiqueta de #{id}");
        }
    }

    [Fact]
    public void Las_casillas_tambien_tienen_etiqueta()
    {
        var cut = Mostrar();

        foreach (var casilla in cut.FindAll("input[type=checkbox]"))
        {
            cut.FindAll($"label[for='{casilla.Id}']").Count.ShouldBe(1);
        }
    }

    [Fact]
    public void Los_campos_con_explicacion_la_enlazan_para_los_lectores_de_pantalla()
    {
        var cut = Mostrar();

        foreach (var campo in cut.FindAll("[aria-describedby]"))
        {
            cut.FindAll($"#{campo.GetAttribute("aria-describedby")}").Count.ShouldBe(1, $"la ayuda de #{campo.Id} no existe");
        }
    }

    [Fact]
    public void La_ventana_de_red_privada_avisa_del_riesgo()
    {
        var cut = Mostrar();

        cut.Find("#ayuda-red").TextContent.ShouldContain("Por seguridad");
        cut.Find("#red-privada").GetAttribute("aria-describedby").ShouldBe("ayuda-red");
    }

    [Fact]
    public void Un_monitor_existente_no_puede_cambiar_de_tipo_y_se_explica_por_que()
    {
        var cut = Mostrar(new ModeloDeMonitor { Tipo = "Tcp", Host = "x" }, tipoFijo: true);

        cut.Find("#tipo").HasAttribute("disabled").ShouldBeTrue();
        cut.Markup.ShouldContain("El tipo no se puede cambiar");
    }

    [Fact]
    public void Guardar_entrega_el_cuerpo_con_lo_escrito()
    {
        CuerpoMonitor? recibido = null;
        var cut = Mostrar(new ModeloDeMonitor { Nombre = "Mi web", Url = "https://ejemplo.com/" }, alGuardar: c => recibido = c);

        cut.Find("form").Submit();

        recibido.ShouldNotBeNull();
        recibido.Nombre.ShouldBe("Mi web");
        recibido.Configuracion.GetProperty("url").GetString().ShouldBe("https://ejemplo.com/");
        recibido.IntervaloSegundos.ShouldBe(60);
    }

    [Fact]
    public void El_selector_de_grupo_lista_los_grupos_y_admite_no_tener()
    {
        var cut = Mostrar(grupos: [new GrupoDto(Guid.NewGuid(), "produccion", "Producción", true, 2)]);

        var opciones = cut.FindAll("#grupo option").Select(o => o.TextContent).ToList();
        opciones.ShouldBe(["Sin grupo", "Producción"]);
    }

    [Fact]
    public void Probar_ahora_manda_la_configuracion_actual_y_enseña_el_resultado_sin_guardar()
    {
        var cut = Mostrar(new ModeloDeMonitor { Tipo = "Tcp", Host = "db.ejemplo.com", Puerto = 5432 });

        cut.FindAll("button").First(b => b.TextContent.Contains("Probar ahora", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Correcta"));
        Api.UltimaConfiguracionProbada!.Value.GetProperty("host").GetString().ShouldBe("db.ejemplo.com");
        Api.Veces("crear-monitor").ShouldBe(0);
    }

    [Fact]
    public void Un_error_de_la_api_al_guardar_se_enseña_en_una_alerta()
    {
        var cut = Render<FormularioDeMonitor>(p =>
        {
            p.Add(c => c.Modelo, new ModeloDeMonitor { Nombre = "X" });
            p.Add(c => c.Error, "El intervalo entre comprobaciones debe ser de 30 segundos como mínimo.");
            p.Add(c => c.AlGuardar, (CuerpoMonitor _) => { });
        });

        cut.Find("p.error[role=alert]").TextContent.ShouldContain("30 segundos");
    }

    [Fact]
    public void Mientras_guarda_el_boton_se_desactiva_para_no_crear_dos_veces_el_monitor()
    {
        var cut = Render<FormularioDeMonitor>(p =>
        {
            p.Add(c => c.Modelo, new ModeloDeMonitor { Nombre = "X" });
            p.Add(c => c.Guardando, true);
            p.Add(c => c.AlGuardar, (CuerpoMonitor _) => { });
        });

        var boton = cut.Find("button[type=submit]");
        boton.HasAttribute("disabled").ShouldBeTrue();
        boton.TextContent.ShouldBe("Guardando…");
    }
}

public class NuevoYEditarMonitorTests : PaginaTest
{
    [Fact]
    public void Crear_un_monitor_lo_manda_a_la_api_y_lleva_a_su_pagina()
    {
        var creado = Datos.Monitor("Nuevo", id: Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
        Api.Guardado = ApiFalsa.Ok(creado);
        var cut = Render<NuevoMonitor>();
        cut.Find("#nombre").Change("Nuevo");
        cut.Find("#url").Change("https://ejemplo.com/");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Navegacion.Uri.ShouldEndWith($"/monitores/{creado.Id}"));
        Api.Veces("crear-monitor").ShouldBe(1);
        Api.UltimoCuerpoDeMonitor!.Nombre.ShouldBe("Nuevo");
    }

    [Fact]
    public void Si_la_api_rechaza_el_monitor_se_queda_en_el_formulario_con_el_motivo()
    {
        Api.Guardado = RespuestaDeApi.Fallo<MonitorDto>(new ErrorDeApi("monitor.intervalo_invalido", "El intervalo entre comprobaciones debe ser de 30 segundos como mínimo."));
        var cut = Render<NuevoMonitor>();
        cut.Find("#nombre").Change("Nuevo");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("p.error[role=alert]").TextContent.ShouldContain("30 segundos"));
        Navegacion.Uri.ShouldNotContain("/monitores/");
    }

    [Fact]
    public void Editar_carga_el_monitor_con_el_tipo_fijo_y_guarda_los_cambios()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
        Api.Monitor = ApiFalsa.Ok(Datos.Monitor("Antes", id: id, url: "https://viejo.example/"));
        Api.Guardado = ApiFalsa.Ok(Datos.Monitor("Después", id: id));
        var cut = Render<EditarMonitor>(p => p.Add(c => c.Id, id));

        cut.WaitForAssertion(() => cut.Find("#nombre").GetAttribute("value").ShouldBe("Antes"));
        cut.Find("#url").GetAttribute("value").ShouldBe("https://viejo.example/");
        cut.Find("#tipo").HasAttribute("disabled").ShouldBeTrue();

        cut.Find("#nombre").Change("Después");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Navegacion.Uri.ShouldEndWith($"/monitores/{id}"));
        Api.Veces("modificar-monitor").ShouldBe(1);
        Api.UltimoCuerpoDeMonitor!.Nombre.ShouldBe("Después");
    }

    [Fact]
    public void Editar_un_monitor_que_no_existe_lo_dice()
    {
        var cut = Render<EditarMonitor>(p => p.Add(c => c.Id, Guid.NewGuid()));

        cut.WaitForAssertion(() => cut.Find("p.error[role=alert]").TextContent.ShouldBe("Este monitor no existe."));
        cut.FindAll("form").ShouldBeEmpty();
    }
}

public class GruposYMantenimientosPaginasTests : PaginaTest
{
    [Fact]
    public void Lista_los_grupos_con_enlace_a_su_pagina_publica_solo_si_lo_es()
    {
        Api.Grupos = ApiFalsa.Ok<IReadOnlyList<GrupoDto>>(
        [
            new GrupoDto(Guid.NewGuid(), "produccion", "Producción", true, 3),
            new GrupoDto(Guid.NewGuid(), "interno", "Interno", false, 1),
        ]);

        var cut = Render<Grupos>();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(2));
        cut.FindAll("tbody tr")[0].QuerySelector("a")!.GetAttribute("href").ShouldBe("estado/produccion");
        cut.FindAll("tbody tr")[1].QuerySelector("a").ShouldBeNull();
        cut.FindAll("tbody tr")[1].TextContent.ShouldContain("Privada");
    }

    [Fact]
    public void Crear_un_grupo_manda_el_identificador_y_el_nombre_recortados()
    {
        Api.GrupoCreado = ApiFalsa.Ok(new GrupoDto(Guid.NewGuid(), "produccion", "Producción", true, 0));
        var cut = Render<Grupos>();
        cut.WaitForAssertion(() => cut.Find("#grupo-nombre"));
        cut.Find("#grupo-nombre").Change("  Producción ");
        cut.Find("#grupo-slug").Change(" produccion ");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Api.Veces("crear-grupo").ShouldBe(1));
        Api.UltimoCuerpoDeGrupo.ShouldBe(new CuerpoGrupo("produccion", "Producción", true));
    }

    [Fact]
    public void Un_identificador_repetido_se_explica_sin_perder_la_pagina()
    {
        Api.GrupoCreado = RespuestaDeApi.Fallo<GrupoDto>(new ErrorDeApi("grupo.slug_repetido", "Ya existe un grupo con ese identificador."));
        var cut = Render<Grupos>();
        cut.WaitForAssertion(() => cut.Find("#grupo-nombre"));
        cut.Find("#grupo-nombre").Change("X");
        cut.Find("#grupo-slug").Change("x");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("p.error[role=alert]").TextContent.ShouldBe("Ya existe un grupo con ese identificador."));
    }

    [Fact]
    public void Borrar_un_grupo_pide_confirmacion_y_avisa_de_que_los_monitores_se_conservan()
    {
        var id = Guid.NewGuid();
        Api.Grupos = ApiFalsa.Ok<IReadOnlyList<GrupoDto>>([new GrupoDto(id, "produccion", "Producción", true, 3)]);
        var cut = Render<Grupos>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(1));

        cut.Find("tbody button").Click();

        Api.Veces("borrar-grupo").ShouldBe(0);
        cut.Find("tbody tr").TextContent.ShouldContain("Sus monitores se conservan");

        cut.FindAll("tbody button").First(b => b.TextContent == "Sí").Click();

        cut.WaitForAssertion(() => Api.Veces("borrar-grupo").ShouldBe(1));
    }

    [Fact]
    public void Las_ventanas_de_mantenimiento_se_listan_con_los_nombres_de_sus_monitores_y_la_hora_en_utc()
    {
        var monitor = Datos.Monitor("Web");
        Api.Monitores = ApiFalsa.Ok<IReadOnlyList<MonitorDto>>([monitor]);
        Api.Mantenimientos = ApiFalsa.Ok<IReadOnlyList<MantenimientoDto>>([new MantenimientoDto(Guid.NewGuid(), [monitor.Id, Guid.NewGuid()], Datos.Ahora.AddHours(1), Datos.Ahora.AddHours(3), "Migración")]);

        var cut = Render<Mantenimientos>();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(1));
        var fila = cut.Find("tbody tr").TextContent;
        fila.ShouldContain("Migración");
        fila.ShouldContain("15 oct 2026, 11:30 UTC");
        fila.ShouldContain("Web, (borrado)");
    }

    [Fact]
    public void Programar_un_mantenimiento_manda_las_horas_como_utc_y_los_monitores_elegidos()
    {
        var web = Datos.Monitor("Web");
        var bd = Datos.Monitor("BD");
        Api.Monitores = ApiFalsa.Ok<IReadOnlyList<MonitorDto>>([web, bd]);
        Api.MantenimientoCreado = ApiFalsa.Ok(new MantenimientoDto(Guid.NewGuid(), [web.Id], Datos.Ahora, Datos.Ahora.AddHours(1), "Reinicio"));
        var cut = Render<Mantenimientos>();
        cut.WaitForAssertion(() => cut.Find("#motivo"));
        cut.Find("#motivo").Change("  Reinicio  ");
        cut.Find($"#m-{web.Id}").Change(true);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Api.Veces("crear-mantenimiento").ShouldBe(1));
        var cuerpo = Api.UltimoCuerpoDeMantenimiento!;
        cuerpo.Motivo.ShouldBe("Reinicio");
        cuerpo.MonitorIds.ShouldBe([web.Id]);
        cuerpo.Inicio.Offset.ShouldBe(TimeSpan.Zero);
        cuerpo.Inicio.ShouldBe(Datos.Ahora);
        cuerpo.Fin.ShouldBe(Datos.Ahora.AddHours(1));
    }

    [Fact]
    public void Sin_monitores_no_ofrece_programar_nada()
    {
        var cut = Render<Mantenimientos>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Crea primero algún monitor."));
        cut.FindAll("form").ShouldBeEmpty();
    }

    [Fact]
    public void Cancelar_una_ventana_llama_a_la_api_con_un_nombre_accesible()
    {
        Api.Mantenimientos = ApiFalsa.Ok<IReadOnlyList<MantenimientoDto>>([new MantenimientoDto(Guid.NewGuid(), [Guid.NewGuid()], Datos.Ahora, Datos.Ahora.AddHours(1), "Migración")]);
        var cut = Render<Mantenimientos>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(1));

        var boton = cut.Find("tbody button");
        boton.GetAttribute("aria-label").ShouldBe("Cancelar el mantenimiento Migración");
        boton.Click();

        cut.WaitForAssertion(() => Api.Veces("borrar-mantenimiento").ShouldBe(1));
    }
}
