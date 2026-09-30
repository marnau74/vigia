using System.Globalization;
using System.Text;

using Vigia.Dominio.Seguimiento;

namespace Vigia.Dominio.Avisos;

/// <summary>El texto de un aviso, sin formato: sirve igual para un correo de texto plano que para un mensaje de Telegram.</summary>
public sealed record TextoDeAviso(string Asunto, string Cuerpo)
{
    /// <summary>
    /// Redacta el aviso a partir de lo que se sabe del incidente. Se hace al enviar, no al crear el aviso, para que
    /// un reintento tardío cuente la verdad (por ejemplo, cuánto duró ya la caída).
    /// </summary>
    public static TextoDeAviso De(TipoAviso tipo, string nombreDelMonitor, Incidente incidente)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nombreDelMonitor);
        ArgumentNullException.ThrowIfNull(incidente);

        var cuerpo = new StringBuilder();

        if (tipo == TipoAviso.Caida)
        {
            cuerpo.AppendLine(CultureInfo.InvariantCulture, $"«{nombreDelMonitor}» no responde.");
            cuerpo.AppendLine();
            cuerpo.AppendLine(CultureInfo.InvariantCulture, $"Caído desde: {Momento(incidente.AbiertoEn)}");
            cuerpo.AppendLine(CultureInfo.InvariantCulture, $"Fallos seguidos: {incidente.Fallos}");
            cuerpo.AppendLine(CultureInfo.InvariantCulture, $"Causa: {incidente.Causa}");

            return new TextoDeAviso($"[Vigía] «{nombreDelMonitor}» está caído", cuerpo.ToString().TrimEnd());
        }

        cuerpo.AppendLine(CultureInfo.InvariantCulture, $"«{nombreDelMonitor}» vuelve a responder.");
        cuerpo.AppendLine();
        cuerpo.AppendLine(CultureInfo.InvariantCulture, $"Caído desde: {Momento(incidente.AbiertoEn)}");

        if (incidente.CerradoEn is { } fin)
        {
            cuerpo.AppendLine(CultureInfo.InvariantCulture, $"Recuperado: {Momento(fin)}");
            cuerpo.AppendLine(CultureInfo.InvariantCulture, $"Duración de la caída: {Duracion(fin - incidente.AbiertoEn)}");
        }

        cuerpo.AppendLine(CultureInfo.InvariantCulture, $"Última causa: {incidente.Causa}");

        return new TextoDeAviso($"[Vigía] «{nombreDelMonitor}» se ha recuperado", cuerpo.ToString().TrimEnd());
    }

    /// <summary>Siempre en UTC y diciéndolo: un aviso que llega de madrugada no debe obligar a adivinar la zona horaria.</summary>
    private static string Momento(DateTimeOffset momento) => momento.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>La duración con las dos unidades más grandes, sin decimales («3 min 12 s», «2 h 5 min»).</summary>
    public static string Duracion(TimeSpan duracion)
    {
        if (duracion < TimeSpan.FromMinutes(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)duracion.TotalSeconds)} s");
        }

        if (duracion < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duracion.TotalMinutes} min {duracion.Seconds} s");
        }

        if (duracion < TimeSpan.FromDays(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duracion.TotalHours} h {duracion.Minutes} min");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)duracion.TotalDays} d {duracion.Hours} h");
    }
}
