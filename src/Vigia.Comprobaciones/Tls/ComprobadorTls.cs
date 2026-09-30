using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Vigia.Comprobaciones.Red;
using Vigia.Dominio.Monitores;

namespace Vigia.Comprobaciones.Tls;

/// <summary>
/// El certificado TLS de un servidor es válido y le quedan días. Hace la negociación TLS a mano
/// (<see cref="SslStream"/>) para poder leer el certificado aunque sea inválido: un cliente HTTP
/// normal se negaría a hablar y solo diría «error de TLS», sin decir que caducó hace tres días.
/// </summary>
/// <remarks>
/// Los avisos a los 21 y 7 días no son fallos de esta comprobación: la comprobación devuelve los días
/// restantes en <c>dias_restantes</c> y quien avisa (el worker) decide con esos umbrales.
/// </remarks>
public sealed class ComprobadorTls(GuardiaDeDestinos guardia, IConector conector, TimeProvider reloj) : Comprobador<ConfiguracionTls>(reloj)
{
    public override TipoMonitor Tipo => TipoMonitor.Tls;

    protected override async Task<ResultadoComprobacion> EjecutarAsync(ConfiguracionTls configuracion, Cronometro cronometro, CancellationToken cancellationToken)
    {
        var direcciones = await guardia.ResolverPermitidasAsync(configuracion.Host, configuracion.PermitirRedPrivada, cancellationToken);

        X509Certificate2? certificado = null;
        var errores = SslPolicyErrors.None;
        var errorDeCadena = false;
        string protocolo = string.Empty;
        Exception? ultimoError = null;

        foreach (var direccion in direcciones)
        {
            try
            {
                using var socket = await conector.ConectarAsync(direccion, configuracion.Puerto, cancellationToken);
                await using var red = new System.Net.Sockets.NetworkStream(socket, ownsSocket: false);
                await using var ssl = new SslStream(red, leaveInnerStreamOpen: false, (_, cert, cadena, politica) =>
                {
                    // Siempre se acepta: lo que se hace con los errores se decide después de leer el certificado.
                    certificado = cert is null ? null : new X509Certificate2(cert);
                    errores = politica;
                    errorDeCadena = cadena?.ChainStatus.Any(s => s.Status != X509ChainStatusFlags.NotTimeValid) ?? false;
                    return true;
                });

                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = configuracion.Host }, cancellationToken);
                protocolo = ssl.SslProtocol.ToString();
                ultimoError = null;
                break;
            }
            catch (System.Net.Sockets.SocketException excepcion)
            {
                ultimoError = excepcion;
            }
        }

        if (ultimoError is not null)
        {
            throw ultimoError;
        }

        if (certificado is null)
        {
            return ResultadoComprobacion.Fallido(TipoFallo.Tls, "El servidor no presentó ningún certificado.", cronometro.Transcurrido);
        }

        using (certificado)
        {
            return Evaluar(configuracion, certificado, errores, errorDeCadena, protocolo, cronometro.Transcurrido);
        }
    }

    private ResultadoComprobacion Evaluar(
        ConfiguracionTls configuracion,
        X509Certificate2 certificado,
        SslPolicyErrors errores,
        bool errorDeCadena,
        string protocolo,
        TimeSpan latencia)
    {
        // Los días se cuentan con el reloj inyectado, no con el del sistema operativo: así los tests
        // simulan «faltan 6 días» sin certificados hechos a medida para cada caso.
        var ahora = Reloj.GetUtcNow();
        var caduca = new DateTimeOffset(certificado.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var empieza = new DateTimeOffset(certificado.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        // Se trunca hacia cero: «29 días y 23 horas» son 29 días, y un certificado caducado hace tres días y una hora son -3.
        var diasRestantes = (int)Math.Truncate((caduca - ahora).TotalDays);

        var detalles = new Dictionary<string, string>
        {
            ["asunto"] = certificado.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            ["emisor"] = certificado.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
            ["caduca"] = caduca.ToString("O", CultureInfo.InvariantCulture),
            ["dias_restantes"] = diasRestantes.ToString(CultureInfo.InvariantCulture),
            ["protocolo"] = protocolo,
        };

        // Un certificado caducado o que aún no vale es un fallo siempre, aunque se haya pedido no verificar la cadena.
        if (ahora >= caduca)
        {
            return ResultadoComprobacion.Fallido(TipoFallo.Tls, $"El certificado caducó el {caduca:yyyy-MM-dd}.", latencia, detalles);
        }

        if (ahora < empieza)
        {
            return ResultadoComprobacion.Fallido(TipoFallo.Tls, $"El certificado no es válido hasta el {empieza:yyyy-MM-dd}.", latencia, detalles);
        }

        if (configuracion.VerificarCertificado)
        {
            if (errores.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                return ResultadoComprobacion.Fallido(TipoFallo.Tls, $"El certificado no es válido para «{configuracion.Host}».", latencia, detalles);
            }

            if (errorDeCadena || errores.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            {
                return ResultadoComprobacion.Fallido(TipoFallo.Tls, "La cadena del certificado no es de confianza (autofirmado, emisor desconocido o incompleta).", latencia, detalles);
            }
        }

        return ResultadoComprobacion.Exito(latencia, detalles);
    }
}
