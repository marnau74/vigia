using System.Net.Http.Json;
using System.Text.Json;

using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Options;

using MimeKit;

using Vigia.Dominio.Avisos;

namespace Vigia.Worker.Avisos;

/// <summary>Un envío que falló: el mensaje es lo que se anota en el aviso y se enseña a quien administra el sistema.</summary>
public sealed class AvisoNoEnviadoException : Exception
{
    public AvisoNoEnviadoException()
    {
    }

    public AvisoNoEnviadoException(string mensaje)
        : base(mensaje)
    {
    }

    public AvisoNoEnviadoException(string mensaje, Exception interna)
        : base(mensaje, interna)
    {
    }
}

/// <summary>Un medio por el que se puede avisar. Lanza <see cref="AvisoNoEnviadoException"/> si no ha podido enviar.</summary>
public interface ICanalDeAviso
{
    CanalAviso Canal { get; }

    Task EnviarAsync(string destino, TextoDeAviso texto, CancellationToken cancellationToken);
}

/// <summary>Envía el aviso como un correo de texto plano por SMTP, con MailKit.</summary>
public sealed class CanalCorreo(IOptions<OpcionesSmtp> opciones) : ICanalDeAviso
{
    public CanalAviso Canal => CanalAviso.Correo;

    public async Task EnviarAsync(string destino, TextoDeAviso texto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texto);

        var ajustes = opciones.Value;

        if (string.IsNullOrWhiteSpace(ajustes.Servidor))
        {
            throw new AvisoNoEnviadoException("Falta Correo:Servidor.");
        }

        var correo = new MimeMessage();
        correo.From.Add(new MailboxAddress(ajustes.NombreRemitente, ajustes.Remitente));
        correo.Subject = texto.Asunto;
        correo.Body = new TextPart("plain") { Text = texto.Cuerpo };

        try
        {
            correo.To.Add(MailboxAddress.Parse(destino));

            using var cliente = new SmtpClient();
            await cliente.ConnectAsync(
                ajustes.Servidor,
                ajustes.Puerto,
                ajustes.UsarTls ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.None,
                cancellationToken);

            if (!string.IsNullOrEmpty(ajustes.Usuario))
            {
                await cliente.AuthenticateAsync(ajustes.Usuario, ajustes.Contrasena ?? string.Empty, cancellationToken);
            }

            await cliente.SendAsync(correo, cancellationToken);
            await cliente.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException and not AvisoNoEnviadoException)
        {
            throw new AvisoNoEnviadoException($"No se pudo enviar el correo: {excepcion.Message}", excepcion);
        }
    }
}

/// <summary>Envía el aviso como un mensaje del bot de Telegram (<c>sendMessage</c>).</summary>
/// <remarks>
/// El token del bot va en la dirección de la petición, así que ningún mensaje de error lo incluye: se
/// construye el mensaje a partir del estado HTTP y de la descripción que devuelve Telegram, nunca de la
/// dirección. El cliente HTTP es propio y sin reintentos automáticos: repetir un POST por la red enviaría
/// el mensaje dos veces; los reintentos los decide la bandeja de salida.
/// </remarks>
public sealed class CanalTelegram(HttpClient cliente, IOptions<OpcionesDeAvisos> opciones) : ICanalDeAviso
{
    /// <summary>Telegram rechaza los mensajes de más de 4096 caracteres.</summary>
    public const int LongitudMaxima = 4096;

    public CanalAviso Canal => CanalAviso.Telegram;

    public async Task EnviarAsync(string destino, TextoDeAviso texto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texto);

        var ajustes = opciones.Value;

        if (string.IsNullOrWhiteSpace(ajustes.TokenTelegram))
        {
            throw new AvisoNoEnviadoException("Falta Avisos:TokenTelegram.");
        }

        var mensaje = $"{texto.Asunto}\n\n{texto.Cuerpo}";
        // El token lleva dos puntos («123456:ABC…»): combinado como ruta relativa con «new Uri(base, ruta)» se leería
        // «bot123456» como un esquema. Se concatena como texto.
        var direccion = new Uri($"{ajustes.UrlTelegram.AbsoluteUri.TrimEnd('/')}/bot{ajustes.TokenTelegram}/sendMessage");

        HttpResponseMessage respuesta;

        try
        {
            respuesta = await cliente.PostAsJsonAsync(
                direccion,
                new { chat_id = destino, text = mensaje.Length > LongitudMaxima ? mensaje[..LongitudMaxima] : mensaje },
                cancellationToken);
        }
        catch (HttpRequestException excepcion)
        {
            // No se reenvía el mensaje de la excepción tal cual: por si alguna versión de .NET incluyera la dirección.
            throw new AvisoNoEnviadoException($"No se pudo contactar con Telegram ({excepcion.HttpRequestError}).");
        }

        using (respuesta)
        {
            if (respuesta.IsSuccessStatusCode)
            {
                return;
            }

            throw new AvisoNoEnviadoException($"Telegram respondió {(int)respuesta.StatusCode}: {await Descripcion(respuesta, cancellationToken)}");
        }
    }

    private static async Task<string> Descripcion(HttpResponseMessage respuesta, CancellationToken cancellationToken)
    {
        try
        {
            var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

            return cuerpo.TryGetProperty("description", out var descripcion) && descripcion.GetString() is { Length: > 0 } texto
                ? texto
                : respuesta.ReasonPhrase ?? "sin descripción";
        }
        catch (JsonException)
        {
            return respuesta.ReasonPhrase ?? "sin descripción";
        }
    }
}
