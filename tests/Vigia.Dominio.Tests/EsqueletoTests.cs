using Shouldly;

namespace Vigia.Dominio.Tests;

public class EsqueletoTests
{
    [Fact]
    public void El_dominio_se_puede_referenciar()
    {
        typeof(ReferenciaEnsamblado).Assembly.GetName().Name.ShouldBe("Vigia.Dominio");
    }
}
