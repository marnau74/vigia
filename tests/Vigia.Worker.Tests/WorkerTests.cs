using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Shouldly;

using Vigia.Comprobaciones;
using Vigia.Datos.Persistencia;
using Vigia.Dominio.Avisos;
using Vigia.Dominio.Mantenimiento;
using Vigia.Dominio.Monitores;
using Vigia.Dominio.Seguimiento;
using Vigia.Tests.Comunes;
using Vigia.Worker.Avisos;
using Vigia.Worker.Planificacion;

using MonitorDeDominio = Vigia.Dominio.Monitores.Monitor;

namespace Vigia.Worker.Tests;

/// <summary>Un comprobador que no sale a la red: el test decide qué resultado da cada vez y anota cuántas comprobaciones se solapan.</summary>
public sealed class ComprobadorFalso : IComprobador
{
    private readonly ConcurrentDictionary<string, int> _activos = new();
    private int _simultaneas;
    private int _llamadas;

    public TipoMonitor Tipo => TipoMonitor.Http;

    /// <summary>Decide el resultado a partir de la dirección y del número de llamada para esa dirección.</summary>
    public Func<string, int, ResultadoComprobacion> Respuesta { get; set; } = (_, _) => ResultadoComprobacion.Exito(TimeSpan.FromMilliseconds(120));

    public int Llamadas => Volatile.Read(ref _llamadas);

    public int MaximoSimultaneas { get; private set; }

    public int SolapesDelMismoMonitor { get; private set; }

    public async Task<ResultadoComprobacion> ComprobarAsync(ConfiguracionMonitor configuracion, CancellationToken cancellationToken)
    {
        var clave = ((ConfiguracionHttp)configuracion).Url.Host;
        var llamada = Interlocked.Increment(ref _llamadas);

        if (_activos.AddOrUpdate(clave, 1, (_, n) => n + 1) > 1)
        {
            SolapesDelMismoMonitor++;
        }

        var ahora = Interlocked.Increment(ref _simultaneas);
        lock (this)
        {
            MaximoSimultaneas = Math.Max(MaximoSimultaneas, ahora);
        }

        try
        {
            await Task.Yield();
            await Task.Yield();

            return Respuesta(clave, llamada);
        }
        finally
        {
            Interlocked.Decrement(ref _simultaneas);
            _activos.AddOrUpdate(clave, 0, (_, n) => n - 1);
        }
    }
}

/// <summary>Un canal de aviso de mentira: anota lo que se le manda y falla cuando el test lo decide.</summary>
public sealed class CanalFalso(CanalAviso canal) : ICanalDeAviso
{
    public CanalAviso Canal { get; } = canal;

    public ConcurrentQueue<(string Destino, TextoDeAviso Texto)> Enviados { get; } = new();

    /// <summary>Se llama antes de cada envío con el número de intento (desde 1); puede lanzar para simular un fallo o esperar.</summary>
    public Func<int, CancellationToken, Task> Antes { get; set; } = (_, _) => Task.CompletedTask;

    private int _intentos;

    public int Intentos => Volatile.Read(ref _intentos);

    public async Task EnviarAsync(string destino, TextoDeAviso texto, CancellationToken cancellationToken)
    {
        await Antes(Interlocked.Increment(ref _intentos), cancellationToken);
        Enviados.Enqueue((destino, texto));
    }
}

public sealed class RegistroDeEventos : IManejadorDeEventos
{
    public ConcurrentQueue<EventoDeSeguimiento> Eventos { get; } = new();

    public bool Fallar { get; set; }

    public ConcurrentQueue<ComprobacionRegistrada> Comprobaciones { get; } = new();

    public Task ManejarAsync(ComprobacionRegistrada comprobacion, CancellationToken cancellationToken)
    {
        Comprobaciones.Enqueue(comprobacion);

        foreach (var evento in comprobacion.Eventos)
        {
            Eventos.Enqueue(evento);
        }

        return Fallar ? throw new InvalidOperationException("El manejador falla a propósito.") : Task.CompletedTask;
    }
}

