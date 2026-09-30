namespace Vigia.Dominio.Comun;

/// <summary>
/// Resultado de una operación de dominio: éxito o un <see cref="ErrorDominio"/> de negocio.
/// Las reglas de negocio no lanzan excepciones: un monitor con un intervalo inválido es un
/// resultado esperado, no un fallo del programa.
/// </summary>
public class Resultado
{
    protected Resultado(bool esExito, ErrorDominio error)
    {
        if (esExito == (error != ErrorDominio.Ninguno))
        {
            throw new ArgumentException("Un éxito no lleva error y un fallo siempre lo lleva.", nameof(error));
        }

        EsExito = esExito;
        Error = error;
    }

    public bool EsExito { get; }

    public bool EsFallo => !EsExito;

    public ErrorDominio Error { get; }

    public static Resultado Exito() => new(true, ErrorDominio.Ninguno);

    public static Resultado Fallo(ErrorDominio error) => new(false, error);

    public static Resultado<T> Exito<T>(T valor) => new(valor, true, ErrorDominio.Ninguno);

    public static Resultado<T> Fallo<T>(ErrorDominio error) => new(default, false, error);
}

/// <summary>Resultado que, si tiene éxito, lleva un valor.</summary>
public sealed class Resultado<T> : Resultado
{
    private readonly T? _valor;

    internal Resultado(T? valor, bool esExito, ErrorDominio error)
        : base(esExito, error) => _valor = valor;

    /// <summary>El valor del éxito. Pedirlo en un fallo es un error de programación.</summary>
    public T Valor => EsExito
        ? _valor!
        : throw new InvalidOperationException($"No hay valor: la operación falló ({Error.Codigo}).");
}
