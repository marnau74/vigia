using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using Vigia.Api.Consultas;
using Vigia.Api.Seguridad;
using Vigia.Contratos;

namespace Vigia.Api.Endpoints;

/// <summary>Lo que no necesita sesión: entrar y la página de estado pública.</summary>
public static partial class EndpointsPublicos
{
    public const string PoliticaDeAcceso = "acceso";
    public const string PoliticaPublica = "publico";

    public static void MapAcceso(this IEndpointRouteBuilder rutas)
    {
        ArgumentNullException.ThrowIfNull(rutas);

        rutas.MapPost("/acceso", (SolicitudAcceso solicitud, IOptions<OpcionesAcceso> opciones, EmisorDeTokens emisor) =>
        {
            var hash = opciones.Value.HashContrasena;

            if (string.IsNullOrEmpty(hash))
            {
                return Results.Problem(
                    title: "El acceso no está configurado: falta Acceso:HashContrasena.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: new Dictionary<string, object?> { ["codigo"] = "acceso.no_configurado" });
            }

            // El mismo mensaje para una contraseña vacía o equivocada: no se da pistas.
            if (!Contrasenas.Verificar(hash, solicitud.Contrasena ?? string.Empty))
            {
                return Results.Problem(
                    title: "Contraseña incorrecta.",
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?> { ["codigo"] = "acceso.contrasena_incorrecta" });
            }

            var (token, expira) = emisor.Emitir();

            return Results.Ok(new RespuestaAcceso(token, expira));
        })
        .AllowAnonymous()
        .RequireRateLimiting(PoliticaDeAcceso)
        .WithTags("Acceso")
        .WithSummary("Entra con la contraseña y devuelve un token de sesión (Authorization: Bearer).");
    }

    public static void MapPublico(this IEndpointRouteBuilder rutas)
    {
        ArgumentNullException.ThrowIfNull(rutas);

        rutas.MapGet("/publico/estado/{slug}", async (string slug, ConsultasDePanel consultas, HttpResponse respuesta, CancellationToken ct) =>
        {
            // Un identificador con forma inválida ni se consulta: es un 404 igual que uno que no existe.
            var pagina = PatronDeSlug().IsMatch(slug) ? await consultas.PaginaPublicaAsync(slug, ct) : null;

            if (pagina is null)
            {
                return Respuestas.NoEncontrado("La página de estado");
            }

            // Cualquiera puede pedirla: un minuto de caché en el navegador y en el proxy frena el abuso y no importa a nadie un estado con 30 s de retraso.
            respuesta.Headers.CacheControl = "public, max-age=30";

            return Results.Ok(pagina);
        })
        .AllowAnonymous()
        .RequireRateLimiting(PoliticaPublica)
        .WithTags("Página de estado pública");
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex PatronDeSlug();
}
