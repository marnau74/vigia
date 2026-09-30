using Bunit;

using Shouldly;

using Vigia.Contratos;
using Vigia.Dominio.Monitores;
using Vigia.Web.Components.Comunes;

namespace Vigia.Web.Tests;

public class InsigniaDeEstadoTests : BunitContext
{
    [Theory]
    [InlineData(EstadoMonitor.Operativo, "Operativo", "ok")]
    [InlineData(EstadoMonitor.Degradado, "Lento", "aviso")]
    [InlineData(EstadoMonitor.Sospechoso, "Comprobando", "aviso")]
    [InlineData(EstadoMonitor.Caido, "Caído", "caido")]
    [InlineData(EstadoMonitor.Mantenimiento, "Mantenimiento", "mantenimiento")]
    [InlineData(EstadoMonitor.Desconocido, "Sin datos", "desconocido")]
    public void El_estado_siempre_se_dice_con_palabras_y_con_una_forma_y_no_solo_con_color(EstadoMonitor estado, string texto, string clase)
    {
        var cut = Render<InsigniaDeEstado>(p => p.Add(c => c.Estado, estado));

        cut.Find("span.insignia").ClassList.ShouldContain(clase);
        cut.Find("span.insignia > span").TextContent.ShouldBe(texto);
        var forma = cut.Find("svg");
        forma.GetAttribute("aria-hidden").ShouldBe("true", "la forma es decorativa: la palabra ya lo dice para quien usa un lector de pantalla");
        forma.InnerHtml.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Cada_estado_tiene_una_forma_distinta_para_quien_no_distingue_colores()
    {
        var formas = Enum.GetValues<EstadoMonitor>()
            .Where(e => e != EstadoMonitor.Sospechoso)
            .Select(e => Render<InsigniaDeEstado>(p => p.Add(c => c.Estado, e)).Find("svg").InnerHtml)
            .ToList();

        formas.Distinct().Count().ShouldBe(formas.Count);
    }
}

public class LeyendaYFormaTests : BunitContext
{
    [Fact]
    public void La_leyenda_nombra_todos_los_estados_y_es_un_grupo_etiquetado()
    {
        var cut = Render<LeyendaDeBarras>();

        cut.Find(".leyenda").GetAttribute("role").ShouldBe("group");
        cut.Find(".leyenda").GetAttribute("aria-label").ShouldBe("Leyenda de las barras");
        cut.Find(".leyenda").TextContent.ShouldContain("Con caída");
        cut.FindAll("pattern").Count.ShouldBe(1);
    }

    [Fact]
    public void Solo_forma_dibuja_el_icono_sin_repetir_la_palabra()
    {
        var cut = Render<InsigniaDeEstado>(p => p.Add(c => c.Estado, EstadoMonitor.Caido).Add(c => c.SoloForma, true));

        cut.FindAll("svg").Count.ShouldBe(1);
        cut.Find("span.insignia").TextContent.Trim().ShouldBeEmpty();
    }
}

public class MiniGraficaTests : BunitContext
{
    [Fact]
    public void Sin_valores_se_muestra_una_raya_y_no_una_grafica_vacia()
    {
        var cut = Render<MiniGrafica>(p => p.Add(c => c.Valores, []));

        cut.FindAll("svg").ShouldBeEmpty();
        cut.Find(".sin-datos").TextContent.ShouldBe("—");
    }

    [Fact]
    public void Con_valores_dibuja_una_linea_y_la_describe_para_los_lectores_de_pantalla()
    {
        var cut = Render<MiniGrafica>(p => p.Add(c => c.Valores, new double?[] { 100, 250, 180, 900 }));

        cut.FindAll("polyline").Count.ShouldBe(1);
        var svg = cut.Find("svg");
        svg.GetAttribute("role").ShouldBe("img");
        svg.GetAttribute("aria-label").ShouldBe("Latencia de las últimas 24 horas: de 100 ms a 900 ms");
    }

    [Fact]
    public void Un_hueco_en_los_datos_corta_la_linea_y_no_inventa_lo_que_no_se_midio()
    {
        var cut = Render<MiniGrafica>(p => p.Add(c => c.Valores, new double?[] { 100, 120, null, 130, 140 }));

        cut.FindAll("polyline").Count.ShouldBe(2);
    }

