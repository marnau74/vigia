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
}
