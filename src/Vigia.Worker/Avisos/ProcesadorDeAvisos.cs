using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Vigia.Datos.Persistencia;
using Vigia.Dominio.Avisos;
using Vigia.Worker.Planificacion;

namespace Vigia.Worker.Avisos;

/// <summary>
/// Vacía la bandeja de salida: toma los avisos que toca enviar, los redacta con el estado actual del
/// incidente, los envía por su canal y anota el resultado. Un fallo no pierde el aviso: se reintenta
/// con esperas cada vez mayores y, si sigue fallando, se abandona a la vista de quien administre.
/// </summary>
/// <remarks>
/// La entrega es «al menos una vez»: el aviso se marca como enviado después de enviarlo, así que si el
/// proceso muere justo entre una cosa y la otra, el aviso se repite cuando vence su reserva. Es la
/// elección correcta para una alerta (mejor dos que ninguno) y ocurre solo en ese instante.
/// </remarks>
public sealed class ProcesadorDeAvisos(
    IServiceScopeFactory ambitos,
    IEnumerable<ICanalDeAviso> canales,
    MetricasVigia metricas,
    IOptions<OpcionesDeAvisos> opciones,
    TimeProvider reloj,
    ILogger<ProcesadorDeAvisos> log)
{
    private readonly Dictionary<CanalAviso, ICanalDeAviso> _canales = canales.ToDictionary(c => c.Canal);

    /// <summary>Envía todo lo que toca a esta hora y devuelve cuántos avisos se enviaron.</summary>
    public async Task<int> ProcesarAsync(CancellationToken cancellationToken)
    {
        var enviados = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var ambito = ambitos.CreateAsyncScope();
            var repositorio = new RepositorioAvisos(ambito.ServiceProvider.GetRequiredService<VigiaDbContext>());

            var lote = await repositorio.ReclamarAsync(reloj.GetUtcNow(), opciones.Value.Reserva, opciones.Value.TamanoDeLote, cancellationToken);

            if (lote.Count == 0)
            {
                break;
            }

            foreach (var aviso in lote)
            {
                if (await EnviarAsync(aviso, repositorio, cancellationToken))
                {
                    enviados++;
                }
            }
        }

        return enviados;
    }

    private async Task<bool> EnviarAsync(Aviso aviso, RepositorioAvisos repositorio, CancellationToken cancellationToken)
    {
        var canal = aviso.Canal.ToString();

        try
        {
            var contexto = await repositorio.CargarContextoAsync(aviso, cancellationToken)
                ?? throw new AvisoNoEnviadoException("El monitor o el incidente ya no existen.");

            if (!_canales.TryGetValue(aviso.Canal, out var medio))
            {
                throw new AvisoNoEnviadoException($"No hay canal de aviso «{canal}».");
            }

            using var limite = new CancellationTokenSource(opciones.Value.TiempoMaximoDeEnvio, reloj);
            using var enlazado = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limite.Token);

            try
            {
                await medio.EnviarAsync(aviso.Destino, TextoDeAviso.De(aviso.Tipo, contexto.NombreDelMonitor, contexto.Incidente), enlazado.Token);
            }
            catch (OperationCanceledException) when (limite.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new AvisoNoEnviadoException($"El envío tardó más de {opciones.Value.TiempoMaximoDeEnvio.TotalSeconds:0.##} s.");
            }

            aviso.MarcarEnviado(reloj.GetUtcNow());
            metricas.AvisoEnviado(canal);
            await repositorio.GuardarAsync(cancellationToken);

            return true;
        }
        catch (AvisoNoEnviadoException excepcion)
        {
            aviso.RegistrarFallo(excepcion.Message, reloj.GetUtcNow());
            metricas.AvisoFallido(canal);
            log.AvisoFallo(aviso.Id, canal, aviso.Intentos, aviso.Abandonado);

            if (aviso.Abandonado)
            {
                log.AvisoAbandonado(aviso.Id, canal);
            }

            await repositorio.GuardarAsync(cancellationToken);

            return false;
        }
    }
}

/// <summary>El bucle de fondo que llama al procesador de avisos cada pocos segundos.</summary>
public sealed class EnviadorDeAvisos(ProcesadorDeAvisos procesador, IOptions<OpcionesDeAvisos> opciones, TimeProvider reloj, ILogger<EnviadorDeAvisos> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(opciones.Value.IntervaloDeEnvio, reloj);

        try
        {
            do
            {
                try
                {
                    await procesador.ProcesarAsync(stoppingToken);
                }
                catch (Exception excepcion) when (excepcion is not OperationCanceledException)
                {
                    // Sin base de datos, por ejemplo: se vuelve a intentar en la siguiente vuelta.
                    log.EnvioDeAvisosFallo(excepcion);
                }
            }
            while (await temporizador.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Parada normal.
        }
    }
}
