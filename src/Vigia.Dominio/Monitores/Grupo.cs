using System.Text.RegularExpressions;

using Vigia.Dominio.Comun;

namespace Vigia.Dominio.Monitores;

/// <summary>Un conjunto de monitores que se enseña junto en una página de estado pública (<c>/estado/{slug}</c>).</summary>
public sealed partial class Grupo
{
    // Constructor para que EF Core reconstruya el grupo desde la base de datos.
    private Grupo()
    {
        Slug = null!;
        Nombre = null!;
    }

    private Grupo(Guid id, string slug, string nombre, bool publico)
    {
        Id = id;
        Slug = slug;
        Nombre = nombre;
        Publico = publico;
    }

    public Guid Id { get; }

    /// <summary>La parte de la URL de la página de estado: minúsculas, números y guiones.</summary>
    public string Slug { get; }

    public string Nombre { get; private set; }

    /// <summary>Si su página de estado se puede ver sin iniciar sesión.</summary>
    public bool Publico { get; private set; }

    public static Resultado<Grupo> Crear(string slug, string nombre, bool publico)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length is < 2 or > 40 || !PatronSlug().IsMatch(slug))
        {
            return Resultado.Fallo<Grupo>(ErroresMonitor.GrupoSlugInvalido);
        }

        var nombreLimpio = nombre?.Trim() ?? string.Empty;

        return nombreLimpio.Length is < 1 or > 100
            ? Resultado.Fallo<Grupo>(ErroresMonitor.GrupoNombreInvalido)
            : Resultado.Exito(new Grupo(Guid.NewGuid(), slug, nombreLimpio, publico));
    }

    /// <summary>Cambia el nombre y si la página de estado es pública. El identificador (slug) no cambia: es la dirección de la página.</summary>
    public Resultado Modificar(string nombre, bool publico)
    {
        var nombreLimpio = nombre?.Trim() ?? string.Empty;

        if (nombreLimpio.Length is < 1 or > 100)
        {
            return Resultado.Fallo(ErroresMonitor.GrupoNombreInvalido);
        }

        Nombre = nombreLimpio;
        Publico = publico;

        return Resultado.Exito();
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PatronSlug();
}
