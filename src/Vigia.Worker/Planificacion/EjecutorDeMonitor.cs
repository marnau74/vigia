using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Vigia.Comprobaciones;
using Vigia.Datos.Persistencia;
using Vigia.Dominio.Avisos;
using Vigia.Dominio.Mantenimiento;
using Vigia.Dominio.Seguimiento;
using Vigia.Worker.Avisos;

namespace Vigia.Worker.Planificacion;

/// <summary>
/// Hace una comprobación completa de un monitor: aplica el mantenimiento, comprueba (con un reintento
/// inmediato si falla), pasa el resultado por la máquina de estados y lo guarda todo en una transacción.
/// </summary>
/// <remarks>
/// El seguimiento de cada monitor se guarda en memoria entre comprobaciones para no leerlo de la base
/// de datos cada vez. Un cerrojo por monitor hace que nunca lo toquen dos operaciones a la vez (el
/// planificador no lanza dos comprobaciones del mismo monitor, pero pausarlo puede coincidir con una en
/// curso). Si guardar falla, la copia en memoria ya no coincide con la base de datos y se descarta: la
/// siguiente comprobación vuelve a cargarla.
/// </remarks>
public sealed class EjecutorDeMonitor(
    IServiceScopeFactory ambitos,
    EjecutorComprobaciones comprobaciones,
    IManejadorDeEventos manejador,
    DestinosDeAviso destinos,
    MetricasVigia metricas,
    IOptions<OpcionesPlanificador> opciones,
    TimeProvider reloj,
    ILogger<EjecutorDeMonitor> log)
{
    private readonly ConcurrentDictionary<Guid, SeguimientoDeMonitor> _seguimientos = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _cerrojos = new();

    public async Task EjecutarAsync(Dominio.Monitores.Monitor monitor, IReadOnlyList<VentanaMantenimiento> ventanas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        var momento = reloj.GetUtcNow();
        var resultado = await ComprobarConReintentoAsync(monitor, cancellationToken);
        var enMantenimiento = VentanaMantenimiento.AlgunaCubre(ventanas, monitor.Id, momento);
        var cerrojo = _cerrojos.GetOrAdd(monitor.Id, _ => new SemaphoreSlim(1, 1));

        await cerrojo.WaitAsync(cancellationToken);
        try
        {
            await using var ambito = ambitos.CreateAsyncScope();
            var db = ambito.ServiceProvider.GetRequiredService<VigiaDbContext>();
            var repositorio = new RepositorioSeguimiento(db);

            if (!_seguimientos.TryGetValue(monitor.Id, out var seguimiento))
            {
                seguimiento = await repositorio.CargarAsync(monitor.Id, momento, cancellationToken);
                _seguimientos[monitor.Id] = seguimiento;
            }

            var eventos = new List<EventoDeSeguimiento>(seguimiento.ActualizarMantenimiento(enMantenimiento, momento));
            eventos.AddRange(seguimiento.Registrar(
                new Observacion(resultado.Correcto, resultado.Latencia, resultado.Error, momento),
                ReglasDeSeguimiento.De(monitor)));

            // Los avisos se guardan con el cambio de estado que los provoca, en la misma transacción.
            var avisos = PlanDeAvisos.Crear(eventos, destinos.Lista, momento);

            await repositorio.GuardarAsync(seguimiento, eventos, Convertir(monitor, resultado, momento, enMantenimiento), avisos, cancellationToken);

            metricas.Comprobacion(monitor.Tipo.ToString(), resultado.Correcto, resultado.Latencia);
            Contar(eventos);
            await AvisarAsync(new ComprobacionRegistrada(monitor.Id, momento, resultado.Correcto, resultado.Latencia, seguimiento.Estado, eventos), cancellationToken);
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            _seguimientos.TryRemove(monitor.Id, out _);
            metricas.ErrorInterno();
            log.NoSeGuardo(excepcion, monitor.Id);
        }
        finally
        {
            cerrojo.Release();
        }
    }

    /// <summary>
    /// Anota que un monitor pausado se ha dejado de vigilar: pasa a «desconocido» y cierra su incidente sin
    /// avisar. El momento es el de la pausa o, si el worker no lo vio a tiempo (estaba parado), cuando venció su
    /// última comprobación. Un monitor que nunca se comprobó no tiene nada que anotar.
    /// </summary>
    public async Task DejarDeVigilarAsync(Guid monitorId, TimeSpan vigencia, CancellationToken cancellationToken)
    {
        var cerrojo = _cerrojos.GetOrAdd(monitorId, _ => new SemaphoreSlim(1, 1));

        await cerrojo.WaitAsync(cancellationToken);
        try
        {
            await using var ambito = ambitos.CreateAsyncScope();
            var repositorio = new RepositorioSeguimiento(ambito.ServiceProvider.GetRequiredService<VigiaDbContext>());

            // Se lee siempre de la base de datos: es lo que hay que corregir, y la copia en memoria se descarta igualmente.
            _seguimientos.TryRemove(monitorId, out _);
            var seguimiento = await repositorio.CargarSiExisteAsync(monitorId, cancellationToken);

            if (seguimiento is null)
            {
                return;
            }

            var ahora = reloj.GetUtcNow();
            var momento = seguimiento.UltimaObservacion is { } ultima && ultima + vigencia < ahora ? ultima + vigencia : ahora;
            var eventos = seguimiento.DejarDeVigilar(momento);

            if (eventos.Count > 0)
            {
                await repositorio.GuardarAsync(seguimiento, eventos, null, PlanDeAvisos.Crear(eventos, destinos.Lista, ahora), cancellationToken);
                Contar([.. eventos]);
            }
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            metricas.ErrorInterno();
            log.NoSeGuardo(excepcion, monitorId);
        }
        finally
        {
            cerrojo.Release();
        }
    }

    /// <summary>Un fallo puede ser un tropiezo puntual: se repite una vez tras una espera corta antes de contarlo, salvo que no quepa en el intervalo o que el destino esté prohibido (repetir no cambiaría nada).</summary>
    private async Task<ResultadoComprobacion> ComprobarConReintentoAsync(Dominio.Monitores.Monitor monitor, CancellationToken cancellationToken)
    {
        var espera = opciones.Value.EsperaReintento;
        var resultado = await comprobaciones.EjecutarAsync(monitor.Configuracion, cancellationToken);

        var cabeReintento = (monitor.Configuracion.TiempoMaximo * 2) + espera < monitor.Intervalo;

        if (resultado.Correcto || resultado.Fallo == TipoFallo.DestinoBloqueado || !cabeReintento)
        {
            return resultado;
        }

        if (espera > TimeSpan.Zero)
        {
            await Task.Delay(espera, reloj, cancellationToken);
        }

        var segundo = await comprobaciones.EjecutarAsync(monitor.Configuracion, cancellationToken);

        return segundo;
    }

    private void Contar(List<EventoDeSeguimiento> eventos)
    {
        foreach (var evento in eventos)
        {
            switch (evento)
            {
                case IncidenteAbierto:
                    metricas.IncidenteAbierto();
                    break;
                case IncidenteCerrado:
                    metricas.IncidenteCerrado();
                    break;
            }
        }
    }

    private async Task AvisarAsync(ComprobacionRegistrada comprobacion, CancellationToken cancellationToken)
    {
        try
        {
            await manejador.ManejarAsync(comprobacion, cancellationToken);
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            log.ManejadorFallo(excepcion);
        }
    }

    private static ResultadoEntidad Convertir(Dominio.Monitores.Monitor monitor, ResultadoComprobacion resultado, DateTimeOffset momento, bool enMantenimiento) => new()
    {
        MonitorId = monitor.Id,
        Momento = momento,
        Correcto = resultado.Correcto,
        LatenciaMs = (int)Math.Min(int.MaxValue, resultado.Latencia.TotalMilliseconds),
        Fallo = (short)resultado.Fallo,
        Error = resultado.Error is { Length: > 500 } largo ? largo[..500] : resultado.Error,
        Detalles = resultado.Detalles.Count == 0 ? null : JsonSerializer.Serialize(resultado.Detalles),
        EnMantenimiento = enMantenimiento,
    };
}
