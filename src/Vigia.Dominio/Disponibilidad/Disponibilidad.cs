using System.Globalization;

using Vigia.Dominio.Monitores;

namespace Vigia.Dominio.Disponibilidad;

/// <summary>El monitor estuvo en un estado durante un tramo de tiempo. El fin es exclusivo.</summary>
public sealed record TramoDeEstado(EstadoMonitor Estado, DateTimeOffset Inicio, DateTimeOffset Fin);

/// <summary>
/// Cuánto tiempo pasó un monitor en cada estado. Es lo que se guarda por horas y por días (agregados):
/// con esto se calcula la disponibilidad de cualquier periodo sin recorrer la tabla de comprobaciones.
/// </summary>
public sealed record TiemposPorEstado
{
    private readonly TimeSpan[] _tiempos;

    public TiemposPorEstado()
        : this(new TimeSpan[Enum.GetValues<EstadoMonitor>().Length])
    {
    }

    private TiemposPorEstado(TimeSpan[] tiempos) => _tiempos = tiempos;

    public TimeSpan this[EstadoMonitor estado] => _tiempos[(int)estado];

    public static TiemposPorEstado Vacio { get; } = new();

    /// <summary>
    /// Los tiempos por estado de una línea de tiempo, recortados al periodo [desde, hasta). Los tramos
    /// pueden empezar antes o acabar después: solo cuenta lo que cae dentro.
    /// </summary>
    public static TiemposPorEstado DeTramos(IEnumerable<TramoDeEstado> tramos, DateTimeOffset desde, DateTimeOffset hasta)
    {
        ArgumentNullException.ThrowIfNull(tramos);

        var tiempos = new TimeSpan[Enum.GetValues<EstadoMonitor>().Length];

        foreach (var tramo in tramos)
        {
            var inicio = tramo.Inicio > desde ? tramo.Inicio : desde;
            var fin = tramo.Fin < hasta ? tramo.Fin : hasta;

            if (fin > inicio)
            {
                tiempos[(int)tramo.Estado] += fin - inicio;
            }
        }

        return new TiemposPorEstado(tiempos);
    }

    /// <summary>Tiempos por estado a partir de sus duraciones (para reconstruir un agregado guardado).</summary>
    public static TiemposPorEstado De(IEnumerable<(EstadoMonitor Estado, TimeSpan Duracion)> partes)
    {
        ArgumentNullException.ThrowIfNull(partes);

        var tiempos = new TimeSpan[Enum.GetValues<EstadoMonitor>().Length];

        foreach (var (estado, duracion) in partes)
        {
            tiempos[(int)estado] += duracion;
        }

        return new TiemposPorEstado(tiempos);
    }

    /// <summary>
    /// Convierte el registro de cambios de estado en tramos: cada estado dura hasta el siguiente cambio y
    /// el último, hasta <paramref name="ahora"/>. Los cambios deben venir por orden de tiempo.
    /// </summary>
    public static IReadOnlyList<TramoDeEstado> TramosDe(IReadOnlyList<(EstadoMonitor Estado, DateTimeOffset Desde)> cambios, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(cambios);

        var tramos = new List<TramoDeEstado>(cambios.Count);

        for (var i = 0; i < cambios.Count; i++)
        {
            var fin = i + 1 < cambios.Count ? cambios[i + 1].Desde : ahora;

            if (fin > cambios[i].Desde)
            {
                tramos.Add(new TramoDeEstado(cambios[i].Estado, cambios[i].Desde, fin));
            }
        }

        return tramos;
    }

    public TiemposPorEstado Sumar(TiemposPorEstado otro)
    {
        ArgumentNullException.ThrowIfNull(otro);

        var suma = new TimeSpan[_tiempos.Length];

        for (var i = 0; i < suma.Length; i++)
        {
            suma[i] = _tiempos[i] + otro._tiempos[i];
        }

        return new TiemposPorEstado(suma);
    }

    public static TiemposPorEstado Sumar(IEnumerable<TiemposPorEstado> varios)
    {
        ArgumentNullException.ThrowIfNull(varios);

        return varios.Aggregate(Vacio, (acumulado, siguiente) => acumulado.Sumar(siguiente));
    }

    /// <summary>Tiempo «en pie»: el servicio funciona (o se sospecha de él, pero todavía no se ha confirmado ninguna caída).</summary>
    public TimeSpan EnPie => this[EstadoMonitor.Operativo] + this[EstadoMonitor.Degradado] + this[EstadoMonitor.Sospechoso];