/// <summary>Un worker completo (planificador, ejecutor, base de datos real) con reloj y comprobador controlados por el test.</summary>
public sealed class EntornoDeWorker : IAsyncDisposable
{
    private readonly ServiceProvider _proveedor;

    public EntornoDeWorker(string cadenaConexion, DateTimeOffset inicio, int concurrencia = 8, TimeSpan? esperaReintento = null, bool conAvisos = true)
    {
        Reloj = new FakeTimeProvider(inicio);
        Comprobador = new ComprobadorFalso();
        Eventos = new RegistroDeEventos();
        Correo = new CanalFalso(CanalAviso.Correo);
        Telegram = new CanalFalso(CanalAviso.Telegram);
        CadenaConexion = cadenaConexion;

        var servicios = new ServiceCollection();
        servicios.AddSingleton<TimeProvider>(Reloj);
        servicios.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        servicios.AddDbContext<VigiaDbContext>(opciones => OpcionesVigia.Configurar(opciones.UseNpgsql(cadenaConexion)));
        servicios.AddSingleton<IComprobador>(Comprobador);
        servicios.AddSingleton<EjecutorComprobaciones>();
        servicios.AddSingleton<ColaDeVencimientos>();
        servicios.AddSingleton<MetricasVigia>();
        servicios.AddSingleton<IManejadorDeEventos>(Eventos);
        servicios.AddSingleton(Options.Create(conAvisos
            ? new OpcionesDeAvisos { Destinatarios = ["guardia@ejemplo.com"], ChatsTelegram = ["99"], TokenTelegram = "token-de-prueba", Reserva = TimeSpan.FromMinutes(2) }
            : new OpcionesDeAvisos()));
        servicios.AddSingleton(Options.Create(new OpcionesSmtp { Servidor = "correo.ejemplo.com" }));
        servicios.AddSingleton<DestinosDeAviso>();
        servicios.AddSingleton<ICanalDeAviso>(Correo);
        servicios.AddSingleton<ICanalDeAviso>(Telegram);
        servicios.AddSingleton<ProcesadorDeAvisos>();
        servicios.AddSingleton<EjecutorDeMonitor>();
        servicios.AddSingleton<Planificador>();
        servicios.AddSingleton(Options.Create(new OpcionesPlanificador { Concurrencia = concurrencia, EsperaReintento = esperaReintento ?? TimeSpan.Zero }));
        _proveedor = servicios.BuildServiceProvider();

        Cola = _proveedor.GetRequiredService<ColaDeVencimientos>();
        Planificador = _proveedor.GetRequiredService<Planificador>();
        Ejecutor = _proveedor.GetRequiredService<EjecutorDeMonitor>();
    }

    public FakeTimeProvider Reloj { get; }

    public ComprobadorFalso Comprobador { get; }

    public CanalFalso Correo { get; }

    public CanalFalso Telegram { get; }

    public IServiceProvider Servicios => _proveedor;

    public ProcesadorDeAvisos Avisos => _proveedor.GetRequiredService<ProcesadorDeAvisos>();

    public RegistroDeEventos Eventos { get; }

    public string CadenaConexion { get; }

    public ColaDeVencimientos Cola { get; }

    public Planificador Planificador { get; }

    public EjecutorDeMonitor Ejecutor { get; }

    public VigiaDbContext NuevoContexto() => ServidorPostgres.CrearContexto(CadenaConexion);

    /// <summary>Avanza el reloj y espera a que el worker termine todo lo vencido (con un empujón mínimo por si el bucle se durmió justo antes del salto).</summary>
    public async Task AvanzarYEsperarAsync(TimeSpan tiempo)
    {
        Reloj.Advance(tiempo);
        var limite = DateTime.UtcNow.AddSeconds(30);
        var ultimoEmpujon = DateTime.UtcNow;

        while (!Planificador.EstaOcioso)
        {
            if (DateTime.UtcNow > limite)
            {
                throw new TimeoutException("El worker no terminó de procesar los vencimientos.");
            }

            if (DateTime.UtcNow - ultimoEmpujon > TimeSpan.FromMilliseconds(50))
            {
                Reloj.Advance(TimeSpan.FromMilliseconds(1));
                ultimoEmpujon = DateTime.UtcNow;
            }

            await Task.Delay(1);
        }
    }

