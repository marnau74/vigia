using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Vigia.Contratos;

namespace Vigia.Web.Servicios;

public sealed class OpcionesApi
{
    public const string Seccion = "Api";

    /// <summary>Dónde está la API. Con Aspire es «https+http://api» (descubrimiento de servicios); en producción, su dirección interna.</summary>
    public Uri Url { get; set; } = new("http://localhost:5061/");
}

/// <summary>Lo que el panel recibe en vivo de la API.</summary>
public interface IConexionEnVivo : IAsyncDisposable
{
    /// <summary>Una comprobación guardada por el worker.</summary>
    event Func<ComprobacionEnVivo, Task>? Recibida;

    /// <summary>La conexión se restableció tras un corte: lo que se haya podido perder hay que volver a pedirlo.</summary>
    event Func<Task>? Reconectada;

    /// <summary>Cambios en el estado de la conexión (true = «En vivo», false = «Reconectando…»).</summary>
    event Func<bool, Task>? EstadoCambiado;

    bool Conectada { get; }

    Task IniciarAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// La conexión SignalR de un panel abierto con la API. Se abre al entrar en una página en vivo y se cierra con el circuito.
/// Se reconecta sola y avisa al volver, para que la página recargue lo que no vio.
/// </summary>
public sealed class ConexionEnVivo(
    IOptions<OpcionesApi> api,
    AuthenticationStateProvider estado,
    [FromKeyedServices("manejador-tiempo-real")] Func<HttpMessageHandler>? manejador = null) : IConexionEnVivo
{
    private HubConnection? _conexion;

    public event Func<ComprobacionEnVivo, Task>? Recibida;

    public event Func<Task>? Reconectada;

    public event Func<bool, Task>? EstadoCambiado;

    public bool Conectada => _conexion?.State == HubConnectionState.Connected;

    public async Task IniciarAsync(CancellationToken cancellationToken = default)
    {
        if (_conexion is not null)
        {
            return;
        }

        _conexion = new HubConnectionBuilder()
            .WithUrl(
                new Uri(api.Value.Url, "hubs/panel"),
                opciones =>
                {
                    opciones.AccessTokenProvider = async () => (await estado.GetAuthenticationStateAsync()).User.FindFirst(ClienteDeApi.ClaimDelToken)?.Value;

                    if (manejador is not null)
                    {
                        opciones.HttpMessageHandlerFactory = _ => manejador();
                        opciones.Transports = HttpTransportType.LongPolling;
                    }
                })
            .AddJsonProtocol(protocolo => protocolo.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .WithAutomaticReconnect()
            .Build();

        _conexion.On<ComprobacionEnVivo>("MonitorActualizado", async mensaje =>
        {
            if (Recibida is { } alRecibir)
            {
                await alRecibir(mensaje);
            }
        });

        _conexion.Reconnecting += _ => Avisar(false);
        _conexion.Reconnected += async _ =>
        {
            await Avisar(true);

            if (Reconectada is { } alReconectar)
            {
                await alReconectar();
            }
        };
        _conexion.Closed += _ => Avisar(false);

        try
        {
            await _conexion.StartAsync(cancellationToken);
            await Avisar(true);
        }
        catch (Exception excepcion) when (excepcion is HttpRequestException or InvalidOperationException or TimeoutException)
        {
            // Sin tiempo real el panel funciona igual, solo que sin actualizarse solo.
            await Avisar(false);
        }
    }

    private Task Avisar(bool conectada) => EstadoCambiado is { } alCambiar ? alCambiar(conectada) : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_conexion is not null)
        {
            await _conexion.DisposeAsync();
        }
    }
}
