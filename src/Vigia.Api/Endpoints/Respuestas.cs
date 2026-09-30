using Vigia.Dominio.Comun;

namespace Vigia.Api.Endpoints;

/// <summary>Las respuestas de error de la API: siempre <c>application/problem+json</c> (RFC 9457), con un código estable que se puede programar contra él.</summary>
internal static class Respuestas
{
    public static IResult Invalido(ErrorDominio error) => Problema(error.Mensaje, StatusCodes.Status400BadRequest, error.Codigo);

    public static IResult Invalido(string codigo, string mensaje) => Problema(mensaje, StatusCodes.Status400BadRequest, codigo);

    public static IResult NoEncontrado(string que) => Problema($"{que} no existe.", StatusCodes.Status404NotFound, "no_encontrado");

    public static IResult Conflicto(string codigo, string mensaje) => Problema(mensaje, StatusCodes.Status409Conflict, codigo);

    private static IResult Problema(string titulo, int estado, string codigo) =>
        Results.Problem(title: titulo, statusCode: estado, extensions: new Dictionary<string, object?> { ["codigo"] = codigo });
}
