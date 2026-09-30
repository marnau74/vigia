using Vigia.Dominio.Avisos;

namespace Vigia.Worker.Avisos;

/// <summary>Ajustes del servidor de correo, en la sección <c>Correo</c> de la configuración.</summary>
public sealed class OpcionesSmtp
{
    public const string Seccion = "Correo";

    /// <summary>Servidor SMTP. Vacío: el correo no se usa como canal de aviso.</summary>
    public string? Servidor { get; set; }

    public int Puerto { get; set; } = 1025;

    /// <summary>Dirección desde la que se envía.</summary>
    public string Remitente { get; set; } = "vigia@localhost";

    public string NombreRemitente { get; set; } = "Vigía";

    /// <summary>Cifrar la conexión (STARTTLS o TLS directo según el puerto). Imprescindible con un servidor real.</summary>
    public bool UsarTls { get; set; }

    public string? Usuario { get; set; }

    /// <summary>Contraseña del servidor de correo: se da por variable de entorno o gestor de secretos, nunca en un fichero del repositorio.</summary>
    public string? Contrasena { get; set; }
}

/// <summary>Ajustes de los avisos, en la sección <c>Avisos</c> de la configuración.</summary>
public sealed class OpcionesDeAvisos
{
    public const string Seccion = "Avisos";

    /// <summary>A quién se escribe por correo. Sin destinatarios (o sin servidor de correo) no se avisa por correo.</summary>
    public List<string> Destinatarios { get; set; } = [];

    /// <summary>Los chats de Telegram a los que avisa el bot. Sin ellos (o sin token) no se avisa por Telegram.</summary>
    public List<string> ChatsTelegram { get; set; } = [];

    /// <summary>El token del bot de Telegram: por variable de entorno o gestor de secretos, nunca en un fichero del repositorio.</summary>
    public string? TokenTelegram { get; set; }

    /// <summary>Dirección de la API de Telegram. Solo se cambia en los tests, que usan un servidor local.</summary>
    public Uri UrlTelegram { get; set; } = new("https://api.telegram.org/");

    /// <summary>Cada cuánto se mira si hay avisos que enviar.</summary>
    public TimeSpan IntervaloDeEnvio { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Cuántos avisos se toman de una vez.</summary>
    public int TamanoDeLote { get; set; } = 20;

    /// <summary>Cuánto se aparta un aviso mientras se envía: si el proceso muere a medias, vuelve a tocar pasado este tiempo.</summary>
    public TimeSpan Reserva { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Tiempo máximo de cada envío.</summary>
    public TimeSpan TiempoMaximoDeEnvio { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Cuánto se conservan los avisos ya enviados o abandonados.</summary>
    public TimeSpan Retencion { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>A quién hay que avisar de verdad: solo los destinos cuyo canal está configurado.</summary>
public sealed class DestinosDeAviso(Microsoft.Extensions.Options.IOptions<OpcionesDeAvisos> avisos, Microsoft.Extensions.Options.IOptions<OpcionesSmtp> correo)
{
    public IReadOnlyCollection<DestinoDeAviso> Lista { get; } = Calcular(avisos.Value, correo.Value);

    private static List<DestinoDeAviso> Calcular(OpcionesDeAvisos avisos, OpcionesSmtp correo)
    {
        var destinos = new List<DestinoDeAviso>();

        if (!string.IsNullOrWhiteSpace(correo.Servidor))
        {
            destinos.AddRange(Limpiar(avisos.Destinatarios).Select(d => new DestinoDeAviso(CanalAviso.Correo, d)));
        }

        if (!string.IsNullOrWhiteSpace(avisos.TokenTelegram))
        {
            destinos.AddRange(Limpiar(avisos.ChatsTelegram).Select(d => new DestinoDeAviso(CanalAviso.Telegram, d)));
        }

        return destinos;
    }

    private static IEnumerable<string> Limpiar(IEnumerable<string> valores) =>
        valores.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
}