    public async ValueTask DisposeAsync() => await _proveedor.DisposeAsync();
}

public abstract class BaseWorkerTest : IAsyncLifetime
{
    private static readonly SemaphoreSlim Arranque = new(1, 1);
    private static ServidorPostgres? _servidor;

    protected static readonly DateTimeOffset Inicio = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    protected string Cadena { get; private set; } = string.Empty;

    protected static CancellationToken Cancelacion => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await Arranque.WaitAsync();

        try
        {
            _servidor ??= new ServidorPostgres();
        }
        finally
        {
            Arranque.Release();
        }

        Cadena = await _servidor.CrearBaseDeDatosAsync();

        await using var db = ServidorPostgres.CrearContexto(Cadena);
        await new Particiones(db).AsegurarAsync(Inicio, Cancelacion);
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    protected async Task<MonitorDeDominio> GuardarAsync(int numero, int intervaloSegundos = 60, int fallos = 3)
    {
        var monitor = MonitorDeDominio.Crear(
            $"Monitor {numero}",
            new ConfiguracionHttp(new Uri($"https://m{numero}.ejemplo.com/")),
            TimeSpan.FromSeconds(intervaloSegundos),
            fallos,
            null,
            null,
            Inicio.AddDays(-1)).Valor;

        await using var db = ServidorPostgres.CrearContexto(Cadena);
        await new RepositorioMonitores(db).AgregarAsync(monitor, Cancelacion);

        return monitor;
    }

    protected static ResultadoComprobacion Fallo(string error = "Sin respuesta") =>
        ResultadoComprobacion.Fallido(TipoFallo.TiempoAgotado, error, TimeSpan.FromSeconds(10));

    protected static int Numero(string host) => int.Parse(host.Split('.')[0][1..], System.Globalization.CultureInfo.InvariantCulture);

    protected static async Task EsperarAsync(Func<bool> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);

        while (!condicion())
        {
            if (DateTime.UtcNow > limite)
            {
                throw new TimeoutException("La condición no se cumplió a tiempo.");
            }

            await Task.Delay(20, Cancelacion);
        }
    }
}

public class WorkerTests : BaseWorkerTest
{
    // --- Ejecutor -----------------------------------------------------------------------------

