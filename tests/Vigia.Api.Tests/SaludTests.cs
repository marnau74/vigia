using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Vigia.Api.Tests;

/// <summary>La API y el worker arrancan y responden a las comprobaciones de salud.</summary>
public class SaludTests
{
    [Fact]
    public async Task La_api_responde_a_health_y_alive()
    {
        using var fabrica = new WebApplicationFactory<Vigia.Api.ReferenciaApi>();
        using var cliente = fabrica.CreateClient();

        using var salud = await cliente.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        using var viva = await cliente.GetAsync(new Uri("/alive", UriKind.Relative), TestContext.Current.CancellationToken);

        salud.StatusCode.ShouldBe(HttpStatusCode.OK);
        viva.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task El_worker_responde_a_health_y_alive()
    {
        using var fabrica = new WebApplicationFactory<Vigia.Worker.ReferenciaWorker>();
        using var cliente = fabrica.CreateClient();

        using var salud = await cliente.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        using var viva = await cliente.GetAsync(new Uri("/alive", UriKind.Relative), TestContext.Current.CancellationToken);

        salud.StatusCode.ShouldBe(HttpStatusCode.OK);
        viva.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
