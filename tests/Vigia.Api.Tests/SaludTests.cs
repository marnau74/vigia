using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;

using Shouldly;

namespace Vigia.Api.Tests;

/// <summary>La API arranca y responde a las comprobaciones de salud (las del worker, que necesita base de datos, están en sus tests).</summary>
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
}
