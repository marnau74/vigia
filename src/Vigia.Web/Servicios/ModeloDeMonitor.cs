using System.Globalization;
using System.Text.Json;

using Vigia.Contratos;

namespace Vigia.Web.Servicios;

/// <summary>
/// Lo que se edita en el formulario de un monitor, con todos los campos de todos los tipos (solo se usan los del tipo elegido)
/// y la traducción de ida y vuelta a lo que entiende la API.
/// </summary>
public sealed class ModeloDeMonitor
{
    public static readonly string[] Tipos = ["Http", "Tls", "Dns", "Tcp", "Icmp"];

    public static readonly string[] RegistrosDns = ["A", "Aaaa", "Cname", "Mx", "Txt"];

    public string Nombre { get; set; } = string.Empty;

    public string Tipo { get; set; } = "Http";

    // HTTP
    public string Url { get; set; } = "https://";

    public string Metodo { get; set; } = "GET";

    public string PalabraClave { get; set; } = string.Empty;

    public string CodigosEsperados { get; set; } = "100-399";

    // TLS, TCP, ICMP
    public string Host { get; set; } = string.Empty;

    public int Puerto { get; set; } = 443;

    // DNS
    public string NombreDns { get; set; } = string.Empty;

    public string RegistroDns { get; set; } = "A";

    public string EsperadosDns { get; set; } = string.Empty;

    public string ServidorDns { get; set; } = string.Empty;

    // Comunes
    public int IntervaloSegundos { get; set; } = 60;

    public int TiempoMaximoSegundos { get; set; } = 10;

    public int FallosParaIncidente { get; set; } = 3;

    public int? UmbralLentoMs { get; set; }

    public Guid? GrupoId { get; set; }

    public bool PermitirRedPrivada { get; set; }

    public bool VerificarCertificado { get; set; } = true;

    /// <summary>La configuración del tipo elegido, con el mismo JSON que espera la API.</summary>
    public JsonElement Configuracion()
    {
        var datos = new Dictionary<string, object?>
        {
            ["tipo"] = Tipo,
            ["tiempoMaximo"] = TimeSpan.FromSeconds(TiempoMaximoSegundos).ToString("c", CultureInfo.InvariantCulture),
        };

        if (PermitirRedPrivada)
        {
            datos["permitirRedPrivada"] = true;
        }

        switch (Tipo)
        {
            case "Http":
                datos["url"] = Url.Trim();
                datos["metodo"] = Metodo;
                datos["codigosEsperados"] = CodigosEsperados.Trim();
                datos["verificarCertificado"] = VerificarCertificado;

                if (!string.IsNullOrWhiteSpace(PalabraClave))
                {
                    datos["palabraClave"] = PalabraClave.Trim();
                }

                break;
            case "Tls":
                datos["host"] = Host.Trim();
                datos["puerto"] = Puerto;
                datos["verificarCertificado"] = VerificarCertificado;
                break;
            case "Dns":
                datos["nombre"] = NombreDns.Trim();
                datos["registro"] = RegistroDns;

                if (!string.IsNullOrWhiteSpace(EsperadosDns))
                {
                    datos["esperados"] = EsperadosDns.Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }

                if (!string.IsNullOrWhiteSpace(ServidorDns))
                {
                    datos["servidor"] = ServidorDns.Trim();
                }

                break;
            case "Tcp":
                datos["host"] = Host.Trim();
                datos["puerto"] = Puerto;
                break;
            default:
                datos["host"] = Host.Trim();
                break;
        }

        return JsonSerializer.SerializeToElement(datos);
    }

    public CuerpoMonitor Cuerpo() => new(Nombre.Trim(), Configuracion(), IntervaloSegundos, FallosParaIncidente, UmbralLentoMs, GrupoId);

    /// <summary>Rellena el formulario con un monitor existente (para editarlo).</summary>
    public static ModeloDeMonitor De(MonitorDto monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        var c = monitor.Configuracion;

        string Texto(string nombre, string porDefecto = "") => c.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? porDefecto : porDefecto;
        int Numero(string nombre, int porDefecto) => c.TryGetProperty(nombre, out var v) && v.TryGetInt32(out var n) ? n : porDefecto;
        bool Booleano(string nombre, bool porDefecto) => c.TryGetProperty(nombre, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : porDefecto;

        var tiempoMaximo = TimeSpan.TryParse(Texto("tiempoMaximo", "00:00:10"), CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.FromSeconds(10);
        var esperados = c.TryGetProperty("esperados", out var lista) && lista.ValueKind == JsonValueKind.Array ? string.Join(", ", lista.EnumerateArray().Select(e => e.GetString())) : string.Empty;

        return new ModeloDeMonitor
        {
            Nombre = monitor.Nombre,
            Tipo = monitor.Tipo,
            Url = Texto("url", "https://"),
            Metodo = Texto("metodo", "GET"),
            PalabraClave = Texto("palabraClave"),
            CodigosEsperados = Texto("codigosEsperados", "100-399"),
            Host = Texto("host"),
            Puerto = Numero("puerto", 443),
            NombreDns = Texto("nombre"),
            RegistroDns = Texto("registro", "A") is { Length: > 0 } r ? char.ToUpperInvariant(r[0]) + r[1..] : "A",
            EsperadosDns = esperados,
            ServidorDns = Texto("servidor"),
            IntervaloSegundos = monitor.IntervaloSegundos,
            TiempoMaximoSegundos = (int)Math.Round(tiempoMaximo.TotalSeconds),
            FallosParaIncidente = monitor.FallosParaIncidente,
            UmbralLentoMs = monitor.UmbralLentoMs,
            GrupoId = monitor.GrupoId,
            PermitirRedPrivada = Booleano("permitirRedPrivada", false),
            VerificarCertificado = Booleano("verificarCertificado", true),
        };
    }
}
