namespace Vigia.Dominio.Comun;

/// <summary>
/// Error de negocio con un código estable (p. ej. <c>reserva.mesa_ocupada</c>) que la API
/// devuelve en los ProblemDetails, y un mensaje legible.
/// </summary>
public sealed record ErrorDominio(string Codigo, string Mensaje)
{
    public static readonly ErrorDominio Ninguno = new(string.Empty, string.Empty);
}
