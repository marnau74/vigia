// El worker hace las comprobaciones, aunque la API se reinicie o se despliegue. Es un servicio web
// mínimo solo para exponer /health y /alive a Aspire, a Docker y al proxy.

using OpenTelemetry.Metrics;

using Vigia.Comprobaciones;
using Vigia.Datos.Persistencia;
using Vigia.Worker.Mantenimiento;
using Vigia.Worker.Planificacion;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<OpcionesPlanificador>(builder.Configuration.GetSection(OpcionesPlanificador.Seccion));
builder.AddNpgsqlDbContext<VigiaDbContext>("vigia", configureDbContextOptions: opciones => OpcionesVigia.Configurar(opciones));

builder.Services.AddComprobaciones();
builder.Services.AddSingleton<ColaDeVencimientos>();
builder.Services.AddSingleton<MetricasVigia>();
builder.Services.AddSingleton<IManejadorDeEventos, ManejadorDeEventosVacio>();
builder.Services.AddSingleton<EjecutorDeMonitor>();
builder.Services.AddSingleton<Planificador>();

// El orden importa: los servicios alojados arrancan uno tras otro, y la base de datos
// tiene que estar migrada y con particiones antes de que el planificador empiece a escribir.
builder.Services.AddHostedService<PreparacionDeBaseDeDatos>();
builder.Services.AddHostedService(proveedor => proveedor.GetRequiredService<Planificador>());
builder.Services.AddHostedService<TareasDeDatos>();
builder.Services.AddHostedService(proveedor => new EscuchaDeCambios(
    builder.Configuration.GetConnectionString("vigia") ?? throw new InvalidOperationException("Falta la cadena de conexión «vigia»."),
    proveedor.GetRequiredService<Planificador>(),
    proveedor.GetRequiredService<TimeProvider>(),
    proveedor.GetRequiredService<ILogger<EscuchaDeCambios>>()));

builder.Services.AddOpenTelemetry().WithMetrics(metricas => metricas.AddMeter(MetricasVigia.NombreMedidor));

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();

/// <summary>Visible para los tests de integración (WebApplicationFactory).</summary>
public partial class Program;