    [Fact]
    public void Un_punto_aislado_se_dibuja_como_un_punto()
    {
        var cut = Render<MiniGrafica>(p => p.Add(c => c.Valores, new double?[] { null, 100, null }));

        cut.FindAll("polyline").ShouldBeEmpty();
        cut.FindAll("circle").Count.ShouldBe(1);
    }

    [Fact]
    public void Todos_los_valores_iguales_dan_una_linea_horizontal_sin_dividir_por_cero()
    {
        var cut = Render<MiniGrafica>(p => p.Add(c => c.Valores, new double?[] { 50, 50, 50 }));

        var puntos = cut.Find("polyline").GetAttribute("points")!.Split(' ');
        puntos.Select(x => x.Split(',')[1]).Distinct().Count().ShouldBe(1);
        puntos.ShouldAllBe(x => !x.Contains("NaN") && !x.Contains("Infinity"));
    }

    [Fact]
    public void Solo_huecos_es_como_no_tener_datos()
    {
        Render<MiniGrafica>(p => p.Add(c => c.Valores, new double?[] { null, null })).FindAll("svg").ShouldBeEmpty();
    }
}

public class GraficaDeLatenciaTests : BunitContext
{
    private static PuntoDeLatencia[] Puntos(params PuntoDeLatencia[] puntos) => puntos;

    [Fact]
    public void Sin_puntos_dice_que_no_hay_datos_en_lugar_de_dibujar_ejes_vacios()
    {
        var cut = Render<GraficaDeLatencia>(p => p.Add(c => c.Puntos, []));

        cut.FindAll("svg.svg-grafica").ShouldBeEmpty();
        cut.Find(".sin-datos").TextContent.ShouldContain("Todavía no hay datos");
    }

    [Fact]
    public void Dibuja_la_mediana_y_el_percentil_95_con_trazos_distintos()
    {
        var cut = Render<GraficaDeLatencia>(p => p.Add(c => c.Puntos, Puntos(Datos.Punto(3), Datos.Punto(2, 120, 400), Datos.Punto(1, 90, 250))));

        cut.FindAll("polyline.linea-p50").Count.ShouldBe(1);
        cut.FindAll("polyline.linea-p95").Count.ShouldBe(1);
        cut.Markup.ShouldContain("linea-p95");
    }

    [Fact]
    public void Esta_etiquetada_para_lectores_de_pantalla_con_un_resumen_de_lo_que_muestra()
    {
        var cut = Render<GraficaDeLatencia>(p => p.Add(c => c.Puntos, Puntos(Datos.Punto(2, 100, 300, fallidas: 2), Datos.Punto(1, 200, 600))));

        var svg = cut.Find("svg.svg-grafica");
        svg.GetAttribute("role").ShouldBe("img");
        var ids = svg.GetAttribute("aria-labelledby")!.Split(' ');
        ids.Length.ShouldBe(2);
        cut.Find($"#{ids[0]}").TextContent.ShouldBe("Latencia por hora");
        var descripcion = cut.Find($"#{ids[1]}").TextContent;
        descripcion.ShouldContain("Mediana entre 100 ms y 200 ms");
        descripcion.ShouldContain("percentil 95 entre 300 ms y 600 ms");
        descripcion.ShouldContain("1 horas con fallos de 2");
    }

    [Fact]
    public void Cada_grafica_tiene_identificadores_propios_para_que_no_choquen_dos_en_la_misma_pagina()
    {
        var uno = Render<GraficaDeLatencia>(p => p.Add(c => c.Puntos, Puntos(Datos.Punto(1)))).Find("svg.svg-grafica").GetAttribute("aria-labelledby");
        var otro = Render<GraficaDeLatencia>(p => p.Add(c => c.Puntos, Puntos(Datos.Punto(1)))).Find("svg.svg-grafica").GetAttribute("aria-labelledby");

        uno.ShouldNotBe(otro);
    }

    [Fact]
    public void Las_horas_con_fallos_llevan_una_cruz_ademas_de_color_y_con_su_explicacion()
    {
        var cut = Render<GraficaDeLatencia>(p => p.Add(c => c.Puntos, Puntos(Datos.Punto(3), Datos.Punto(2, fallidas: 3), Datos.Punto(1))));

        var marcas = cut.FindAll("svg.svg-grafica path.marca-fallo");
        marcas.Count.ShouldBe(1);
        marcas[0].QuerySelector("title")!.TextContent.ShouldContain("3 fallos");
        cut.Find("figcaption").TextContent.ShouldContain("Hora con fallos");
    }

