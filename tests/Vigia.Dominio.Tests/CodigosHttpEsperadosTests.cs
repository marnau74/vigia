using Shouldly;

using Vigia.Dominio.Monitores;

namespace Vigia.Dominio.Tests;

public class CodigosHttpEsperadosTests
{
    [Theory]
    [InlineData("200", new[] { 200 }, new[] { 199, 201, 404 })]
    [InlineData("200-299", new[] { 200, 250, 299 }, new[] { 199, 300 })]
    [InlineData("200-299,301,404", new[] { 200, 301, 404 }, new[] { 302, 403, 500 })]
    [InlineData(" 200 - 204 , 500 ", new[] { 200, 204, 500 }, new[] { 205, 501 })]
    public void Los_codigos_y_rangos_se_interpretan(string texto, int[] incluidos, int[] excluidos)
    {
        CodigosHttpEsperados.TryParse(texto, out var codigos).ShouldBeTrue();

        foreach (var codigo in incluidos)
        {
            codigos.Contiene(codigo).ShouldBeTrue($"{codigo} debería estar en «{texto}»");
        }

        foreach (var codigo in excluidos)
        {
            codigos.Contiene(codigo).ShouldBeFalse($"{codigo} no debería estar en «{texto}»");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("200-")]
    [InlineData("-200")]
    [InlineData("299-200")]
    [InlineData("99")]
    [InlineData("600")]
    [InlineData("200,,300")]
    [InlineData("200-300-400")]
    [InlineData("+200")]
    [InlineData("2e2")]
    public void Un_texto_que_no_es_valido_se_rechaza(string? texto)
    {
        CodigosHttpEsperados.TryParse(texto, out _).ShouldBeFalse();
    }

    [Fact]
    public void El_texto_se_normaliza()
    {
        CodigosHttpEsperados.TryParse(" 200 - 204 , 500 ", out var codigos).ShouldBeTrue();

        codigos.Texto.ShouldBe("200-204,500");
    }

    [Fact]
    public void Por_defecto_todo_lo_que_no_es_un_error_es_correcto()
    {
        CodigosHttpEsperados.PorDefecto.Contiene(200).ShouldBeTrue();
        CodigosHttpEsperados.PorDefecto.Contiene(301).ShouldBeTrue();
        CodigosHttpEsperados.PorDefecto.Contiene(399).ShouldBeTrue();
        CodigosHttpEsperados.PorDefecto.Contiene(400).ShouldBeFalse();
        CodigosHttpEsperados.PorDefecto.Contiene(503).ShouldBeFalse();
    }
}

public class ConfiguracionMonitorTests
{
    [Fact]
    public void Por_defecto_la_red_privada_esta_prohibida_y_el_tiempo_maximo_es_de_diez_segundos()
    {
        ConfiguracionMonitor[] configuraciones =
        [
            new ConfiguracionHttp(new Uri("https://ejemplo.com")),
            new ConfiguracionTls("ejemplo.com"),
            new ConfiguracionDns("ejemplo.com", TipoRegistroDns.A),
            new ConfiguracionTcp("ejemplo.com", 22),
            new ConfiguracionIcmp("ejemplo.com"),
        ];

        configuraciones.ShouldAllBe(c => !c.PermitirRedPrivada && c.TiempoMaximo == TimeSpan.FromSeconds(10));
        configuraciones.Select(c => c.Tipo).ShouldBe(Enum.GetValues<TipoMonitor>(), ignoreOrder: true);
    }
}
