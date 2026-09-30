using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Shouldly;

using Vigia.Datos.Persistencia;
using Vigia.Tests.Comunes;

namespace Vigia.Worker.Tests;

/// <summary>El worker completo, tal como arranca de verdad, contra una PostgreSQL real.</summary>
public class ArranqueTests
{
    [Fact]
    public async Task El_worker_migra_crea_las_particiones_y_responde_a_health_y_alive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var servidor = new ServidorPostgres();
        var cadena = await servidor.CrearBaseDeDatosSinMigrarAsync();

        using var fabrica = new WebApplicationFactory<Vigia.Worker.ReferenciaWorker>()
            .WithWebHostBuilder(constructor => constructor.UseSetting("ConnectionStrings:vigia", cadena));
        using var cliente = fabrica.CreateClient();

        using var salud = await cliente.GetAsync(new Uri("/health", UriKind.Relative), ct);
        using var viva = await cliente.GetAsync(new Uri("/alive", UriKind.Relative), ct);

        salud.StatusCode.ShouldBe(HttpStatusCode.OK);
        viva.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var db = ServidorPostgres.CrearContexto(cadena);
        (await db.Database.GetAppliedMigrationsAsync(ct)).ShouldNotBeEmpty("el arranque aplica las migraciones");
        (await new Particiones(db).ListarAsync(ct)).Count.ShouldBeGreaterThanOrEqualTo(Particiones.MesesPorAdelantado + 1);
    }
}
