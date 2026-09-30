using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Vigia.Datos.Persistencia;
using Vigia.Worker.Avisos;
using Vigia.Worker.Planificacion;

namespace Vigia.Worker.Mantenimiento;

/// <summary>
/// Prepara la base de datos antes de que nada escriba en ella: aplica las migraciones y crea las
/// particiones de los próximos meses. Se registra antes que el planificador; los servicios alojados
/// arrancan en orden y este espera a terminar, así que cuando llega la primera comprobación ya hay
/// tabla y partición donde guardarla.
/// </summary>
public sealed class PreparacionDeBaseDeDatos(
    IServiceScopeFactory ambitos,
    IOptions<OpcionesPlanificador> opciones,
    TimeProvider reloj) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var ambito = ambitos.CreateAsyncScope();
        var db = ambito.ServiceProvider.GetRequiredService<VigiaDbContext>();

        if (opciones.Value.MigrarAlArrancar)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        await new Particiones(db).AsegurarAsync(reloj.GetUtcNow(), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// El trabajo de fondo sobre los datos: agrega las horas y días terminados y aplica la retención. Cada
/// paso es idempotente, así que si el worker se cae y se reinicia simplemente retoma lo pendiente.
/// </summary>
public sealed class TareasDeDatos(
    IServiceScopeFactory ambitos,
    IOptions<OpcionesPlanificador> opciones,
    IOptions<OpcionesDeAvisos> avisos,
    TimeProvider reloj,
    ILogger<TareasDeDatos> log) : BackgroundService
{
    private DateOnly? _ultimoDiaDeLimpieza;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(opciones.Value.MantenimientoCada, reloj);

        do
        {
            try
            {
                await EjecutarUnaVezAsync(stoppingToken);
            }
            catch (Exception excepcion) when (excepcion is not OperationCanceledException)
            {
                // Se reintenta en la siguiente vuelta; nada de esto es urgente.
                log.MantenimientoFallo(excepcion);
            }
        }
        while (await SiguienteAsync(temporizador, stoppingToken));
    }

    /// <summary>Un ciclo completo. Público para poder probarlo sin esperar al temporizador.</summary>
    public async Task EjecutarUnaVezAsync(CancellationToken cancellationToken)
    {
        var ahora = reloj.GetUtcNow();

        await using var ambito = ambitos.CreateAsyncScope();
        var db = ambito.ServiceProvider.GetRequiredService<VigiaDbContext>();

        var (horas, dias) = await new Agregador(db).AgregarPendientesAsync(ahora, cancellationToken);

        if (horas > 0)
        {
            log.Agregado(horas, dias);
        }

        // La limpieza y las particiones se hacen una vez al día: cambian poco y no compensa repetirlas.
        var hoy = DateOnly.FromDateTime(ahora.UtcDateTime);

        if (_ultimoDiaDeLimpieza != hoy)
        {
            var particiones = new Particiones(db);
            await particiones.AsegurarAsync(ahora, cancellationToken);
            var eliminadas = await particiones.AplicarRetencionAsync(ahora - opciones.Value.RetencionResultados, cancellationToken);
            var horasBorradas = await new Agregador(db).PurgarHorasAntiguasAsync(ahora, cancellationToken);
            var avisosBorrados = await new RepositorioAvisos(db).PurgarAsync(ahora - avisos.Value.Retencion, cancellationToken);

            log.RetencionAplicada(eliminadas.Count, horasBorradas);
            log.AvisosPurgados(avisosBorrados);
            _ultimoDiaDeLimpieza = hoy;
        }
    }

    private static async Task<bool> SiguienteAsync(PeriodicTimer temporizador, CancellationToken cancellationToken)
    {
        try
        {
            return await temporizador.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
