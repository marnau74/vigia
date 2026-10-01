using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Vigia.Datos.Persistencia;
using Vigia.Dominio.Mantenimiento;

namespace Vigia.Worker.Planificacion;

/// <summary>
/// Reparte las comprobaciones en el tiempo. Un único bucle mira la cola de vencimientos, duerme hasta el
/// siguiente y entrega los monitores vencidos a un grupo fijo de trabajadores a través de un canal con
/// límite. Así el número de comprobaciones simultáneas está acotado por <see cref="OpcionesPlanificador.Concurrencia"/>
/// pase lo que pase (con 5000 monitores no se abren 5000 sockets), y si los trabajadores van más lentos que
/// el ritmo de entrada, el canal lleno frena el bucle en lugar de acumular memoria.
/// </summary>
public sealed class Planificador(
    IServiceScopeFactory ambitos,
    ColaDeVencimientos cola,
    EjecutorDeMonitor ejecutor,
    IOptions<OpcionesPlanificador> opciones,
    TimeProvider reloj,
    ILogger<Planificador> log) : BackgroundService
{
    private readonly SemaphoreSlim _despertar = new(0);
    private volatile bool _recargar = true;
    private volatile IReadOnlyList<VentanaMantenimiento> _ventanas = [];

    /// <summary>Pide releer los monitores (lo llama la escucha de LISTEN/NOTIFY).</summary>
    public void Recargar()
    {
        _recargar = true;
        _despertar.Release();
    }

    public bool EstaOcioso => cola.EstaOcioso(reloj.GetUtcNow());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrencia = Math.Max(1, opciones.Value.Concurrencia);
        var canal = Channel.CreateBounded<Vencimiento>(new BoundedChannelOptions(concurrencia) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });
        var trabajadores = Enumerable.Range(0, concurrencia).Select(_ => Task.Run(() => TrabajarAsync(canal.Reader, stoppingToken), CancellationToken.None)).ToArray();
        var ultimaRecarga = DateTimeOffset.MinValue;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var ahora = reloj.GetUtcNow();

                if (_recargar || ahora - ultimaRecarga >= opciones.Value.RecargaCada)
                {
                    _recargar = false;
                    ultimaRecarga = ahora;
                    await RecargarAsync(ahora, stoppingToken);
                }

                foreach (var vencimiento in cola.TomarVencidos(ahora, int.MaxValue))
                {
                    await canal.Writer.WriteAsync(vencimiento, stoppingToken);
                }

                await DormirAsync(ahora, ultimaRecarga, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Parada normal.
        }
        finally
        {
            canal.Writer.TryComplete();
            await Task.WhenAll(trabajadores);
        }
    }

    private async Task DormirAsync(DateTimeOffset ahora, DateTimeOffset ultimaRecarga, CancellationToken cancellationToken)
    {
        ahora = reloj.GetUtcNow(); // El reloj pudo avanzar mientras se entregaban los vencidos.
        var siguiente = cola.ProximoVencimiento();
        var proximaRecarga = ultimaRecarga + opciones.Value.RecargaCada;
        var despertar = siguiente is { } vence && vence < proximaRecarga ? vence : proximaRecarga;
        var espera = despertar - ahora;

        if (espera <= TimeSpan.Zero)
        {
            return;
        }

        // Se despierta al llegar el momento, o antes si termina una comprobación (puede haber vencidos nuevos) o hay recarga.
        using var limite = new CancellationTokenSource(espera, reloj);
        using var enlazado = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limite.Token);

        // Si el reloj avanzó mientras se preparaba la espera, el vencimiento ya pasó y el temporizador
        // recién creado no lo sabría: se vuelve a mirar antes de dormir.
        if (cola.ProximoVencimiento() is { } vencido && vencido <= reloj.GetUtcNow())
        {
            return;
        }

        try
        {
            await _despertar.WaitAsync(enlazado.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Llegó la hora.
        }
    }

    private async Task RecargarAsync(DateTimeOffset ahora, CancellationToken cancellationToken)
    {
        try
        {
            await using var ambito = ambitos.CreateAsyncScope();
            var repositorio = new RepositorioMonitores(ambito.ServiceProvider.GetRequiredService<VigiaDbContext>());
            var monitores = await repositorio.ListarActivosAsync(cancellationToken);
            _ventanas = await repositorio.ListarVentanasVigentesAsync(ahora, cancellationToken);
            cola.Sincronizar(monitores, ahora);

            // Un monitor pausado deja de vigilarse: su último estado no puede seguir contando como si se mirara.
            foreach (var (id, vigencia) in await repositorio.ListarPausadosSinCerrarAsync(cancellationToken))
            {
                await ejecutor.DejarDeVigilarAsync(id, vigencia, cancellationToken);
            }
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            // Sin base de datos se sigue con lo que ya había: mejor comprobar con la lista antigua que parar.
            log.RecargaFallo(excepcion);
        }
    }

    private async Task TrabajarAsync(ChannelReader<Vencimiento> entrada, CancellationToken cancellationToken)
    {
        await foreach (var vencimiento in entrada.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                await ejecutor.EjecutarAsync(vencimiento.Monitor, _ventanas, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Parada: se termina de vaciar el canal sin ejecutar nada más.
            }
            catch (Exception excepcion) when (excepcion is not OperationCanceledException)
            {
                log.ComprobacionFallo(excepcion, vencimiento.Monitor.Id);
            }
            finally
            {
                cola.Completar(vencimiento.Monitor.Id, vencimiento.Previsto, reloj.GetUtcNow());
                _despertar.Release();
            }
        }
    }
}
