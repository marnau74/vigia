using System.Text.Json;

using Npgsql;

using Vigia.Dominio.Monitores;

namespace Vigia.Datos.Persistencia;

/// <summary>Lo que el worker cuenta a la API cada vez que guarda una comprobación, para el panel en tiempo real.</summary>
/// <param name="MonitorId">El monitor comprobado.</param>
/// <param name="Momento">Cuándo se hizo.</param>
/// <param name="Correcto">Si salió bien.</param>
/// <param name="LatenciaMs">Cuánto tardó.</param>
/// <param name="Estado">El estado del monitor tras la comprobación.</param>
/// <param name="Incidente">«abierto» o «cerrado» si la comprobación abrió o cerró un incidente; vacío si no.</param>
public sealed record ComprobacionPublicada(Guid MonitorId, DateTimeOffset Momento, bool Correcto, int LatenciaMs, EstadoMonitor Estado, string? Incidente)
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web);

    public string Serializar() => JsonSerializer.Serialize(this, Opciones);

    /// <summary>Lee una carga recibida. Una carga que no se entiende (de una versión distinta, por ejemplo) devuelve <c>null</c> en lugar de romper el oyente.</summary>
    public static ComprobacionPublicada? Leer(string carga)
    {
        try
        {
            return JsonSerializer.Deserialize<ComprobacionPublicada>(carga, Opciones);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Escucha un canal de <c>LISTEN/NOTIFY</c> con una conexión propia y la reabre sola si se cae. Un aviso
/// que llega mientras la conexión está caída no se puede recuperar, así que quien escucha debe poder
/// ponerse al día por otra vía: <paramref name="alConectar"/> se llama cada vez que la conexión queda
/// lista (la primera y tras cada reconexión) para que recargue lo que se haya podido perder.
/// </summary>
public static class EscuchaDeNotificaciones
{
    public static readonly TimeSpan EsperaTrasFallo = TimeSpan.FromSeconds(5);

    public static async Task EscucharAsync(
        string cadenaDeConexion,
        string canal,
        Action<string> alRecibir,
        Action alConectar,
        Action<Exception> alFallar,
        TimeProvider reloj,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cadenaDeConexion);
        ArgumentException.ThrowIfNullOrWhiteSpace(canal);
        ArgumentNullException.ThrowIfNull(alRecibir);
        ArgumentNullException.ThrowIfNull(alConectar);
        ArgumentNullException.ThrowIfNull(alFallar);
        ArgumentNullException.ThrowIfNull(reloj);

        // El nombre del canal lo pone este código (constantes), nunca una persona: un identificador no se puede pasar como parámetro.
        var orden = $"LISTEN {canal}";

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var conexion = new NpgsqlConnection(cadenaDeConexion);
                await conexion.OpenAsync(cancellationToken);
                conexion.Notification += (_, evento) => alRecibir(evento.Payload);

#pragma warning disable CA2100 // Sin datos de usuario: es una constante.
                await using (var comando = new NpgsqlCommand(orden, conexion))
                {
                    await comando.ExecuteNonQueryAsync(cancellationToken);
                }
#pragma warning restore CA2100

                alConectar();

                while (!cancellationToken.IsCancellationRequested)
                {
                    await conexion.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception excepcion)
            {
                alFallar(excepcion);

                try
                {
                    await Task.Delay(EsperaTrasFallo, reloj, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
