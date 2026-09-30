using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Shouldly;

using Vigia.Datos.Persistencia;
using Vigia.Dominio.Monitores;

using MonitorDeDominio = Vigia.Dominio.Monitores.Monitor;

namespace Vigia.Api.Tests;

/// <summary>Un cliente con sesión iniciada contra una API nueva y una base de datos propia, más utilidades para preparar datos.</summary>
public abstract class BaseApiTest : IAsyncLifetime
{
    protected static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    protected FabricaDeApi Fabrica { get; private set; } = null!;

    protected HttpClient Cliente { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Fabrica = await FabricaDeApi.CrearAsync();
        await Fabrica.PrepararAsync();
        Cliente = await Fabrica.ClienteAutenticadoAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Cliente.Dispose();
        await Fabrica.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected static object Http(string url = "https://ejemplo.com/") => new { tipo = "Http", url };

    protected static object CuerpoDeMonitor(string nombre = "Mi web", object? configuracion = null, int intervalo = 60, int fallos = 3, int? umbralMs = null, Guid? grupoId = null) =>
        new { nombre, configuracion = configuracion ?? Http(), intervaloSegundos = intervalo, fallosParaIncidente = fallos, umbralLentoMs = umbralMs, grupoId };

    protected static async Task<JsonElement> LeerAsync(HttpResponseMessage respuesta) => await respuesta.Content.ReadFromJsonAsync<JsonElement>(FabricaDeApi.Json, Cancelacion);

    protected async Task<Guid> CrearMonitorAsync(string nombre = "Mi web", object? configuracion = null, int intervalo = 60, Guid? grupoId = null)
    {
        using var respuesta = await Cliente.PostAsJsonAsync("/api/monitores", CuerpoDeMonitor(nombre, configuracion, intervalo, grupoId: grupoId), Cancelacion);
        respuesta.EnsureSuccessStatusCode();

        return (await LeerAsync(respuesta)).GetProperty("id").GetGuid();
    }

    protected async Task<Guid> CrearGrupoAsync(string slug = "produccion", string nombre = "Producción", bool publico = true)
    {
        using var respuesta = await Cliente.PostAsJsonAsync("/api/grupos", new { slug, nombre, publico }, Cancelacion);
        respuesta.EnsureSuccessStatusCode();

        return (await LeerAsync(respuesta)).GetProperty("id").GetGuid();
    }

    /// <summary>Escribe directamente en la base de datos (lo que haría el worker) y devuelve el contexto para seguir preparando.</summary>
    protected async Task<MonitorDeDominio> MonitorEnBaseDeDatosAsync(string nombre = "Servicio", Guid? grupoId = null, bool activo = true)
    {
        var monitor = MonitorDeDominio.Crear(nombre, new ConfiguracionHttp(new Uri("https://interno.ejemplo/secreto")), TimeSpan.FromSeconds(60), 3, null, grupoId, FabricaDeApi.Inicio.AddDays(-100)).Valor;

        if (!activo)
        {
            monitor.Pausar();
        }

        await using var db = Fabrica.NuevoContexto();
        db.Monitores.Add(monitor);
        await db.SaveChangesAsync(Cancelacion);

        return monitor;
    }

    protected Task<int> ContarAsync(string tabla) => ConsultarAsync<int>($"SELECT count(*)::int AS \"Value\" FROM {tabla}");

    protected async Task<T> ConsultarAsync<T>(string sql)
    {
        await using var db = Fabrica.NuevoContexto();

        return (await db.Database.SqlQueryRaw<T>(sql).ToListAsync(Cancelacion)).Single();
    }

    /// <summary>Escucha un canal de LISTEN/NOTIFY y anota lo que llega.</summary>
    protected async Task<ObservadorDeNotificaciones> ObservarAsync(string canal)
    {
        var observador = new ObservadorDeNotificaciones(Fabrica.Cadena);
        await observador.IniciarAsync(canal, Cancelacion);

        return observador;
    }
}

public sealed class ObservadorDeNotificaciones(string cadena) : IAsyncDisposable
{
    private readonly NpgsqlConnection _conexion = new(cadena);

    public ConcurrentQueue<string> Cargas { get; } = new();

    public async Task IniciarAsync(string canal, CancellationToken cancellationToken)
    {
        await _conexion.OpenAsync(cancellationToken);
        _conexion.Notification += (_, evento) => Cargas.Enqueue(evento.Payload);

#pragma warning disable CA2100 // Constante de los tests.
        await using var comando = new NpgsqlCommand($"LISTEN {canal}", _conexion);
#pragma warning restore CA2100
        await comando.ExecuteNonQueryAsync(cancellationToken);

        // Las notificaciones solo llegan mientras se espera en la conexión.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        await _conexion.WaitAsync(cancellationToken);
                    }
                }
                catch (Exception)
                {
                    // Se cierra al terminar el test.
                }
            },
            CancellationToken.None);
    }

    /// <summary>Espera a que lleguen al menos <paramref name="cantidad"/> avisos.</summary>
    public async Task EsperarAsync(int cantidad = 1)
    {
        var limite = DateTime.UtcNow.AddSeconds(10);

        while (Cargas.Count < cantidad)
        {
            if (DateTime.UtcNow > limite)
            {
                throw new TimeoutException($"Se esperaban {cantidad} avisos y llegaron {Cargas.Count}.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Comprueba que no llega nada durante un rato.</summary>
    public async Task NoLlegaNadaAsync()
    {
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Cargas.Count.ShouldBe(0);
    }

    public async ValueTask DisposeAsync() => await _conexion.DisposeAsync();
}