    [Fact]
    public void Un_hueco_en_una_serie_corta_la_linea_en_dos()
    {
        var cut = Render<GraficaDeLatencia>(p => p.Add(c => c.Puntos, Puntos(Datos.Punto(4), Datos.Punto(3), Datos.Punto(2, p50: null, p95: null), Datos.Punto(1), Datos.Punto(0))));

        cut.FindAll("polyline.linea-p50").Count.ShouldBe(2);
        cut.FindAll("polyline.linea-p95").Count.ShouldBe(2);
    }

    [Fact]
    public void Un_solo_punto_aun_se_dibuja_y_no_rompe_las_escalas()
    {
        var cut = Render<GraficaDeLatencia>(p => p.Add(c => c.Puntos, Puntos(Datos.Punto(1, 80, 200))));

        cut.FindAll("polyline.linea-p50").Count.ShouldBe(1);
        cut.Markup.ShouldNotContain("NaN");
        cut.Markup.ShouldNotContain("Infinity");
    }

    [Theory]
    [InlineData(80, "50", "100")]
    [InlineData(300, "250", "500")]
    [InlineData(3000, "2500", "5000")]
    public void El_eje_vertical_usa_un_maximo_redondo_y_legible(double p95, string _, string maximoEsperado)
    {
        var cut = Render<GraficaDeLatencia>(p => p.Add(c => c.Puntos, Puntos(Datos.Punto(2, 10, p95 - 20), Datos.Punto(1, 10, p95))));

        var etiquetas = cut.FindAll("text.etiqueta").Select(t => t.TextContent).ToList();
        etiquetas.ShouldContain(Web.Servicios.Formato.Latencia(double.Parse(maximoEsperado, System.Globalization.CultureInfo.InvariantCulture)));
        etiquetas.ShouldContain("0 ms");
    }

    [Fact]
    public void Los_mismos_datos_estan_disponibles_en_una_tabla_para_quien_no_puede_ver_la_grafica()
    {
        var cut = Render<GraficaDeLatencia>(p => p.Add(c => c.Puntos, Puntos(Datos.Punto(2, 100, 300, fallidas: 1), Datos.Punto(1, 200, 600))));

        var filas = cut.FindAll("details table tbody tr");
        filas.Count.ShouldBe(2);
        filas[0].TextContent.ShouldContain("100 ms");
        filas[0].TextContent.ShouldContain("300 ms");
        cut.Find("details summary").TextContent.ShouldContain("tabla");
        cut.FindAll("details th[scope=col]").Count.ShouldBe(5);
    }

    [Fact]
    public void El_texto_de_los_ejes_se_escapa()
    {
        // Las etiquetas salen de datos de la API: nada de lo que lleguen debe poder inyectar marcado.
        var cut = Render<GraficaDeLatencia>(p => p.Add(c => c.Puntos, Puntos(Datos.Punto(1))));

        cut.FindAll("svg.svg-grafica script").ShouldBeEmpty();
    }
}

public class BarrasDeDisponibilidadTests : BunitContext
{
    private static readonly string[] TextosDeLeyenda = ["Sin incidentes", "Lento", "Con caída", "Mantenimiento", "Sin datos"];

    private static IReadOnlyList<BarraDiaria> Semana() =>
    [
        Datos.Barra(6, EstadoMonitor.Operativo),
        Datos.Barra(5, EstadoMonitor.Operativo),
        Datos.Barra(4, EstadoMonitor.Caido, 97.5m, 2160),
        Datos.Barra(3, null, null),
        Datos.Barra(2, EstadoMonitor.Degradado, 99.9m),
        Datos.Barra(1, EstadoMonitor.Mantenimiento, null),
        Datos.Barra(0, EstadoMonitor.Operativo),
    ];

