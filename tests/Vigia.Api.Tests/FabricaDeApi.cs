using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Vigia.Api.Seguridad;
using Vigia.Datos.Persistencia;
using Vigia.Tests.Comunes;

namespace Vigia.Api.Tests;

/// <summary>Un PostgreSQL para todos los tests del ensamblado, con una base de datos nueva para cada uno.</summary>
public static class ServidorCompartido
{
    private static readonly SemaphoreSlim Arranque = new(1, 1);
    private static ServidorPostgres? _servidor;

    public static async Task<string> NuevaBaseDeDatosAsync()
    {
        await Arranque.WaitAsync();

        try
        {
            _servidor ??= new ServidorPostgres();
        }
        finally
        {
            Arranque.Release();
        }

        return await _servidor.CrearBaseDeDatosAsync();
    }
}

/// <summary>La API completa, contra una base de datos propia y con el reloj en manos del test.</summary>
public sealed class FabricaDeApi : WebApplicationFactory<ReferenciaApi>
{
    public const string ContrasenaDePrueba = "una-contrasena-de-prueba-larga";
    public const string ClaveDePrueba = "clave-de-firma-de-pruebas-de-al-menos-32-caracteres";

    public static readonly DateTimeOffset Inicio = new(2026, 10, 15, 10, 30, 0, TimeSpan.Zero);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string? _hash;
    private readonly string? _clave;

    private FabricaDeApi(string cadena, string? hash, string? clave)
    {
        Cadena = cadena;
        _hash = hash;
        _clave = clave;
    }

    public string Cadena { get; }

    public FakeTimeProvider Reloj { get; } = new(Inicio);

    public static async Task<FabricaDeApi> CrearAsync(bool conAcceso = true, string? clave = ClaveDePrueba) =>
        new(await ServidorCompartido.NuevaBaseDeDatosAsync(), conAcceso ? Contrasenas.Hash(ContrasenaDePrueba) : null, clave);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("ConnectionStrings:vigia", Cadena);

        if (_hash is not null)
        {
            builder.UseSetting("Acceso:HashContrasena", _hash);
        }

        if (_clave is not null)
        {
            builder.UseSetting("Acceso:ClaveJwt", _clave);
        }

        builder.ConfigureTestServices(servicios =>
        {
            servicios.RemoveAll<TimeProvider>();
            servicios.AddSingleton<TimeProvider>(Reloj);
        });
    }

    public VigiaDbContext NuevoContexto() => ServidorPostgres.CrearContexto(Cadena);

    /// <summary>Un cliente ya con sesión iniciada.</summary>
    public async Task<HttpClient> ClienteAutenticadoAsync()
    {
        var cliente = CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());

        return cliente;
    }

    public async Task<string> TokenAsync()
    {
        using var cliente = CreateClient();
        using var respuesta = await cliente.PostAsJsonAsync("/api/acceso", new { contrasena = ContrasenaDePrueba }, TestContext.Current.CancellationToken);
        respuesta.EnsureSuccessStatusCode();

        return (await respuesta.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("token").GetString()!;
    }

    public async Task PrepararAsync()
    {
        // La base de datos ya está migrada; las particiones del mes simulado hay que crearlas (el worker lo hace en producción).
        await using var db = NuevoContexto();
        await new Particiones(db).AsegurarAsync(Inicio, TestContext.Current.CancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
