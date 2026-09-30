using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

using Vigia.Api.Consultas;
using Vigia.Api.Endpoints;
using Vigia.Api.Seguridad;
using Vigia.Api.TiempoReal;
using Vigia.Comprobaciones;
using Vigia.Datos.Persistencia;

// «dotnet run --project src/Vigia.Api -- hash-contrasena» lee una contraseña de la entrada estándar y escribe su hash,
// que es lo que se pone en la configuración (Acceso__HashContrasena). La contraseña nunca se guarda.
if (args.Length > 0 && args[0] == "hash-contrasena")
{
    Console.Error.WriteLine("Escribe la contraseña y pulsa Intro:");
    var contrasena = Console.ReadLine();

    if (string.IsNullOrEmpty(contrasena))
    {
        Console.Error.WriteLine("La contraseña no puede estar vacía.");
        return 1;
    }

    Console.WriteLine(Contrasenas.Hash(contrasena));

    return 0;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<PeticionesInvalidas>();
builder.Services.AddOpenApi();

// Los estados y los tipos de fallo se escriben con su nombre («Caido»), no con un número que nadie recuerda.
builder.Services.ConfigureHttpJsonOptions(opciones => opciones.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.AddNpgsqlDbContext<VigiaDbContext>("vigia", configureDbContextOptions: opciones => OpcionesVigia.Configurar(opciones));
builder.Services.AddComprobaciones();
builder.Services.AddScoped<ConsultasDePanel>();

// --- Acceso: una contraseña, sesiones con JWT de corta duración ----------------------------------------------------
builder.Services.AddOptions<OpcionesAcceso>()
    .Bind(builder.Configuration.GetSection(OpcionesAcceso.Seccion))
    .PostConfigure(opciones =>
    {
        // En desarrollo, sin clave configurada, se genera una al arrancar: las sesiones duran hasta reiniciar y nadie tiene que inventar un secreto para probar.
        if (string.IsNullOrEmpty(opciones.ClaveJwt) && builder.Environment.IsDevelopment())
        {
            opciones.ClaveJwt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        }
    })
    .Validate(opciones => opciones.ClaveJwt is { Length: >= OpcionesAcceso.LongitudMinimaDeLaClave }, $"Acceso:ClaveJwt debe tener al menos {OpcionesAcceso.LongitudMinimaDeLaClave} caracteres.")
    .ValidateOnStart();

builder.Services.AddSingleton<EmisorDeTokens>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<OpcionesAcceso>, TimeProvider>((opciones, acceso, reloj) =>
    {
        opciones.MapInboundClaims = false;
        opciones.TokenValidationParameters = EmisorDeTokens.Parametros(acceso.Value, reloj);

        // Los WebSockets del navegador no pueden mandar cabeceras: SignalR manda el token en la dirección, y solo para su hub.
        opciones.Events = new JwtBearerEvents
        {
            OnMessageReceived = contexto =>
            {
                if (contexto.Request.Path.StartsWithSegments(PanelHub.Ruta, StringComparison.Ordinal) && contexto.Request.Query.TryGetValue("access_token", out var token))
                {
                    contexto.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// --- Límites: entrar (fuerza bruta) y la página pública (la ve cualquiera) ---------------------------------------------------------
builder.Services.AddRateLimiter(opciones =>
{
    opciones.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static string Cliente(HttpContext contexto) => contexto.Connection.RemoteIpAddress?.ToString() ?? "desconocido";

    opciones.AddPolicy(EndpointsPublicos.PoliticaDeAcceso, contexto => RateLimitPartition.GetFixedWindowLimiter(
        Cliente(contexto),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    opciones.AddPolicy(EndpointsPublicos.PoliticaPublica, contexto => RateLimitPartition.GetFixedWindowLimiter(
        Cliente(contexto),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// --- Tiempo real -------------------------------------------------------------------------------------------------------------
builder.Services.AddSignalR().AddJsonProtocol(protocolo => protocolo.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHostedService(proveedor => new ReenvioDeComprobaciones(
    builder.Configuration.GetConnectionString("vigia") ?? throw new InvalidOperationException("Falta la cadena de conexión «vigia»."),
    proveedor.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<PanelHub>>(),
    proveedor.GetRequiredService<TimeProvider>(),
    proveedor.GetRequiredService<ILogger<ReenvioDeComprobaciones>>()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();

var api = app.MapGroup("/api");
api.MapAcceso();
api.MapPublico();
api.MapMonitores();
api.MapHistorico();

app.MapHub<PanelHub>(PanelHub.Ruta);

app.Run();

return 0;

/// <summary>Visible para los tests de integración (WebApplicationFactory).</summary>
public partial class Program;