    [Fact]
    public void Dibuja_una_barra_por_dia_con_la_clase_de_su_peor_estado()
    {
        var cut = Render<BarrasDeDisponibilidad>(p => p.Add(c => c.Barras, Semana()));

        var barras = cut.FindAll("svg.svg-barras rect[height='36']");
        barras.Count.ShouldBe(7);
        barras.Select(b => b.ClassList.Single(c => c != "svg")).ShouldBe(["ok", "ok", "caido", "desconocido", "aviso", "mantenimiento", "ok"]);
    }

    [Fact]
    public void Los_dias_con_caida_llevan_trama_ademas_de_color()
    {
        var cut = Render<BarrasDeDisponibilidad>(p => p.Add(c => c.Barras, Semana()));

        var caida = cut.FindAll("svg.svg-barras rect.caido").Single();
        caida.GetAttribute("fill").ShouldStartWith("url(#hachura-");
        cut.FindAll("svg.svg-barras rect.ok").ShouldAllBe(b => b.GetAttribute("fill") == null);
        cut.FindAll("svg.svg-barras pattern").Count.ShouldBe(1);
    }

    [Fact]
    public void Cada_barra_explica_su_dia_con_texto()
    {
        var cut = Render<BarrasDeDisponibilidad>(p => p.Add(c => c.Barras, Semana()));

        var titulos = cut.FindAll("svg.svg-barras rect title").Select(t => t.TextContent).ToList();
        titulos[2].ShouldContain("97,5 %");
        titulos[2].ShouldContain("caído 36 min 0 s");
        titulos[3].ShouldContain("sin datos");
        titulos[0].ShouldContain("100 %");
    }

    [Fact]
    public void Esta_descrita_para_lectores_de_pantalla_con_el_resumen_de_dias()
    {
        var cut = Render<BarrasDeDisponibilidad>(p => p.Add(c => c.Barras, Semana()));

        var svg = cut.Find("svg.svg-barras");
        svg.GetAttribute("role").ShouldBe("img");
        svg.GetAttribute("aria-label").ShouldBe("Disponibilidad diaria de los últimos 7 días: 5 días sin caídas, 1 con caídas y 1 sin datos.");
    }

    [Fact]
    public void La_leyenda_nombra_cada_estado_con_texto()
    {
        var cut = Render<BarrasDeDisponibilidad>(p => p.Add(c => c.Barras, Semana()));

        var leyenda = cut.Find(".leyenda").TextContent;
        TextosDeLeyenda.ShouldAllBe(t => leyenda.Contains(t, StringComparison.Ordinal));
    }

    [Fact]
    public void La_leyenda_se_puede_quitar_cuando_se_enseña_una_sola_vez_para_varias_barras()
    {
        var cut = Render<BarrasDeDisponibilidad>(p => p.Add(c => c.Barras, Semana()).Add(c => c.MostrarLeyenda, false));

        cut.FindAll(".leyenda").ShouldBeEmpty();
        cut.FindAll("svg.svg-barras").Count.ShouldBe(1);
    }

    [Fact]
    public void Dos_grupos_de_barras_en_la_misma_pagina_no_comparten_identificador_de_trama()
    {
        using var otro = new BunitContext();
        var a = Render<BarrasDeDisponibilidad>(p => p.Add(c => c.Barras, Semana())).Find("pattern").Id;
        var b = otro.Render<BarrasDeDisponibilidad>(p => p.Add(c => c.Barras, Semana())).Find("pattern").Id;

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Sin_barras_no_rompe()
    {
        var cut = Render<BarrasDeDisponibilidad>(p => p.Add(c => c.Barras, []));

        cut.FindAll("svg.svg-barras rect[height='36']").ShouldBeEmpty();
        cut.Find("svg.svg-barras").GetAttribute("aria-label")!.ShouldContain("0 días");
    }

    [Fact]
    public void Noventa_dias_caben_en_una_sola_fila()
    {
        var barras = Enumerable.Range(0, 90).Select(i => Datos.Barra(89 - i, EstadoMonitor.Operativo)).ToList();

        var cut = Render<BarrasDeDisponibilidad>(p => p.Add(c => c.Barras, barras));

        cut.FindAll("svg.svg-barras rect[height='36']").Count.ShouldBe(90);
        cut.Find("svg.svg-barras").GetAttribute("viewBox").ShouldBe("0 0 720 36");
        cut.Find(".extremos").TextContent.ShouldContain("hace 90 días");
    }
}
