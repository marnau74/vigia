using Microsoft.EntityFrameworkCore;

using Vigia.Datos.Persistencia;
using Vigia.Dominio.Monitores;
using Vigia.Tests.Comunes;

using MonitorDeDominio = Vigia.Dominio.Monitores.Monitor;

namespace Vigia.Datos.Tests;

/// <summary>Base de los tests que necesitan PostgreSQL: cada test recibe una base de datos propia, ya migrada.</summary>
public abstract class BaseDeDatosTest : IAsyncLifetime
{
    private static readonly SemaphoreSlim Arranque = new(1, 1);
    private static ServidorPostgres? _servidor;

    protected static readonly DateTimeOffset Ahora = new(2026, 10, 15, 10, 30, 0, TimeSpan.Zero);

    protected static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    protected string CadenaConexion { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        // Un solo contenedor para todos los tests del proyecto (arrancar uno tarda unos segundos).
        await Arranque.WaitAsync();

        try
        {
            _servidor ??= new ServidorPostgres();
        }
        finally
        {
            Arranque.Release();
        }

        CadenaConexion = await _servidor.CrearBaseDeDatosAsync();
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    protected VigiaDbContext NuevoContexto() => ServidorPostgres.CrearContexto(CadenaConexion);

    /// <summary>Guarda un monitor HTTP de prueba (creado un mes antes de «ahora», salvo que se indique otra cosa).</summary>
    protected async Task<MonitorDeDominio> GuardarMonitorAsync(string nombre = "Mi web", DateTimeOffset? creado = null, ConfiguracionMonitor? configuracion = null)
    {
        var monitor = MonitorDeDominio.Crear(
            nombre,
            configuracion ?? new ConfiguracionHttp(new Uri("https://ejemplo.com/")),
            TimeSpan.FromSeconds(60),
            3,
            TimeSpan.FromSeconds(2),
            null,
            creado ?? Ahora.AddMonths(-1)).Valor;

        await using var db = NuevoContexto();
        await new RepositorioMonitores(db).AgregarAsync(monitor, Cancelacion);

        return monitor;
    }

    protected async Task<T> ConsultarAsync<T>(string sql)
    {
        await using var db = NuevoContexto();

        return (await db.Database.SqlQueryRaw<T>(sql).ToListAsync(Cancelacion)).Single();
    }
}
