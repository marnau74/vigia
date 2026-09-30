using Vigia.Datos.Persistencia;

using Monitor = Vigia.Dominio.Monitores.Monitor;

namespace Vigia.Worker.Planificacion;

/// <summary>
/// Qué monitor toca comprobar y cuándo. Es una cola de prioridad ordenada por el instante en que vence
/// cada uno: sacar el siguiente cuesta O(log n) y, cuando no hay nada vencido, se sabe exactamente
/// cuánto dormir. No lee el reloj ni la base de datos (recibe la hora como argumento), así que se prueba
/// sin esperar.
/// </summary>
/// <remarks>
/// Tres reglas la hacen fiable:
/// <list type="bullet">
/// <item><b>Sin solapes.</b> Un monitor que se saca de la cola no vuelve a entrar hasta que se marca como
/// terminado: si una comprobación tarda más de lo previsto, no se lanza otra encima.</item>
/// <item><b>Ritmo fijo.</b> El siguiente vencimiento se calcula desde el anterior previsto, no desde cuándo
/// terminó, para que la cadencia no se vaya desplazando. Si el worker se quedó atrás, se salta lo perdido en
/// lugar de hacer una ráfaga para recuperarlo.</item>
/// <item><b>Reparto inicial.</b> La primera comprobación de cada monitor se desplaza una fracción fija
/// (derivada de su id) de su intervalo, para que 500 monitores de un minuto no se lancen todos a la vez.</item>
/// </list>
/// La cola se borra «perezosamente»: al quitar o cambiar un monitor se invalida su entrada y se descarta
/// al llegar a la cabeza, en lugar de buscarla dentro del montículo.
/// </remarks>
public sealed class ColaDeVencimientos
{
    private readonly object _bloqueo = new();
    private readonly PriorityQueue<(Guid Id, int Version), DateTimeOffset> _cola = new();
    private readonly Dictionary<Guid, Entrada> _entradas = [];

    /// <summary>Cuántos monitores hay programados (en cola o comprobándose).</summary>
    public int Cantidad
    {
        get
        {
            lock (_bloqueo)
            {
                return _entradas.Count;
            }
        }
    }

    /// <summary>Cuántas comprobaciones están sacadas de la cola y sin terminar.</summary>
    public int EnCurso
    {
        get
        {
            lock (_bloqueo)
            {
                return _entradas.Values.Count(e => e.EnCurso);
            }
        }
    }

    /// <summary>Instante en que un monitor hace su primera comprobación: un punto fijo dentro de su primer intervalo.</summary>
    public static DateTimeOffset PrimerVencimiento(Guid monitorId, TimeSpan intervalo, DateTimeOffset ahora)
    {
        var fraccion = BitConverter.ToUInt32(monitorId.ToByteArray(), 0) / (double)uint.MaxValue;

        return ahora + TimeSpan.FromTicks((long)(intervalo.Ticks * fraccion));
    }

    /// <summary>
    /// Pone la cola al día con la lista de monitores activos: añade los nuevos, quita los que ya no están
    /// (pausados o borrados) y adelanta los modificados, que se comprueban enseguida para ver el efecto del cambio.
    /// </summary>
    public void Sincronizar(IReadOnlyCollection<Monitor> activos, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(activos);

        lock (_bloqueo)
        {
            var ids = activos.Select(m => m.Id).ToHashSet();

            foreach (var id in _entradas.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                _entradas.Remove(id);
            }

            foreach (var monitor in activos)
            {
                if (!_entradas.TryGetValue(monitor.Id, out var entrada))
                {
                    _entradas[monitor.Id] = entrada = new Entrada(monitor);
                    Encolar(entrada, PrimerVencimiento(monitor.Id, monitor.Intervalo, ahora));
                }
                else if (HaCambiado(entrada.Monitor, monitor))
                {
                    entrada.Monitor = monitor;

                    // Si se está comprobando ahora, se reprogramará al terminar con la configuración nueva.
                    if (!entrada.EnCurso)
                    {
                        Encolar(entrada, ahora);
                    }
                }
                else
                {
                    entrada.Monitor = monitor;
                }
            }
        }
    }

