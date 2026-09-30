using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vigia.Datos.Persistencia;

/// <summary>
/// La usa la herramienta <c>dotnet ef</c> al crear migraciones. La cadena de conexión no se usa para
/// conectar (crear una migración no toca la base de datos), solo indica que el proveedor es PostgreSQL.
/// </summary>
internal sealed class FabricaVigiaDbContext : IDesignTimeDbContextFactory<VigiaDbContext>
{
    public VigiaDbContext CreateDbContext(string[] args)
    {
        var opciones = new DbContextOptionsBuilder<VigiaDbContext>().UseNpgsql("Host=localhost;Database=vigia_diseno");
        OpcionesVigia.Configurar(opciones);

        return new VigiaDbContext(opciones.Options);
    }
}