    [Fact]
    public async Task Una_comprobacion_guarda_el_resultado_y_el_estado()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);

        await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);

        await using var db = entorno.NuevoContexto();
        var resultado = await db.Resultados.SingleAsync(Cancelacion);
        resultado.MonitorId.ShouldBe(monitor.Id);
        resultado.Correcto.ShouldBeTrue();
        resultado.LatenciaMs.ShouldBe(120);
        resultado.Momento.ShouldBe(Inicio);
        (await db.Seguimientos.SingleAsync(Cancelacion)).Estado.ShouldBe(EstadoMonitor.Operativo);
    }

    [Fact]
    public async Task Un_fallo_puntual_se_repite_antes_de_contarlo()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, llamada) => llamada == 1 ? Fallo() : ResultadoComprobacion.Exito(TimeSpan.FromMilliseconds(80));

        await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);

        entorno.Comprobador.Llamadas.ShouldBe(2);
        await using var db = entorno.NuevoContexto();
        (await db.Resultados.SingleAsync(Cancelacion)).Correcto.ShouldBeTrue();
        (await db.Seguimientos.SingleAsync(Cancelacion)).FallosSeguidos.ShouldBe(0);
    }

    [Fact]
    public async Task Un_fallo_que_persiste_tras_el_reintento_cuenta_una_sola_vez()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();

        await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);

        entorno.Comprobador.Llamadas.ShouldBe(2);
        await using var db = entorno.NuevoContexto();
        (await db.Resultados.CountAsync(Cancelacion)).ShouldBe(1);
        (await db.Seguimientos.SingleAsync(Cancelacion)).FallosSeguidos.ShouldBe(1);
    }

    [Fact]
    public async Task Un_destino_bloqueado_no_se_reintenta()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => ResultadoComprobacion.Fallido(TipoFallo.DestinoBloqueado, "Bloqueado", TimeSpan.Zero);

        await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);

        entorno.Comprobador.Llamadas.ShouldBe(1);
    }

    [Fact]
    public async Task No_se_reintenta_si_dos_comprobaciones_no_caben_en_el_intervalo()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = MonitorDeDominio.Crear(
            "Justo",
            new ConfiguracionHttp(new Uri("https://m9.ejemplo.com/")) { TiempoMaximo = TimeSpan.FromSeconds(20) },
            TimeSpan.FromSeconds(30),
            3,
            null,
            null,
            Inicio).Valor;
        await using (var db = entorno.NuevoContexto())
        {
            await new RepositorioMonitores(db).AgregarAsync(monitor, Cancelacion);
        }

        entorno.Comprobador.Respuesta = (_, _) => Fallo();

        await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);

        entorno.Comprobador.Llamadas.ShouldBe(1);
    }

    [Fact]
    public async Task Tres_fallos_seguidos_abren_un_incidente_y_avisan_una_vez()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo("Sin respuesta en 10 s.");

        for (var i = 0; i < 5; i++)
        {
            await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);
            entorno.Reloj.Advance(TimeSpan.FromMinutes(1));
        }

        await using var db = entorno.NuevoContexto();
        var incidente = await db.Incidentes.SingleAsync(Cancelacion);
        incidente.EstaAbierto.ShouldBeTrue();
        incidente.Fallos.ShouldBe(5);
        entorno.Eventos.Eventos.OfType<IncidenteAbierto>().Count().ShouldBe(1);
        (await db.Seguimientos.SingleAsync(Cancelacion)).Estado.ShouldBe(EstadoMonitor.Caido);
    }

    [Fact]
    public async Task Al_recuperarse_se_cierra_el_incidente_y_se_avisa_una_vez()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        var caido = true;
        entorno.Comprobador.Respuesta = (_, _) => caido ? Fallo() : ResultadoComprobacion.Exito(TimeSpan.FromMilliseconds(50));

        for (var i = 0; i < 3; i++)
        {
            await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);
            entorno.Reloj.Advance(TimeSpan.FromMinutes(1));
        }

        caido = false;
        await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);

        await using var db = entorno.NuevoContexto();
        (await db.Incidentes.SingleAsync(Cancelacion)).EstaAbierto.ShouldBeFalse();
        entorno.Eventos.Eventos.OfType<IncidenteCerrado>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Un_manejador_que_falla_no_impide_que_todo_quede_guardado()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Eventos.Fallar = true;

        await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);

        await using var db = entorno.NuevoContexto();
        (await db.Resultados.CountAsync(Cancelacion)).ShouldBe(1);
        (await db.Seguimientos.CountAsync(Cancelacion)).ShouldBe(1);
    }

    [Fact]
    public async Task En_mantenimiento_se_guarda_el_resultado_sin_contar_ni_avisar()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();
        var ventana = VentanaMantenimiento.Crear([monitor.Id], Inicio.AddMinutes(-5), Inicio.AddHours(1), "Migración").Valor;

        for (var i = 0; i < 4; i++)
        {
            await entorno.Ejecutor.EjecutarAsync(monitor, [ventana], Cancelacion);
            entorno.Reloj.Advance(TimeSpan.FromMinutes(1));
        }

        await using var db = entorno.NuevoContexto();
        (await db.Resultados.CountAsync(r => r.EnMantenimiento, Cancelacion)).ShouldBe(4);
        (await db.Seguimientos.SingleAsync(Cancelacion)).Estado.ShouldBe(EstadoMonitor.Mantenimiento);
        (await db.Incidentes.CountAsync(Cancelacion)).ShouldBe(0);
        entorno.Eventos.Eventos.OfType<IncidenteAbierto>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Al_acabar_el_mantenimiento_vuelve_a_desconocido_y_se_recupera_con_la_primera_comprobacion()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        var ventana = VentanaMantenimiento.Crear([monitor.Id], Inicio.AddMinutes(-5), Inicio.AddMinutes(2), "Reinicio").Valor;

        await entorno.Ejecutor.EjecutarAsync(monitor, [ventana], Cancelacion);
        entorno.Reloj.Advance(TimeSpan.FromMinutes(3));
        await entorno.Ejecutor.EjecutarAsync(monitor, [ventana], Cancelacion);

        await using var db = entorno.NuevoContexto();
        (await db.Seguimientos.SingleAsync(Cancelacion)).Estado.ShouldBe(EstadoMonitor.Operativo);
        var cambios = await db.CambiosDeEstado.OrderBy(c => c.Momento).Select(c => c.Nuevo).ToListAsync(Cancelacion);
        cambios.ShouldBe([EstadoMonitor.Mantenimiento, EstadoMonitor.Desconocido, EstadoMonitor.Operativo]);
    }

    [Fact]
    public async Task Si_no_se_puede_guardar_no_se_propaga_el_error_y_se_reintenta_desde_la_base_de_datos()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var fantasma = MonitorDeDominio.Crear("Fantasma", new ConfiguracionHttp(new Uri("https://m7.ejemplo.com/")), TimeSpan.FromSeconds(60), 3, null, null, Inicio).Valor;

        await Should.NotThrowAsync(() => entorno.Ejecutor.EjecutarAsync(fantasma, [], Cancelacion));

        await using var db = entorno.NuevoContexto();
        (await db.Resultados.CountAsync(Cancelacion)).ShouldBe(0, "el monitor no existe: la clave foránea rechaza todo y no queda nada a medias");
        (await db.Seguimientos.CountAsync(Cancelacion)).ShouldBe(0);
    }

    // --- Planificador -------------------------------------------------------------------------

    [Fact]
    public async Task Cincuenta_monitores_durante_una_hora_hacen_exactamente_las_comprobaciones_que_tocan()
    {
        const int monitores = 50;
        await using var entorno = new EntornoDeWorker(Cadena, Inicio, concurrencia: 8);

        var caidos = new HashSet<int> { 3, 17, 42 };
        var lentos = new HashSet<int> { 5 };
        entorno.Comprobador.Respuesta = (host, _) => caidos.Contains(Numero(host))
            ? Fallo()
            : ResultadoComprobacion.Exito(TimeSpan.FromMilliseconds(lentos.Contains(Numero(host)) ? 2500 : 100));

        var ids = new Dictionary<int, Guid>();

        for (var n = 1; n <= monitores; n++)
        {
            ids[n] = (await GuardarAsync(n)).Id;
        }

        await entorno.Planificador.StartAsync(Cancelacion);
        await EsperarAsync(() => entorno.Cola.Cantidad == monitores);

        try
        {
            // Una hora en pasos de 10 s: tras cada paso el worker debe quedar sin nada pendiente.
            for (var paso = 0; paso < 360; paso++)
            {
                await entorno.AvanzarYEsperarAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            await entorno.Planificador.StopAsync(CancellationToken.None);
        }

        await using var db = entorno.NuevoContexto();
        var porMonitor = await db.Resultados
            .GroupBy(r => r.MonitorId)
            .Select(g => new { g.Key, Total = g.Count(), Distintos = g.Select(r => r.Momento).Distinct().Count(), Primero = g.Min(r => r.Momento) })
            .ToDictionaryAsync(g => g.Key, Cancelacion);

        porMonitor.Count.ShouldBe(monitores);
        // Cada monitor vence cada 60 s desde su primer vencimiento, que es un punto fijo dentro de su primer minuto.
        // Hasta el final de la simulación le tocan 60 comprobaciones (61 si su primer vencimiento fue casi inmediato
        // y los empujones de un milisegundo del test llevaron el reloj un poco más allá de la hora en punto): ni una más ni una menos.
        var fin = entorno.Reloj.GetUtcNow();
        var anomalos = porMonitor
            .Where(m => m.Value.Total != (int)((fin - ColaDeVencimientos.PrimerVencimiento(m.Key, TimeSpan.FromSeconds(60), Inicio)) / TimeSpan.FromSeconds(60)) + 1)
            .Select(m => $"{m.Key}: {m.Value.Total} (fin {fin:HH:mm:ss.fff})")
            .ToList();
        anomalos.ShouldBeEmpty("ni una comprobación de más ni de menos");
        porMonitor.Values.ShouldAllBe(m => m.Total == 60 || m.Total == 61);
        porMonitor.Values.ShouldAllBe(m => m.Distintos == m.Total, "cada comprobación con su propio instante");
        porMonitor.Values.ShouldAllBe(m => m.Primero >= Inicio && m.Primero < Inicio.AddSeconds(61));

        entorno.Comprobador.SolapesDelMismoMonitor.ShouldBe(0, "nunca dos comprobaciones a la vez del mismo monitor");
        entorno.Comprobador.MaximoSimultaneas.ShouldBeLessThanOrEqualTo(8, "el límite de concurrencia se respeta");

        var incidentes = await db.Incidentes.Where(i => i.CerradoEn == null).Select(i => i.MonitorId).ToListAsync(Cancelacion);
        incidentes.Order().ShouldBe(caidos.Select(n => ids[n]).Order());

        var estados = await db.Seguimientos.ToDictionaryAsync(s => s.MonitorId, s => s.Estado, Cancelacion);
        estados[ids[3]].ShouldBe(EstadoMonitor.Caido);
        estados[ids[1]].ShouldBe(EstadoMonitor.Operativo);
        estados[ids[5]].ShouldBe(EstadoMonitor.Operativo, "sin umbral de lentitud, tardar no es un problema");
    }

    [Fact]
    public async Task Un_monitor_con_intervalo_largo_se_comprueba_menos_veces()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var rapido = await GuardarAsync(1, 60);
        var lento = await GuardarAsync(2, 300);

        await entorno.Planificador.StartAsync(Cancelacion);
        await EsperarAsync(() => entorno.Cola.Cantidad == 2);

        try
        {
            for (var paso = 0; paso < 60; paso++)
            {
                await entorno.AvanzarYEsperarAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            await entorno.Planificador.StopAsync(CancellationToken.None);
        }

        await using var db = entorno.NuevoContexto();
        (await db.Resultados.CountAsync(r => r.MonitorId == rapido.Id, Cancelacion)).ShouldBe(10);
        (await db.Resultados.CountAsync(r => r.MonitorId == lento.Id, Cancelacion)).ShouldBe(2);
    }

    [Fact]
    public async Task Un_monitor_pausado_deja_de_comprobarse_tras_recargar()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        await entorno.Planificador.StartAsync(Cancelacion);
        await EsperarAsync(() => entorno.Cola.Cantidad == 1);

        try
        {
            await entorno.AvanzarYEsperarAsync(TimeSpan.FromMinutes(2));

            await using (var db = entorno.NuevoContexto())
            {
                var guardado = await db.Monitores.SingleAsync(Cancelacion);
                guardado.Pausar();
                await db.SaveChangesAsync(Cancelacion);
            }

            entorno.Planificador.Recargar();
            await EsperarAsync(() => entorno.Cola.Cantidad == 0);
            var antes = entorno.Comprobador.Llamadas;

            await entorno.AvanzarYEsperarAsync(TimeSpan.FromMinutes(5));

            entorno.Comprobador.Llamadas.ShouldBe(antes);
        }
        finally
        {
            await entorno.Planificador.StopAsync(CancellationToken.None);
        }

        monitor.Activo.ShouldBeTrue();
    }

    [Fact]
    public async Task Pausar_un_monitor_caido_lo_deja_en_desconocido_y_cierra_el_incidente_sin_avisar()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();
        await entorno.Planificador.StartAsync(Cancelacion);
        await EsperarAsync(() => entorno.Cola.Cantidad == 1);

        try
        {
            for (var paso = 0; paso < 30; paso++)
            {
                await entorno.AvanzarYEsperarAsync(TimeSpan.FromSeconds(10));
            }

            await using (var db = entorno.NuevoContexto())
            {
                (await db.Incidentes.SingleAsync(Cancelacion)).EstaAbierto.ShouldBeTrue("tres fallos seguidos: caído");
                var guardado = await db.Monitores.SingleAsync(Cancelacion);
                guardado.Pausar();
                await db.SaveChangesAsync(Cancelacion);
            }

            entorno.Planificador.Recargar();
            await EsperarAsync(() => entorno.Cola.Cantidad == 0);
            await EsperarAsync(() =>
            {
                using var db = entorno.NuevoContexto();
                return db.Seguimientos.Single().Estado == EstadoMonitor.Desconocido;
            });
        }
        finally
        {
            await entorno.Planificador.StopAsync(CancellationToken.None);
        }

        await using var lectura = entorno.NuevoContexto();
        var incidente = await lectura.Incidentes.SingleAsync(Cancelacion);
        incidente.EstaAbierto.ShouldBeFalse();
        incidente.CerradoPor.ShouldBe(MotivoDeCierre.SinVigilancia);
        (await lectura.Seguimientos.SingleAsync(Cancelacion)).IncidenteAbiertoId.ShouldBeNull();
        (await lectura.Avisos.CountAsync(a => a.Tipo == TipoAviso.Recuperacion, Cancelacion)).ShouldBe(0, "nadie ha visto que se recuperase");
        (await lectura.CambiosDeEstado.OrderByDescending(c => c.Momento).FirstAsync(Cancelacion)).Nuevo.ShouldBe(EstadoMonitor.Desconocido);
    }

    [Fact]
    public async Task Tras_un_hueco_sin_comprobaciones_el_tiempo_perdido_es_desconocido_y_no_el_ultimo_estado()
    {
        // El worker estuvo parado una hora: al volver, el primer resultado anota el hueco desde que venció el anterior.
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);

        await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);
        entorno.Reloj.Advance(TimeSpan.FromHours(1));
        await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);

        await using var db = entorno.NuevoContexto();
        var cambios = await db.CambiosDeEstado.OrderBy(c => c.Momento).ToListAsync(Cancelacion);
        cambios.Select(c => c.Nuevo).ShouldBe([EstadoMonitor.Operativo, EstadoMonitor.Desconocido, EstadoMonitor.Operativo]);
        cambios[1].Momento.ShouldBe(Inicio + monitor.VigenciaDeUnaComprobacion);
    }

    [Fact]
    public async Task Un_aviso_de_postgres_hace_que_el_worker_conozca_un_monitor_nuevo()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        using var escucha = new EscuchaDeCambios(Cadena, entorno.Planificador, entorno.Reloj, NullLogger<EscuchaDeCambios>.Instance);
        await entorno.Planificador.StartAsync(Cancelacion);
        await escucha.StartAsync(Cancelacion);

        try
        {
            await EsperarAsync(() => entorno.Cola.Cantidad == 0);
            await GuardarAsync(1);

            await using var db = entorno.NuevoContexto();
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_notify('{EscuchaDeCambios.Canal}', '')", Cancelacion);

            await EsperarAsync(() => entorno.Cola.Cantidad == 1);
        }
        finally
        {
            await escucha.StopAsync(CancellationToken.None);
            await entorno.Planificador.StopAsync(CancellationToken.None);
        }
    }
}
