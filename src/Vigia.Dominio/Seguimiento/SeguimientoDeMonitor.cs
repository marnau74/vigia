using Vigia.Dominio.Monitores;

namespace Vigia.Dominio.Seguimiento;

/// <summary>
/// La máquina de estados de un monitor: recibe comprobaciones una a una y decide en qué estado queda,
/// cuándo se abre o se cierra un incidente y qué hay que avisar. Es código puro: no usa la red, ni el
/// reloj, ni la base de datos, así que se prueba con secuencias de resultados («bien, bien, fallo,
/// fallo, fallo, bien») sin esperar nada.
/// </summary>
/// <remarks>
/// <code>
/// Desconocido ─OK→ Operativo ⇄ Degradado          (OK lento ↔ OK rápido)
/// Operativo/Degradado/Desconocido ─fallo→ Sospechoso ─OK→ Operativo
/// Sospechoso ─N fallos seguidos→ Caido  (abre incidente y avisa una vez)
/// Caido ─OK→ Operativo/Degradado        (cierra el incidente y avisa una vez)
/// cualquiera ─ventana de mantenimiento→ Mantenimiento ─fin→ Desconocido
/// cualquiera ─pausa, o hueco sin comprobaciones→ Desconocido   (cierra el incidente sin avisar)
/// </code>
/// Al salir de un mantenimiento se vuelve a «Desconocido», no a «Operativo»: nadie sabe cómo está el
/// servicio hasta la primera comprobación, y si sigue caído se detecta por el camino normal. Lo mismo al
/// dejar de vigilarlo: el tiempo sin comprobaciones no se atribuye al último estado visto.
/// </remarks>
public sealed class SeguimientoDeMonitor
{
    /// <summary>Un monitor nuevo, sin datos todavía.</summary>
    public SeguimientoDeMonitor(Guid monitorId, DateTimeOffset desde)
        : this(monitorId, EstadoMonitor.Desconocido, 0, desde, null, null)
    {
    }

    /// <summary>Reconstruye el seguimiento desde lo guardado en la base de datos.</summary>
    public SeguimientoDeMonitor(
        Guid monitorId,
        EstadoMonitor estado,
        int fallosSeguidos,
        DateTimeOffset desde,
        DateTimeOffset? ultimaObservacion,
        Incidente? incidenteAbierto)
    {
        MonitorId = monitorId;
        Estado = estado;
        FallosSeguidos = fallosSeguidos;
        Desde = desde.ToUniversalTime();
        UltimaObservacion = ultimaObservacion?.ToUniversalTime();
        IncidenteAbierto = incidenteAbierto;
    }

    public Guid MonitorId { get; }

    public EstadoMonitor Estado { get; private set; }

    /// <summary>Fallos consecutivos hasta ahora; vuelve a cero con cualquier éxito.</summary>
    public int FallosSeguidos { get; private set; }

    /// <summary>Desde cuándo está en el estado actual.</summary>
    public DateTimeOffset Desde { get; private set; }

    public DateTimeOffset? UltimaObservacion { get; private set; }

    /// <summary>El incidente en curso: existe exactamente cuando el estado es «caído».</summary>
    public Incidente? IncidenteAbierto { get; private set; }

    /// <summary>
    /// Anota una comprobación y devuelve lo que ha pasado, en orden. Una observación de un momento
    /// anterior o igual a la última ya vista se ignora (un reintento tras un reinicio no debe contar dos veces),
    /// y las que llegan durante un mantenimiento tampoco cuentan. Si desde la anterior ha pasado más de lo que
    /// vale una comprobación (<see cref="ReglasDeSeguimiento.Vigencia"/>), primero se anota que se dejó de
    /// vigilar cuando venció la anterior.
    /// </summary>
    public IReadOnlyList<EventoDeSeguimiento> Registrar(Observacion observacion, ReglasDeSeguimiento reglas)
    {
        ArgumentNullException.ThrowIfNull(observacion);
        ArgumentNullException.ThrowIfNull(reglas);

        if (Estado == EstadoMonitor.Mantenimiento || (UltimaObservacion is { } ultima && observacion.Momento <= ultima))
        {
            return [];
        }

        var eventos = new List<EventoDeSeguimiento>();

        if (reglas.Vigencia is { } vigencia && UltimaObservacion is { } anterior && observacion.Momento - anterior > vigencia)
        {
            eventos.AddRange(DejarDeVigilar(anterior + vigencia));
        }

        UltimaObservacion = observacion.Momento.ToUniversalTime();
        eventos.AddRange(observacion.Correcto ? RegistrarExito(observacion, reglas) : RegistrarFallo(observacion, reglas));

        return eventos;
    }

