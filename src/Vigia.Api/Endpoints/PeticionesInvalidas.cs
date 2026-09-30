using Microsoft.AspNetCore.Diagnostics;

namespace Vigia.Api.Endpoints;

/// <summary>
/// Un cuerpo que no es JSON, vacío o con tipos equivocados es un error de quien llama (400), no un fallo
/// del servidor (500). ASP.NET Core lo señala con una excepción que, sin esto, el manejador general
/// convertiría en un 500 y llenaría los registros de errores que no lo son.
/// </summary>
public sealed class PeticionesInvalidas : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not BadHttpRequestException invalida)
        {
            return false;
        }

        httpContext.Response.StatusCode = invalida.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(
            new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = invalida.StatusCode,
                Title = "La petición no tiene el formato esperado.",
                Extensions = { ["codigo"] = "peticion_invalida" },
            },
            options: null,
            contentType: "application/problem+json",
            cancellationToken);

        return true;
    }
}
