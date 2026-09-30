using System.Globalization;

namespace Vigia.Dominio.Monitores;

/// <summary>
/// Los códigos de estado HTTP que se consideran «correcto», escritos como una lista de números o
/// rangos: <c>200-299,301</c>. Los códigos 3xx cuentan como correctos por defecto porque las
/// redirecciones se siguen y lo que se evalúa es la respuesta final.
/// </summary>
public sealed record CodigosHttpEsperados
{
    private readonly (int Desde, int Hasta)[] _rangos;

    private CodigosHttpEsperados((int Desde, int Hasta)[] rangos, string texto)
    {
        _rangos = rangos;
        Texto = texto;
    }

    /// <summary>Cualquier respuesta que no sea un error (100 a 399).</summary>
    public static CodigosHttpEsperados PorDefecto { get; } = new([(100, 399)], "100-399");

    /// <summary>La lista tal como se escribió, normalizada.</summary>
    public string Texto { get; }

    public bool Contiene(int codigo) => _rangos.Any(r => codigo >= r.Desde && codigo <= r.Hasta);

    /// <summary>Interpreta «200», «200-299» o una lista separada por comas. Devuelve <c>false</c> si algo no es válido.</summary>
    public static bool TryParse(string? texto, out CodigosHttpEsperados codigos)
    {
        codigos = PorDefecto;

        if (string.IsNullOrWhiteSpace(texto))
        {
            return false;
        }

        var rangos = new List<(int, int)>();

        foreach (var parte in texto.Split(',', StringSplitOptions.TrimEntries))
        {
            var extremos = parte.Split('-', StringSplitOptions.TrimEntries);

            if (extremos.Length is < 1 or > 2 || !TryCodigo(extremos[0], out var desde) || !TryCodigo(extremos[^1], out var hasta) || desde > hasta)
            {
                return false;
            }

            rangos.Add((desde, hasta));
        }

        codigos = new CodigosHttpEsperados([.. rangos], string.Join(',', rangos.Select(r => r.Item1 == r.Item2 ? Numero(r.Item1) : $"{Numero(r.Item1)}-{Numero(r.Item2)}")));
        return true;
    }

    private static bool TryCodigo(string texto, out int codigo) =>
        int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out codigo) && codigo is >= 100 and <= 599;

    private static string Numero(int n) => n.ToString(CultureInfo.InvariantCulture);
}
