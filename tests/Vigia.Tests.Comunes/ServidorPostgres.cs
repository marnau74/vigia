using Microsoft.EntityFrameworkCore;

using Npgsql;

using Testcontainers.PostgreSql;

using Vigia.Datos.Persistencia;

namespace Vigia.Tests.Comunes;

/// <summary>
/// Un PostgreSQL real en un contenedor de Docker, compartido por todos los tests de un
/// ensamblado (arrancar un contenedor tarda unos segundos, así que se hace una sola vez).
/// Cada test pide su propia base de datos, ya migrada: quedan completamente aislados entre sí
/// aunque se ejecuten a la vez.
/// </summary>
/// <remarks>
/// La versión coincide con la del entorno local (PostgreSQL 17). Probar contra una base de datos en
/// memoria o contra SQLite no serviría: el particionado por mes de los resultados y los índices
/// parciales son cosas de PostgreSQL.
/// </remarks>
public sealed class ServidorPostgres : IAsyncDisposable
{
    private readonly PostgreSqlContainer _contenedor = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=300")
        .Build();
    private readonly SemaphoreSlim _arranque = new(1, 1);
    private bool _iniciado;

    /// <summary>Crea una base de datos nueva con todas las migraciones aplicadas y devuelve su cadena de conexión.</summary>
    public async Task<string> CrearBaseDeDatosAsync()
    {
        var cadena = await CrearBaseDeDatosSinMigrarAsync();

        await using var contexto = CrearContexto(cadena);
        await contexto.Database.MigrateAsync();

        return cadena;
    }

    /// <summary>Crea una base de datos vacía (sin tablas) y devuelve su cadena de conexión.</summary>
    public async Task<string> CrearBaseDeDatosSinMigrarAsync()
    {
        await IniciarAsync();

        var nombre = $"vigia_{Guid.NewGuid():N}";
        var administrador = _contenedor.GetConnectionString();

        await using (var conexion = new NpgsqlConnection(administrador))
        {
            await conexion.OpenAsync();
            await using var crear = new NpgsqlCommand($"CREATE DATABASE \"{nombre}\"", conexion);
            await crear.ExecuteNonQueryAsync();
        }

        // Sin pool de conexiones: cada test tiene su propia base de datos y, con pool, cada una
        // dejaría conexiones abiertas hasta agotar las del servidor.
        return new NpgsqlConnectionStringBuilder(administrador) { Database = nombre, Pooling = false }.ConnectionString;
    }

    /// <summary>Un contexto nuevo sobre la base de datos indicada (cada uno es una unidad de trabajo independiente).</summary>
    public static VigiaDbContext CrearContexto(string cadenaConexion)
    {
        var opciones = new DbContextOptionsBuilder<VigiaDbContext>().UseNpgsql(cadenaConexion);
        OpcionesVigia.Configurar(opciones);

        return new VigiaDbContext(opciones.Options);
    }

    public async ValueTask DisposeAsync()
    {
        _arranque.Dispose();
        await _contenedor.DisposeAsync();
    }

    private async Task IniciarAsync()
    {
        await _arranque.WaitAsync();

        try
        {
            if (!_iniciado)
            {
                await _contenedor.StartAsync();
                _iniciado = true;
            }
        }
        finally
        {
            _arranque.Release();
        }
    }
}
