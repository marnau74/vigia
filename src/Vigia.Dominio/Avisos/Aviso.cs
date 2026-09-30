namespace Vigia.Dominio.Avisos;

/// <summary>Qué se cuenta: una caída o una recuperación. Solo hay un aviso de cada por incidente y destino.</summary>
public enum TipoAviso
{
    Caida = 1,
    Recuperacion = 2,
}

public enum CanalAviso
{
    Correo = 1,
    Telegram = 2,
}

/// <summary>A dónde se manda: una dirección de correo o un chat de Telegram.</summary>
public sealed record DestinoDeAviso(CanalAviso Canal, string Direccion);

/// <summary>
/// Un aviso que hay que enviar, guardado en la base de datos en la misma transacción que el cambio de
/// estado que lo provoca (patrón «bandeja de salida»). Así no puede haber una caída sin su aviso ni un
/// aviso de algo que no llegó a pasar, aunque el servidor de correo o Telegram estén caídos: otro proceso
/// lo envía después, con reintentos espaciados.
/// </summary>
/// <remarks>
/// Hay como máximo un aviso por (incidente, tipo, canal, destino): la base de datos lo garantiza con un
/// índice único, así que ni un reinicio ni dos instancias pueden mandar el mismo aviso dos veces.
/// </remarks>
public sealed class Aviso
{
    /// <summary>Intentos fallidos tras los cuales se deja de insistir.</summary>
    public const int MaximoIntentos = 6;

    /// <summary>Espera antes de cada reintento: tras el primer fallo 30 s, tras el segundo 2 min, etc.</summary>
    public static readonly TimeSpan[] Esperas =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
    ];

    // Constructor para que EF Core reconstruya el aviso desde la base de datos.
    private Aviso() => Destino = null!;

    private Aviso(Guid incidenteId, Guid monitorId, TipoAviso tipo, CanalAviso canal, string destino, DateTimeOffset creadoEn)
    {
        Id = Guid.NewGuid();
        IncidenteId = incidenteId;
        MonitorId = monitorId;
        Tipo = tipo;
        Canal = canal;
        Destino = destino;
        CreadoEn = creadoEn.ToUniversalTime();
        ProximoIntentoEn = CreadoEn;
    }

    public Guid Id { get; }

    public Guid IncidenteId { get; }

    public Guid MonitorId { get; }

    public TipoAviso Tipo { get; }

    public CanalAviso Canal { get; }

    /// <summary>La dirección de correo o el identificador del chat.</summary>
    public string Destino { get; }

    public DateTimeOffset CreadoEn { get; }

    public int Intentos { get; private set; }

    /// <summary>Desde cuándo se puede (re)intentar el envío.</summary>
    public DateTimeOffset ProximoIntentoEn { get; private set; }

    public DateTimeOffset? EnviadoEn { get; private set; }

    /// <summary>Se agotaron los intentos: no se envía más y hay que mirarlo a mano.</summary>
    public bool Abandonado { get; private set; }

    public string? UltimoError { get; private set; }

    public bool EstaPendiente => EnviadoEn is null && !Abandonado;

    public static Aviso Crear(Guid incidenteId, Guid monitorId, TipoAviso tipo, DestinoDeAviso destino, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(destino);
        ArgumentException.ThrowIfNullOrWhiteSpace(destino.Direccion);

        return new Aviso(incidenteId, monitorId, tipo, destino.Canal, destino.Direccion.Trim(), ahora);
    }

    /// <summary>Lo aparta para un proceso durante un rato, para que otro no lo envíe a la vez.</summary>
    public void Reservar(DateTimeOffset hasta) => ProximoIntentoEn = hasta.ToUniversalTime();

    public void MarcarEnviado(DateTimeOffset ahora)
    {
        EnviadoEn = ahora.ToUniversalTime();
        UltimoError = null;
    }

    /// <summary>Anota un fallo y programa el siguiente intento, o abandona el aviso si ya se intentó demasiado.</summary>
    public void RegistrarFallo(string error, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(error);

        Intentos++;
        UltimoError = error.Length > 500 ? error[..500] : error;

        if (Intentos >= MaximoIntentos)
        {
            Abandonado = true;
            return;
        }

        ProximoIntentoEn = (ahora + Esperas[Intentos - 1]).ToUniversalTime();
    }
}
