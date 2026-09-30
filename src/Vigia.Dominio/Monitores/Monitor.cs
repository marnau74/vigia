using Vigia.Dominio.Comun;

namespace Vigia.Dominio.Monitores;

public static class ErroresMonitor
{
    public static readonly ErrorDominio NombreInvalido =
        new("monitor.nombre_invalido", "El monitor necesita un nombre de entre 1 y 100 caracteres.");

    public static readonly ErrorDominio IntervaloInvalido =
        new("monitor.intervalo_invalido", "El intervalo entre comprobaciones debe ser de 30 segundos como mínimo y de un día como máximo.");

    public static readonly ErrorDominio FallosInvalidos =
        new("monitor.fallos_invalidos", "Los fallos seguidos antes de abrir un incidente deben estar entre 1 y 10.");

    public static readonly ErrorDominio TiempoMaximoInvalido =
        new("monitor.tiempo_maximo_invalido", "El tiempo máximo debe ser mayor que cero, de 60 segundos como mucho y menor que el intervalo.");

    public static readonly ErrorDominio UmbralLentoInvalido =
        new("monitor.umbral_lento_invalido", "El umbral de lentitud debe ser mayor que cero y menor que el tiempo máximo.");

    public static readonly ErrorDominio CambioDeTipo =
        new("monitor.cambio_de_tipo", "Un monitor no puede cambiar de tipo: crea uno nuevo (el histórico es de un tipo concreto).");

    public static readonly ErrorDominio GrupoSlugInvalido =
        new("grupo.slug_invalido", "El identificador del grupo debe tener entre 2 y 40 caracteres: minúsculas, números y guiones.");

    public static readonly ErrorDominio GrupoNombreInvalido =
        new("grupo.nombre_invalido", "El grupo necesita un nombre de entre 1 y 100 caracteres.");
}

/// <summary>Lo que hay que vigilar de un servicio y cada cuánto.</summary>
public sealed class Monitor
{
    public static readonly TimeSpan IntervaloMinimo = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan IntervaloMaximo = TimeSpan.FromDays(1);
    public static readonly TimeSpan TiempoMaximoLimite = TimeSpan.FromSeconds(60);

    // Constructor para que EF Core reconstruya el monitor desde la base de datos.
    private Monitor()
    {
        Nombre = null!;
        Configuracion = null!;
    }

    private Monitor(
        Guid id,
        string nombre,
        ConfiguracionMonitor configuracion,
        TimeSpan intervalo,
        int fallosParaIncidente,
        TimeSpan? umbralLento,
        Guid? grupoId,
        DateTimeOffset creadoEn)
    {
        Id = id;
        Nombre = nombre;
        Configuracion = configuracion;
        Intervalo = intervalo;
        FallosParaIncidente = fallosParaIncidente;
        UmbralLento = umbralLento;
        GrupoId = grupoId;
        Activo = true;
        CreadoEn = creadoEn.ToUniversalTime();
    }

    public Guid Id { get; }

    public string Nombre { get; private set; }

    public ConfiguracionMonitor Configuracion { get; private set; }

    /// <summary>Cada cuánto se comprueba.</summary>
    public TimeSpan Intervalo { get; private set; }

    /// <summary>Cuántos fallos seguidos hacen falta para dar el servicio por caído y abrir un incidente.</summary>
    public int FallosParaIncidente { get; private set; }

    /// <summary>Si responde correctamente pero tarda más que esto, se marca como degradado. Sin valor, no se vigila la lentitud.</summary>
    public TimeSpan? UmbralLento { get; private set; }

    public Guid? GrupoId { get; private set; }

    /// <summary>Un monitor pausado no se comprueba, pero conserva su histórico.</summary>
    public bool Activo { get; private set; }

    public DateTimeOffset CreadoEn { get; }

    public TipoMonitor Tipo => Configuracion.Tipo;

    public static Resultado<Monitor> Crear(
        string nombre,
        ConfiguracionMonitor configuracion,
        TimeSpan intervalo,
        int fallosParaIncidente,
        TimeSpan? umbralLento,
        Guid? grupoId,
        DateTimeOffset ahora)
    {
        var validacion = Validar(nombre, configuracion, intervalo, fallosParaIncidente, umbralLento);

        return validacion.EsFallo
            ? Resultado.Fallo<Monitor>(validacion.Error)
            : Resultado.Exito(new Monitor(Guid.NewGuid(), nombre.Trim(), configuracion, intervalo, fallosParaIncidente, umbralLento, grupoId, ahora));
    }

    /// <summary>Cambia todo lo configurable del monitor de una vez, con las mismas reglas que al crearlo.</summary>
    public Resultado Modificar(
        string nombre,
        ConfiguracionMonitor configuracion,
        TimeSpan intervalo,
        int fallosParaIncidente,
        TimeSpan? umbralLento,
        Guid? grupoId)
    {
        var validacion = Validar(nombre, configuracion, intervalo, fallosParaIncidente, umbralLento);

        if (validacion.EsFallo)
        {
            return validacion;
        }

        // Cambiar de tipo dejaría un histórico de un tipo con la configuración de otro.
        if (configuracion.Tipo != Configuracion.Tipo)
        {
            return Resultado.Fallo(ErroresMonitor.CambioDeTipo);
        }

        Nombre = nombre.Trim();
        Configuracion = configuracion;
        Intervalo = intervalo;
        FallosParaIncidente = fallosParaIncidente;
        UmbralLento = umbralLento;
        GrupoId = grupoId;

        return Resultado.Exito();
    }

    public void Pausar() => Activo = false;

    public void Reanudar() => Activo = true;

    private static Resultado Validar(string nombre, ConfiguracionMonitor configuracion, TimeSpan intervalo, int fallos, TimeSpan? umbralLento)
    {
        ArgumentNullException.ThrowIfNull(configuracion);

        if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length > 100)
        {
            return Resultado.Fallo(ErroresMonitor.NombreInvalido);
        }

        if (intervalo < IntervaloMinimo || intervalo > IntervaloMaximo)
        {
            return Resultado.Fallo(ErroresMonitor.IntervaloInvalido);
        }

        if (fallos is < 1 or > 10)
        {
            return Resultado.Fallo(ErroresMonitor.FallosInvalidos);
        }

        // Si la comprobación puede durar más que el intervalo, empezarían a solaparse unas con otras.
        if (configuracion.TiempoMaximo <= TimeSpan.Zero || configuracion.TiempoMaximo > TiempoMaximoLimite || configuracion.TiempoMaximo >= intervalo)
        {
            return Resultado.Fallo(ErroresMonitor.TiempoMaximoInvalido);
        }

        if (umbralLento is { } umbral && (umbral <= TimeSpan.Zero || umbral >= configuracion.TiempoMaximo))
        {
            return Resultado.Fallo(ErroresMonitor.UmbralLentoInvalido);
        }

        return ValidacionDeConfiguracion.Validar(configuracion);
    }
}