    public TimeSpan Caido => this[EstadoMonitor.Caido];

    public TimeSpan Mantenimiento => this[EstadoMonitor.Mantenimiento];

    public TimeSpan Desconocido => this[EstadoMonitor.Desconocido];

    // Igualdad por valor de los tiempos (un record con un array compararía la referencia).
    public bool Equals(TiemposPorEstado? otro) => otro is not null && _tiempos.AsSpan().SequenceEqual(otro._tiempos);

    public override int GetHashCode()
    {
        var hash = default(HashCode);

        foreach (var tiempo in _tiempos)
        {
            hash.Add(tiempo);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Un porcentaje de disponibilidad, guardado como fracción entre 0 y 1.</summary>
public readonly record struct PorcentajeDisponibilidad(decimal Fraccion)
{
    /// <summary>
    /// El porcentaje con los decimales pedidos, <b>truncado y nunca redondeado hacia arriba</b>: una
    /// disponibilidad de 99,9996 % no puede enseñarse como 100 %, porque hubo caídas. Redondear hacia
    /// arriba es la trampa clásica de las páginas de estado.
    /// </summary>
    public decimal Porcentaje(int decimales)
    {
        var factor = (decimal)Math.Pow(10, decimales);

        return Math.Truncate(Fraccion * 100m * factor) / factor;
    }

    public string Texto(int decimales = 2) =>
        Porcentaje(decimales).ToString($"F{decimales.ToString(CultureInfo.InvariantCulture)}", CultureInfo.InvariantCulture) + " %";
}

/// <summary>
/// El cálculo de la disponibilidad, que es lo primero que se pregunta de un monitor («¿cómo calculas el 99,9 %?»).
/// <code>
/// disponibilidad = tiempo en pie / (tiempo en pie + tiempo caído)
/// </code>
/// <list type="bullet">
/// <item>El <b>mantenimiento no cuenta</b> ni a favor ni en contra: es tiempo planificado.</item>
/// <item>El tiempo <b>desconocido no cuenta</b> (no se sabe cómo estaba; contarlo como bien sería inventar un dato,
/// y contarlo como mal, penalizar por no haber mirado).</item>
/// <item>El estado <b>sospechoso cuenta como en pie</b>: un fallo aislado no es una caída hasta que se confirma.
/// Cuando se confirma, el tiempo caído se cuenta desde que se abre el incidente.</item>
/// <item>Sin datos (nada en pie ni caído) el resultado es <c>null</c>, no 100 %.</item>
/// </list>
/// </summary>
public static class CalculadoraDisponibilidad
{
    public static PorcentajeDisponibilidad? Calcular(TiemposPorEstado tiempos)
    {
        ArgumentNullException.ThrowIfNull(tiempos);

        var total = tiempos.EnPie + tiempos.Caido;

        if (total <= TimeSpan.Zero)
        {
            return null;
        }

        return new PorcentajeDisponibilidad((decimal)(tiempos.EnPie.Ticks) / total.Ticks);
    }

    public static PorcentajeDisponibilidad? Calcular(IEnumerable<TramoDeEstado> tramos, DateTimeOffset desde, DateTimeOffset hasta) =>
        Calcular(TiemposPorEstado.DeTramos(tramos, desde, hasta));
}

/// <summary>Estadística de latencias para los agregados y las gráficas.</summary>
public static class Estadistica
{
    /// <summary>
    /// El percentil por el método del rango más cercano: el menor valor tal que al menos el
    /// <paramref name="percentil"/> % de las muestras es menor o igual. Sin muestras, <c>null</c>.
    /// </summary>
    public static TimeSpan? Percentil(IEnumerable<TimeSpan> muestras, int percentil)
    {
        ArgumentNullException.ThrowIfNull(muestras);
        ArgumentOutOfRangeException.ThrowIfLessThan(percentil, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentil, 100);

        var ordenadas = muestras.Order().ToArray();

        if (ordenadas.Length == 0)
        {
            return null;
        }

        var posicion = (int)Math.Ceiling(percentil / 100d * ordenadas.Length);

        return ordenadas[Math.Max(posicion, 1) - 1];
    }

    public static TimeSpan? Media(IEnumerable<TimeSpan> muestras)
    {
        ArgumentNullException.ThrowIfNull(muestras);

        var lista = muestras.ToArray();

        return lista.Length == 0 ? null : TimeSpan.FromTicks((long)lista.Average(m => m.Ticks));
    }
}
