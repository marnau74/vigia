using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Vigia.Dominio.Monitores;

namespace Vigia.Comprobaciones.Http;

/// <summary>
/// Una URL responde con el código esperado (y, si se pide, con cierta palabra) dentro del tiempo
/// máximo. Sigue las redirecciones a mano, hasta un máximo, porque cada salto es un destino nuevo y
/// tiene que pasar por la guardia SSRF como el primero (la conexión la valida el manejador, ver
/// <see cref="ClientesHttp"/>).
/// </summary>
public sealed class ComprobadorHttp(IHttpClientFactory fabrica, TimeProvider reloj) : Comprobador<ConfiguracionHttp>(reloj)
{
    /// <summary>Cuánto de la respuesta se lee al buscar una palabra: no se descarga un vídeo de 2 GB.</summary>
    public const int MaximoBytesLeidos = 1024 * 1024;

    public override TipoMonitor Tipo => TipoMonitor.Http;

    protected override async Task<ResultadoComprobacion> EjecutarAsync(ConfiguracionHttp configuracion, Cronometro cronometro, CancellationToken cancellationToken)
    {
        if (!EsUrlPermitida(configuracion.Url, out var motivo))
        {
            return ResultadoComprobacion.Fallido(TipoFallo.DestinoBloqueado, motivo, cronometro.Transcurrido);
        }

        // Buscar una palabra exige el cuerpo: con HEAD no lo hay.
        var metodo = configuracion.PalabraClave is null ? new HttpMethod(configuracion.Metodo) : HttpMethod.Get;

        using var cliente = fabrica.CreateClient(ClientesHttp.Nombre(configuracion.PermitirRedPrivada, configuracion.VerificarCertificado));
        var medidas = new MedidasDeConexion();
        var direccion = configuracion.Url;
        var saltos = 0;

        while (true)
        {
            using var peticion = new HttpRequestMessage(metodo, direccion);
            peticion.Headers.UserAgent.Add(new ProductInfoHeaderValue("Vigia", "1.0"));
            peticion.Options.Set(ClientesHttp.ClaveMedidas, medidas);

            var inicioPeticion = Reloj.GetTimestamp();
            using var respuesta = await cliente.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            medidas.PrimerByte = Reloj.GetElapsedTime(inicioPeticion);

            if (EsRedireccion(respuesta.StatusCode) && respuesta.Headers.Location is { } destino)
            {
                if (saltos >= configuracion.MaxRedirecciones)
                {
                    return ResultadoComprobacion.Fallido(TipoFallo.Otro, $"Más de {configuracion.MaxRedirecciones} redirecciones seguidas.", cronometro.Transcurrido, Detalles(respuesta, direccion, saltos, medidas));
                }

                direccion = new Uri(direccion, destino);
                saltos++;

                // Una redirección a otro esquema (file:, ftp:, gopher:) es un clásico para salirse de la lista.
                if (!EsUrlPermitida(direccion, out motivo))
                {
                    return ResultadoComprobacion.Fallido(TipoFallo.DestinoBloqueado, $"Redirección no permitida: {motivo}", cronometro.Transcurrido, Detalles(respuesta, direccion, saltos, medidas));
                }

                continue;
            }

            var detalles = Detalles(respuesta, direccion, saltos, medidas);
            var codigo = (int)respuesta.StatusCode;

            if (!configuracion.CodigosEsperados.Contiene(codigo))
            {
                return ResultadoComprobacion.Fallido(
                    TipoFallo.CodigoInesperado,
                    $"Respuesta HTTP {codigo} {respuesta.ReasonPhrase}; se esperaba {configuracion.CodigosEsperados.Texto}.".TrimEnd(),
                    cronometro.Transcurrido,
                    detalles);
            }

            if (configuracion.PalabraClave is { Length: > 0 } palabra && !await ContienePalabraAsync(respuesta, palabra, cancellationToken))
            {
                return ResultadoComprobacion.Fallido(
                    TipoFallo.PalabraClaveAusente,
                    $"La respuesta no contiene «{palabra}» en su primer megabyte.",
                    cronometro.Transcurrido,
                    detalles);
            }

            return ResultadoComprobacion.Exito(cronometro.Transcurrido, detalles);
        }
    }

    private static bool EsUrlPermitida(Uri url, out string motivo)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
        {
            motivo = $"Solo se permiten direcciones http y https, no «{url.Scheme}».";
            return false;
        }

        // http://usuario:clave@host/: llevaría las credenciales en cada petición y en los registros.
        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            motivo = "La dirección no puede llevar usuario ni contraseña.";
            return false;
        }

        motivo = string.Empty;
        return true;
    }

    private static bool EsRedireccion(HttpStatusCode codigo) =>
        codigo is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<bool> ContienePalabraAsync(HttpResponseMessage respuesta, string palabra, CancellationToken cancellationToken)
    {
        await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);

        // Un descodificador con estado, no uno por bloque: una letra de varios bytes («ñ», «é» en UTF-8) puede quedar
        // partida entre dos lecturas, y descodificar cada mitad por separado la convertiría en dos caracteres basura.
        var codificacion = Codificacion(respuesta);
        var descodificador = codificacion.GetDecoder();
        var buffer = new byte[16 * 1024];
        var caracteres = new char[codificacion.GetMaxCharCount(buffer.Length)];
        var acumulado = new StringBuilder();
        var leidos = 0;

        while (leidos < MaximoBytesLeidos)
        {
            var n = await flujo.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaximoBytesLeidos - leidos)), cancellationToken);
            if (n == 0)
            {
                break;
            }

            leidos += n;
            var descodificados = descodificador.GetChars(buffer, 0, n, caracteres, 0, flush: false);
            acumulado.Append(caracteres, 0, descodificados);

            if (acumulado.ToString().Contains(palabra, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Se conserva el final por si la palabra queda partida entre dos bloques.
            if (acumulado.Length >= palabra.Length)
            {
                acumulado.Remove(0, acumulado.Length - palabra.Length + 1);
            }
        }

        return false;
    }

    /// <summary>La codificación que declara la respuesta (<c>charset</c>); si no declara ninguna o no se conoce, UTF-8.</summary>
    private static Encoding Codificacion(HttpResponseMessage respuesta)
    {
        var declarada = respuesta.Content.Headers.ContentType?.CharSet?.Trim('"', ' ');

        if (string.IsNullOrEmpty(declarada))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(declarada);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static Dictionary<string, string> Detalles(HttpResponseMessage respuesta, Uri direccionFinal, int redirecciones, MedidasDeConexion medidas)
    {
        var detalles = new Dictionary<string, string>
        {
            ["codigo"] = ((int)respuesta.StatusCode).ToString(CultureInfo.InvariantCulture),
            ["url_final"] = direccionFinal.GetLeftPart(UriPartial.Path),
            ["redirecciones"] = redirecciones.ToString(CultureInfo.InvariantCulture),
            ["primer_byte_ms"] = Milisegundos(medidas.PrimerByte),
        };

        if (medidas.Dns is { } dns)
        {
            detalles["dns_ms"] = Milisegundos(dns);
        }

        if (medidas.Conexion is { } conexion)
        {
            detalles["conexion_ms"] = Milisegundos(conexion);
        }

        return detalles;
    }

    private static string Milisegundos(TimeSpan tiempo) => tiempo.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
}
