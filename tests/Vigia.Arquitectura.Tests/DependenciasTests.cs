using NetArchTest.Rules;

using Shouldly;

using ResultadoRegla = NetArchTest.Rules.TestResult;

namespace Vigia.Arquitectura.Tests;

/// <summary>
/// Las dependencias entre proyectos van siempre hacia dentro: el dominio no conoce nada, las
/// comprobaciones de red y los datos solo conocen el dominio, y la API y el worker no se conocen
/// entre sí (se comunican por la base de datos y por mensajes).
/// </summary>
public class DependenciasTests
{
    private static readonly System.Reflection.Assembly Dominio = typeof(Vigia.Dominio.ReferenciaEnsamblado).Assembly;
    private static readonly System.Reflection.Assembly Comprobaciones = typeof(Vigia.Comprobaciones.ReferenciaEnsamblado).Assembly;
    private static readonly System.Reflection.Assembly Datos = typeof(Vigia.Datos.ReferenciaEnsamblado).Assembly;
    private static readonly System.Reflection.Assembly Api = typeof(Vigia.Api.ReferenciaApi).Assembly;
    private static readonly System.Reflection.Assembly Web = typeof(Vigia.Web.ReferenciaWeb).Assembly;
    private static readonly System.Reflection.Assembly Contratos = typeof(Vigia.Contratos.MonitorDto).Assembly;
    private static readonly System.Reflection.Assembly Worker = typeof(Vigia.Worker.ReferenciaWorker).Assembly;

    private static string Explicar(ResultadoRegla resultado) =>
        $"Tipos que rompen la regla: {string.Join(", ", resultado.FailingTypeNames ?? [])}";

    [Fact]
    public void El_dominio_no_depende_de_ninguna_otra_capa_ni_de_frameworks()
    {
        var resultado = Types.InAssembly(Dominio)
            .ShouldNot().HaveDependencyOnAny("Vigia.Comprobaciones", "Vigia.Datos", "Vigia.Api", "Vigia.Worker", "Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore")
            .GetResult();

        resultado.IsSuccessful.ShouldBeTrue(Explicar(resultado));
    }

    [Fact]
    public void Las_comprobaciones_no_dependen_de_los_datos_ni_de_la_web()
    {
        var resultado = Types.InAssembly(Comprobaciones)
            .ShouldNot().HaveDependencyOnAny("Vigia.Datos", "Vigia.Api", "Vigia.Worker", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore")
            .GetResult();

        resultado.IsSuccessful.ShouldBeTrue(Explicar(resultado));
    }

    [Fact]
    public void Los_datos_no_dependen_de_las_comprobaciones_ni_de_la_web()
    {
        var resultado = Types.InAssembly(Datos)
            .ShouldNot().HaveDependencyOnAny("Vigia.Comprobaciones", "Vigia.Api", "Vigia.Worker", "Microsoft.AspNetCore")
            .GetResult();

        resultado.IsSuccessful.ShouldBeTrue(Explicar(resultado));
    }

    [Fact]
    public void La_api_y_el_worker_no_se_conocen_entre_si()
    {
        Types.InAssembly(Api).ShouldNot().HaveDependencyOn("Vigia.Worker").GetResult().IsSuccessful.ShouldBeTrue();
        Types.InAssembly(Worker).ShouldNot().HaveDependencyOn("Vigia.Api").GetResult().IsSuccessful.ShouldBeTrue();
    }

    [Fact]
    public void Los_contratos_solo_conocen_el_dominio()
    {
        var resultado = Types.InAssembly(Contratos)
            .ShouldNot().HaveDependencyOnAny("Vigia.Datos", "Vigia.Comprobaciones", "Vigia.Api", "Vigia.Worker", "Vigia.Web", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore")
            .GetResult();

        resultado.IsSuccessful.ShouldBeTrue(Explicar(resultado));
    }

    [Fact]
    public void La_web_habla_con_la_api_por_http_y_no_conoce_ni_los_datos_ni_el_worker_ni_la_api()
    {
        // La web no toca la base de datos ni las comprobaciones: todo lo que sabe viene de la API, a través de los contratos.
        var resultado = Types.InAssembly(Web)
            .ShouldNot().HaveDependencyOnAny("Vigia.Datos", "Vigia.Comprobaciones", "Vigia.Api", "Vigia.Worker", "Microsoft.EntityFrameworkCore", "Npgsql")
            .GetResult();

        resultado.IsSuccessful.ShouldBeTrue(Explicar(resultado));
    }

    [Fact]
    public void Ni_la_api_ni_el_worker_conocen_la_web()
    {
        Types.InAssembly(Api).ShouldNot().HaveDependencyOn("Vigia.Web").GetResult().IsSuccessful.ShouldBeTrue();
        Types.InAssembly(Worker).ShouldNot().HaveDependencyOn("Vigia.Web").GetResult().IsSuccessful.ShouldBeTrue();
    }
}