    /// <summary>Saca los monitores que ya vencieron, hasta <paramref name="maximo"/>, y los marca «en curso».</summary>
    public IReadOnlyList<Vencimiento> TomarVencidos(DateTimeOffset ahora, int maximo)
    {
        var tomados = new List<Vencimiento>();

        lock (_bloqueo)
        {
            while (tomados.Count < maximo && _cola.TryPeek(out var cabeza, out var vence) && vence <= ahora)
            {
                _cola.Dequeue();

                if (_entradas.TryGetValue(cabeza.Id, out var entrada) && entrada.Version == cabeza.Version)
                {
                    entrada.EnCurso = true;
                    tomados.Add(new Vencimiento(entrada.Monitor, vence));
                }
            }
        }

        return tomados;
    }

    /// <summary>Marca la comprobación como terminada y vuelve a programar el monitor al ritmo de su intervalo.</summary>
    public void Completar(Guid monitorId, DateTimeOffset previsto, DateTimeOffset ahora)
    {
        lock (_bloqueo)
        {
            if (!_entradas.TryGetValue(monitorId, out var entrada))
            {
                return; // Se quitó mientras se comprobaba.
            }

            entrada.EnCurso = false;

            var siguiente = previsto + entrada.Monitor.Intervalo;

            if (siguiente <= ahora)
            {
                // Se quedó atrás: se salta al primer múltiplo del intervalo que aún no ha pasado.
                var perdidos = (long)Math.Floor((ahora - previsto) / entrada.Monitor.Intervalo) + 1;
                siguiente = previsto + (entrada.Monitor.Intervalo * perdidos);
            }

            Encolar(entrada, siguiente);
        }
    }

    /// <summary>Cuándo vence el siguiente, o nada si no hay ninguno en cola.</summary>
    public DateTimeOffset? ProximoVencimiento()
    {
        lock (_bloqueo)
        {
            return ProximoVencimientoSinBloqueo();
        }
    }

    /// <summary>Sin nada vencido y sin nada comprobándose: el sistema ha terminado todo lo que tocaba a esta hora.</summary>
    public bool EstaOcioso(DateTimeOffset ahora)
    {
        lock (_bloqueo)
        {
            return _entradas.Values.All(e => !e.EnCurso) && (ProximoVencimientoSinBloqueo() is not { } vence || vence > ahora);
        }
    }

    private DateTimeOffset? ProximoVencimientoSinBloqueo()
    {
        while (_cola.TryPeek(out var cabeza, out var vence))
        {
            if (_entradas.TryGetValue(cabeza.Id, out var entrada) && entrada.Version == cabeza.Version)
            {
                return vence;
            }

            _cola.Dequeue();
        }

        return null;
    }

    private void Encolar(Entrada entrada, DateTimeOffset vence)
    {
        entrada.Version++;
        _cola.Enqueue((entrada.Monitor.Id, entrada.Version), vence);
    }

    // Se compara por el JSON de la configuración: los monitores llegan de la base de datos como objetos
    // nuevos en cada recarga y no deben contar como «cambiados» si su contenido es el mismo.
    private static bool HaCambiado(Monitor anterior, Monitor nuevo) =>
        anterior.Intervalo != nuevo.Intervalo
        || ConversorConfiguracion.Serializar(anterior.Configuracion) != ConversorConfiguracion.Serializar(nuevo.Configuracion);

    private sealed class Entrada(Monitor monitor)
    {
        public Monitor Monitor { get; set; } = monitor;

        public int Version { get; set; }

        public bool EnCurso { get; set; }
    }
}

/// <summary>Un monitor que toca comprobar y el instante para el que estaba previsto.</summary>
public sealed record Vencimiento(Monitor Monitor, DateTimeOffset Previsto);
