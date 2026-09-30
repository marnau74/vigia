using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Vigia.Api.Seguridad;

/// <summary>
/// Cómo se entra al panel, en la sección <c>Acceso</c> de la configuración. Vigía tiene un único usuario
/// (quien lo administra), así que no hay tabla de usuarios: una contraseña con su hash en la configuración.
/// </summary>
public sealed class OpcionesAcceso
{
    public const string Seccion = "Acceso";

    public const string Audiencia = "vigia-panel";

    /// <summary>Un hash de la contraseña (nunca la contraseña), generado con <c>dotnet run --project src/Vigia.Api -- hash-contrasena</c>. Sin él, no se puede entrar.</summary>
    public string? HashContrasena { get; set; }

    /// <summary>La clave con la que se firman los tokens: al menos 32 caracteres, por variable de entorno o gestor de secretos.</summary>
    public string? ClaveJwt { get; set; }

    public string Emisor { get; set; } = "vigia";

    /// <summary>Cuánto dura una sesión. Al vencer hay que volver a entrar.</summary>
    public TimeSpan Validez { get; set; } = TimeSpan.FromHours(1);

    public const int LongitudMinimaDeLaClave = 32;
}

/// <summary>El hash de la contraseña, con el algoritmo de ASP.NET Core Identity (PBKDF2 con sal y muchas vueltas).</summary>
public static class Contrasenas
{
    private static readonly PasswordHasher<string> Hasher = new();

    // El nombre de usuario no interviene en el hash; se pasa uno fijo porque la API lo pide.
    private const string Usuario = "admin";

    public static string Hash(string contrasena)
    {
        ArgumentException.ThrowIfNullOrEmpty(contrasena);

        return Hasher.HashPassword(Usuario, contrasena);
    }

    public static bool Verificar(string hash, string contrasena)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(contrasena))
        {
            return false;
        }

        try
        {
            return Hasher.VerifyHashedPassword(Usuario, hash, contrasena) != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            // Un hash mal copiado a la configuración no debe tumbar el servidor: simplemente no deja entrar.
            return false;
        }
    }
}

/// <summary>Emite los tokens de sesión (JWT firmados con HMAC-SHA256) y sabe cómo validarlos.</summary>
public sealed class EmisorDeTokens(IOptions<OpcionesAcceso> opciones, TimeProvider reloj)
{
    public (string Token, DateTimeOffset ExpiraEn) Emitir()
    {
        var ajustes = opciones.Value;
        var ahora = reloj.GetUtcNow();
        var expira = ahora + ajustes.Validez;

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, "admin"), new Claim(ClaimTypes.Role, "admin")]),
            Issuer = ajustes.Emisor,
            Audience = OpcionesAcceso.Audiencia,
            IssuedAt = ahora.UtcDateTime,
            NotBefore = ahora.UtcDateTime,
            Expires = expira.UtcDateTime,
            SigningCredentials = new SigningCredentials(Clave(ajustes), SecurityAlgorithms.HmacSha256),
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expira);
    }

    public static TokenValidationParameters Parametros(OpcionesAcceso ajustes, TimeProvider reloj)
    {
        ArgumentNullException.ThrowIfNull(ajustes);
        ArgumentNullException.ThrowIfNull(reloj);

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = ajustes.Emisor,
            ValidateAudience = true,
            ValidAudience = OpcionesAcceso.Audiencia,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = Clave(ajustes),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,

            // Solo HMAC-SHA256: así un token sin firma o con otro algoritmo no pasa.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),

            // La vigencia se comprueba con el reloj inyectado, no con el del sistema: los tests avanzan el tiempo en lugar de esperar.
            LifetimeValidator = (desde, hasta, _, parametros) =>
            {
                var ahora = reloj.GetUtcNow().UtcDateTime;

                return hasta is not null
                    && ahora <= hasta.Value + parametros.ClockSkew
                    && (desde is null || ahora >= desde.Value - parametros.ClockSkew);
            },
        };
    }

    private static SymmetricSecurityKey Clave(OpcionesAcceso ajustes) =>
        new(Encoding.UTF8.GetBytes(ajustes.ClaveJwt ?? throw new InvalidOperationException("Falta Acceso:ClaveJwt.")));
}
