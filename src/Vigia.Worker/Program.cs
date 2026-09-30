// El worker hace las comprobaciones, aunque la API se reinicie o se despliegue. Es un servicio web
// mínimo solo para exponer /health y /alive a Aspire, a Docker y al proxy.

using Microsoft.Extensions.Options;

using OpenTelemetry.Metrics;

using Vigia.Comprobaciones;
using Vigia.Datos.Persistencia;
using Vigia.Worker;
using Vigia.Worker.Avisos;
using Vigia.Worker.Mantenimiento;
using Vigia.Worker.Planificacion;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.Configure<OpcionesPlanificador>(builder.Configuration.GetSection(OpcionesPlanificador.Seccion));
builder.AddNpgsqlDbContext<VigiaDbContext>("vigia", configureDbContextOptions: opciones => OpcionesVigia.Configurar(opciones));

builder.Services.Configure<OpcionesSmtp>(builder.Configuration.GetSection(OpcionesSmtp.Seccion));
builder.Services.Configure<OpcionesDeAvisos>(builder.Configuration.GetSection(OpcionesDeAvisos.Seccion));

builder.Services.AddComprobaciones();
builder.Services.AddSingleton<ColaDeVencimientos>();
builder.Services.AddSingleton<MetricasVigia>();
builder.Services.AddSingleton<IManejadorDeEventos, PublicadorDeComprobaciones>();
builder.Services.AddSingleton<DestinosDeAviso>();
builder.Services.AddSingleton<EjecutorDeMonitor>();

// Los avisos: un canal por medio, y un enviador que vacía la bandeja de salida.
builder.Services.AddSingleton<ICanalDeAviso, CanalCorreo>();

// Telegram tiene su propio cliente HTTP, sin los reintentos automáticos que el resto de clientes reciben de
// ServiceDefaults: repetir un POST por la red enviaría el mensaje dos veces.
builder.Services.AddSingleton<ICanalDeAviso>(proveedor => new CanalTelegram(
    new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(60) },
    proveedor.GetRequiredService<IOptions<OpcionesDeAvisos>>()));
builder.Services.AddSingleton<ProcesadorDeAvisos>();
builder.Services.AddSingleton<Planificador>();

// El orden importa: los servicios alojados arrancan uno tras otro, y la base de datos
// tiene que estar migrada y con particiones antes de que el planificador empiece a escribir.
builder.Services.AddHostedService<PreparacionDeBaseDeDatos>();
builder.Services.AddHostedService(proveedor => proveedor.GetRequiredService<Planificador>());
builder.Services.AddHostedService<TareasDeDatos>();
builder.Services.AddHostedService<EnviadorDeAvisos>();
builder.Services.AddHostedService(proveedor => new EscuchaDeCambios(
    builder.Configuration.GetConnectionString("vigia") ?? throw new InvalidOperationException("Falta la cadena de conexión «vigia»."),
    proveedor.GetRequiredService<Planificador>(),
    proveedor.GetRequiredService<TimeProvider>(),
    proveedor.GetRequiredService<ILogger<EscuchaDeCambios>>()));

builder.Services.AddOpenTelemetry().WithMetrics(metricas => metricas.AddMeter(MetricasVigia.NombreMedidor));

var app = builder.Build();

if (app.Services.GetRequiredService<DestinosDeAviso>().Lista.Count == 0)
{
    app.Services.GetRequiredService<ILogger<Program>>().SinDestinos();
}

app.MapDefaultEndpoints();

app.Run();

/// <summary>Visible para los tests de integración (WebApplicationFactory).</summary>
public partial class Program;
