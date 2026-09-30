using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

using Vigia.Web.Components;
using Vigia.Web.Servicios;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

// Las claves con las que se cifran la cookie de sesión y los tokens antifalsificación. Sin guardarlas, cada reinicio las cambiaría
// y cerraría todas las sesiones y los formularios abiertos: en producción van a un volumen (DataProtection__Ruta).
if (builder.Configuration["DataProtection:Ruta"] is { Length: > 0 } rutaDeClaves)
{
    builder.Services.AddDataProtection().SetApplicationName("vigia-web").PersistKeysToFileSystem(new DirectoryInfo(rutaDeClaves));
}

// --- La API ------------------------------------------------------------------------------------------------------
builder.Services.Configure<OpcionesApi>(builder.Configuration.GetSection(OpcionesApi.Seccion));
builder.Services.AddHttpClient<IClienteDeApi, ClienteDeApi>((proveedor, cliente) => cliente.BaseAddress = proveedor.GetRequiredService<IOptions<OpcionesApi>>().Value.Url);
builder.Services.AddScoped<IConexionEnVivo, ConexionEnVivo>();

// --- Sesión: el panel guarda el token de la API en su propia cookie (cifrada, HttpOnly) --------------------------------
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opciones =>
    {
        opciones.LoginPath = "/entrar";
        opciones.ReturnUrlParameter = "volver";
        opciones.Cookie.Name = "vigia.sesion";
        opciones.Cookie.HttpOnly = true;
        opciones.Cookie.SameSite = SameSiteMode.Lax;

        // Fuera de desarrollo la cookie solo viaja por HTTPS (el proxy termina el TLS y avisa con X-Forwarded-Proto).
        opciones.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        opciones.SlidingExpiration = false; // La sesión dura lo mismo que el token: una hora.
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/no-encontrado", createScopeForStatusCodePages: true);
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapDefaultEndpoints();

// Salir: un formulario con POST y antifalsificación, nunca un enlace (un enlace podría cerrar la sesión desde otra web).
app.MapPost("/salir", async (HttpContext contexto, [Microsoft.AspNetCore.Mvc.FromForm] string? _) =>
{
    await contexto.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    return Results.Redirect("/entrar");
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

/// <summary>Visible para los tests de integración (WebApplicationFactory).</summary>
public partial class Program;
