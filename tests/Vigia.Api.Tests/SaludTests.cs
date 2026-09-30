using System.Net;

using Microsoft.AspNetCore.Hosting;

using Shouldly;

namespace Vigia.Api.Tests;

/// <summary>La API arranca contra una base de datos real y responde a las comprobaciones de salud, sin necesidad de sesión.</summary>
public class SaludTests
{
    [Fact]
    public async Task La_api_responde_a_health_y_alive_sin_sesion()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync();
        using var cliente = fabrica.CreateClient();

        using var salud = await cliente.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        using var viva = await cliente.GetAsync(new Uri("/alive", UriKind.Relative), TestContext.Current.CancellationToken);

        salud.StatusCode.ShouldBe(HttpStatusCode.OK);
        viva.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sin_clave_de_firma_la_api_no_arranca_fuera_de_desarrollo()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync(clave: null);
        using var produccion = fabrica.WithWebHostBuilder(constructor => constructor.UseEnvironment("Production"));

        await Should.ThrowAsync<Exception>(async () =>
        {
            using var cliente = produccion.CreateClient();
            await cliente.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task Con_una_clave_demasiado_corta_la_api_no_arranca()
    {
        await using var fabrica = await FabricaDeApi.CrearAsync(clave: "corta");

        await Should.ThrowAsync<Exception>(async () =>
        {
            using var cliente = fabrica.CreateClient();
            await cliente.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        });
    }
}
