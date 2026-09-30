using System.Globalization;

using Vigia.Contratos;
using Vigia.Dominio.Avisos;
using Vigia.Dominio.Monitores;

namespace Vigia.Web.Servicios;

/// <summary>
/// Cómo se escriben los números, las fechas y los estados. Todo a mano y con formato fijo (coma decimal, horas en
/// UTC): no depende de la cultura del servidor, que en un contenedor suele ser la invariante.
/// </summary>
public static class Formato
{
    private static readonly NumberFormatInfo Coma = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

    private static readonly string[] Meses = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];

    /// <summary>«99,95 %» o «100 %»; sin datos, una raya (nunca un 100 % inventado).</summary>
    public static string Porcentaje(decimal? valor) => valor is { } v ? $"{v.ToString("0.##", Coma)} %" : "—";

    public static string Latencia(int? milisegundos) => milisegundos switch
    {
        null => "—",
        < 1000 => $"{milisegundos} ms",
        _ => $"{(milisegundos.Value / 1000.0).ToString("0.##", Coma)} s",
    };

    public static string Latencia(double? milisegundos) => milisegundos is { } ms ? Latencia((int)Math.Round(ms)) : "—";

    /// <summary>«15 oct 2026, 10:30 UTC»: siempre en UTC y diciéndolo.</summary>
    public static string Fecha(DateTimeOffset? momento)
    {
        if (momento is not { } m)
        {
            return "—";
        }

        var utc = m.UtcDateTime;

        return string.Create(CultureInfo.InvariantCulture, $"{utc.Day} {Meses[utc.Month - 1]} {utc.Year}, {utc:HH:mm} UTC");
    }

    public static string Dia(DateOnly dia) => string.Create(CultureInfo.InvariantCulture, $"{dia.Day} {Meses[dia.Month - 1]} {dia.Year}");

    public static string Hora(DateTimeOffset momento) => momento.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string Duracion(double segundos) => TextoDeAviso.Duracion(TimeSpan.FromSeconds(segundos));

    /// <summary>«hace 3 min», «hace 2 h»…</summary>
    public static string Hace(DateTimeOffset? momento, DateTimeOffset ahora)
    {
        if (momento is not { } m)
        {
            return "nunca";
        }

        var diferencia = ahora - m;

        return diferencia.TotalSeconds switch
        {
            < 5 => "ahora mismo",
            < 60 => $"hace {(int)diferencia.TotalSeconds} s",
            < 3600 => $"hace {(int)diferencia.TotalMinutes} min",
            < 86400 => $"hace {(int)diferencia.TotalHours} h",
            _ => $"hace {(int)diferencia.TotalDays} d",
        };
    }
}

/// <summary>Los textos de los estados. El estado nunca se indica solo con color: siempre lleva una palabra y una forma.</summary>
public static class Textos
{
    public static string Estado(EstadoMonitor estado) => estado switch
    {
        EstadoMonitor.Operativo => "Operativo",
        EstadoMonitor.Degradado => "Lento",
        EstadoMonitor.Sospechoso => "Comprobando",
        EstadoMonitor.Caido => "Caído",
        EstadoMonitor.Mantenimiento => "Mantenimiento",
        _ => "Sin datos",
    };

    /// <summary>La clase CSS del estado (el color); la forma y el texto van aparte.</summary>
    public static string Clase(EstadoMonitor estado) => estado switch
    {
        EstadoMonitor.Operativo => "ok",
        EstadoMonitor.Degradado or EstadoMonitor.Sospechoso => "aviso",
        EstadoMonitor.Caido => "caido",
        EstadoMonitor.Mantenimiento => "mantenimiento",
        _ => "desconocido",
    };

    public static string General(EstadoGeneral general) => general switch
    {
        EstadoGeneral.Operativo => "Todos los sistemas operativos",
        EstadoGeneral.Degradado => "Algunos servicios van lentos",
        EstadoGeneral.IncidenteParcial => "Hay servicios caídos",
        EstadoGeneral.Caido => "Todos los servicios están caídos",
        EstadoGeneral.Mantenimiento => "Mantenimiento programado",
        _ => "Sin datos todavía",
    };

    public static EstadoMonitor Representante(EstadoGeneral general) => general switch
    {
        EstadoGeneral.Operativo => EstadoMonitor.Operativo,
        EstadoGeneral.Degradado => EstadoMonitor.Degradado,
        EstadoGeneral.IncidenteParcial or EstadoGeneral.Caido => EstadoMonitor.Caido,
        EstadoGeneral.Mantenimiento => EstadoMonitor.Mantenimiento,
        _ => EstadoMonitor.Desconocido,
    };

    public static string Tipo(string tipo) => tipo switch
    {
        "Http" => "Web (HTTP)",
        "Tls" => "Certificado TLS",
        "Dns" => "DNS",
        "Tcp" => "Puerto TCP",
        "Icmp" => "Ping",
        _ => tipo,
    };

    public static string Destino(MonitorDto monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        var c = monitor.Configuracion;

        string? Texto(string nombre) => c.ValueKind == System.Text.Json.JsonValueKind.Object && c.TryGetProperty(nombre, out var valor) ? valor.ToString() : null;

        return monitor.Tipo switch
        {
            "Http" => Texto("url") ?? string.Empty,
            "Dns" => $"{Texto("nombre")} ({Texto("registro")})",
            "Tcp" => $"{Texto("host")}:{Texto("puerto")}",
            _ => Texto("host") ?? string.Empty,
        };
    }
}