    /// <summary>
    /// Se deja de vigilar el monitor (se ha pausado, o hubo un hueco sin comprobaciones): desde
    /// <paramref name="momento"/> no se sabe cómo está. Pasa a «desconocido» y cierra el incidente abierto sin
    /// avisar, porque nadie ha visto que el servicio se recupere; si al volver a mirar sigue caído, se abre otro
    /// por el camino normal. Es idempotente.
    /// </summary>
    public IReadOnlyList<EventoDeSeguimiento> DejarDeVigilar(DateTimeOffset momento)
    {
        // Nunca antes del estado actual: la línea de tiempo de cambios tiene que ir hacia delante.
        var desde = momento > Desde ? momento : Desde;
        var eventos = new List<EventoDeSeguimiento>();

        FallosSeguidos = 0;
        Cambiar(EstadoMonitor.Desconocido, desde, eventos);
        CerrarIncidente(MotivoDeCierre.SinVigilancia, desde, eventos);

        return eventos;
    }

    /// <summary>
    /// Aplica el mantenimiento que corresponde al momento actual. Se llama en cada pasada del planificador
    /// y es idempotente: repetirlo no cambia nada.
    /// </summary>
    public IReadOnlyList<EventoDeSeguimiento> ActualizarMantenimiento(bool enVentana, DateTimeOffset momento)
    {
        var eventos = new List<EventoDeSeguimiento>();

        if (enVentana && Estado != EstadoMonitor.Mantenimiento)
        {
            FallosSeguidos = 0;
            Cambiar(EstadoMonitor.Mantenimiento, momento, eventos);
            CerrarIncidente(MotivoDeCierre.Mantenimiento, momento, eventos);
        }
        else if (!enVentana && Estado == EstadoMonitor.Mantenimiento)
        {
            Cambiar(EstadoMonitor.Desconocido, momento, eventos);
        }

        return eventos;
    }

    private List<EventoDeSeguimiento> RegistrarExito(Observacion observacion, ReglasDeSeguimiento reglas)
    {
        var eventos = new List<EventoDeSeguimiento>();
        var nuevo = reglas.UmbralLento is { } umbral && observacion.Latencia > umbral ? EstadoMonitor.Degradado : EstadoMonitor.Operativo;

        FallosSeguidos = 0;
        Cambiar(nuevo, observacion.Momento, eventos);

        // Siempre se anota primero el cambio de estado y después lo que provoca (abrir o cerrar el incidente).
        CerrarIncidente(MotivoDeCierre.Recuperado, observacion.Momento, eventos);

        return eventos;
    }

    private List<EventoDeSeguimiento> RegistrarFallo(Observacion observacion, ReglasDeSeguimiento reglas)
    {
        var eventos = new List<EventoDeSeguimiento>();
        var causa = string.IsNullOrWhiteSpace(observacion.Error) ? "Sin detalle del error." : observacion.Error;

        FallosSeguidos++;
        IncidenteAbierto?.RegistrarFallo(causa);

        var nuevo = FallosSeguidos >= reglas.FallosParaIncidente ? EstadoMonitor.Caido : EstadoMonitor.Sospechoso;
        var abre = nuevo == EstadoMonitor.Caido && IncidenteAbierto is null;

        Cambiar(nuevo, observacion.Momento, eventos);

        if (abre)
        {
            IncidenteAbierto = Incidente.Abrir(MonitorId, observacion.Momento, causa, FallosSeguidos);
            eventos.Add(new IncidenteAbierto(MonitorId, IncidenteAbierto, observacion.Momento));
        }

        return eventos;
    }

    private void CerrarIncidente(MotivoDeCierre motivo, DateTimeOffset momento, List<EventoDeSeguimiento> eventos)
    {
        if (IncidenteAbierto is not { } incidente)
        {
            return;
        }

        incidente.Cerrar(momento, motivo);
        eventos.Add(new IncidenteCerrado(MonitorId, incidente, motivo, momento));
        IncidenteAbierto = null;
    }

    private void Cambiar(EstadoMonitor nuevo, DateTimeOffset momento, List<EventoDeSeguimiento> eventos)
    {
        if (nuevo == Estado)
        {
            return;
        }

        eventos.Add(new EstadoCambiado(MonitorId, Estado, nuevo, momento));
        Estado = nuevo;
        Desde = momento.ToUniversalTime();
    }
}
