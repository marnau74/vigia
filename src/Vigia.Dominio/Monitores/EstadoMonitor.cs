namespace Vigia.Dominio.Monitores;

/// <summary>En qué situación está un monitor según sus últimas comprobaciones.</summary>
public enum EstadoMonitor
{
    /// <summary>Todavía no hay datos: recién creado, o acaba de salir de un mantenimiento.</summary>
    Desconocido = 0,

    /// <summary>La última comprobación fue bien y a buena velocidad.</summary>
    Operativo = 1,

    /// <summary>Responde, pero más despacio que el umbral configurado.</summary>
    Degradado = 2,

    /// <summary>
    /// Ha habido uno o más fallos, pero todavía no los suficientes seguidos para darlo por caído. Existe
    /// para no avisar por un fallo aislado de red (<i>flapping</i>): un aviso falso a las tres de la
    /// mañana hace que la gente deje de fiarse de los avisos verdaderos.
    /// </summary>
    Sospechoso = 3,

    /// <summary>Fallos seguidos hasta el límite: hay un incidente abierto.</summary>
    Caido = 4,

    /// <summary>Dentro de una ventana de mantenimiento: sin avisos y sin contar en la disponibilidad.</summary>
    Mantenimiento = 5,
}
