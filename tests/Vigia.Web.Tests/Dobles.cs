using System.Text.Json;

using Vigia.Contratos;
using Vigia.Dominio.Monitores;
using Vigia.Web.Servicios;

namespace Vigia.Web.Tests;

/// <summary>La API de mentira de los tests de componentes: devuelve lo que el test le pone y anota a qué se le llamó.</summary>
public sealed class ApiFalsa : IClienteDeApi
{
    public List<string> Llamadas { get; } = [];

    public CuerpoMonitor? UltimoCuerpoDeMonitor { get; private set; }

    public CuerpoGrupo? UltimoCuerpoDeGrupo { get; private set; }

    public CuerpoMantenimiento? UltimoCuerpoDeMantenimiento { get; private set; }

    public JsonElement? UltimaConfiguracionProbada { get; private set; }

    public int UltimasHoras { get; private set; }

    public RespuestaDeApi<IReadOnlyList<MonitorDto>> Monitores { get; set; } = Ok<IReadOnlyList<MonitorDto>>([]);

    public RespuestaDeApi<MonitorDto> Monitor { get; set; } = RespuestaDeApi.Fallo<MonitorDto>(new ErrorDeApi("no_encontrado", "El monitor no existe."));

    public RespuestaDeApi<MonitorDto> Guardado { get; set; } = RespuestaDeApi.Fallo<MonitorDto>(new ErrorDeApi("x", "sin configurar"));

    public RespuestaDeApi<bool> Sencilla { get; set; } = Ok(true);

    public RespuestaDeApi<PruebaDto> Prueba { get; set; } = Ok(new PruebaDto(true, 42, "Ninguno", null, new Dictionary<string, string>()));

    public RespuestaDeApi<IReadOnlyList<BarraDiaria>> Barras { get; set; } = Ok<IReadOnlyList<BarraDiaria>>([]);

    public RespuestaDeApi<IReadOnlyList<PuntoDeLatencia>> Latencia { get; set; } = Ok<IReadOnlyList<PuntoDeLatencia>>([]);

    public RespuestaDeApi<DisponibilidadDto> Disponibilidad { get; set; } = Ok(new DisponibilidadDto(null, null, null, null));

    public RespuestaDeApi<IReadOnlyList<IncidenteDto>> Incidentes { get; set; } = Ok<IReadOnlyList<IncidenteDto>>([]);

    public RespuestaDeApi<IReadOnlyList<GrupoDto>> Grupos { get; set; } = Ok<IReadOnlyList<GrupoDto>>([]);

    public RespuestaDeApi<GrupoDto> GrupoCreado { get; set; } = RespuestaDeApi.Fallo<GrupoDto>(new ErrorDeApi("x", "sin configurar"));

    public RespuestaDeApi<IReadOnlyList<MantenimientoDto>> Mantenimientos { get; set; } = Ok<IReadOnlyList<MantenimientoDto>>([]);

    public RespuestaDeApi<MantenimientoDto> MantenimientoCreado { get; set; } = RespuestaDeApi.Fallo<MantenimientoDto>(new ErrorDeApi("x", "sin configurar"));

    public RespuestaDeApi<EstadoPublico> Publico { get; set; } = RespuestaDeApi.Fallo<EstadoPublico>(new ErrorDeApi("no_encontrado", "no existe"));

    public RespuestaDeApi<RespuestaAcceso> Acceso { get; set; } = RespuestaDeApi.Fallo<RespuestaAcceso>(new ErrorDeApi("acceso.contrasena_incorrecta", "Contraseña incorrecta."));

    public static RespuestaDeApi<T> Ok<T>(T valor) => RespuestaDeApi.Exito(valor);

    public static RespuestaDeApi<T> Caducada<T>() => RespuestaDeApi.Caducada<T>();

    public int Veces(string llamada) => Llamadas.Count(l => l == llamada);

    private Task<T> Anotar<T>(string nombre, T valor)
    {
        Llamadas.Add(nombre);

        return Task.FromResult(valor);
    }

    public Task<RespuestaDeApi<RespuestaAcceso>> AccederAsync(string contrasena, CancellationToken cancellationToken = default) => Anotar("acceder", Acceso);

    public Task<RespuestaDeApi<IReadOnlyList<MonitorDto>>> MonitoresAsync(CancellationToken cancellationToken = default) => Anotar("monitores", Monitores);

    public Task<RespuestaDeApi<MonitorDto>> MonitorAsync(Guid id, CancellationToken cancellationToken = default) => Anotar("monitor", Monitor);

    public Task<RespuestaDeApi<MonitorDto>> CrearMonitorAsync(CuerpoMonitor cuerpo, CancellationToken cancellationToken = default)
    {
        UltimoCuerpoDeMonitor = cuerpo;

        return Anotar("crear-monitor", Guardado);
    }

    public Task<RespuestaDeApi<MonitorDto>> ModificarMonitorAsync(Guid id, CuerpoMonitor cuerpo, CancellationToken cancellationToken = default)
    {
        UltimoCuerpoDeMonitor = cuerpo;

        return Anotar("modificar-monitor", Guardado);
    }

    public Task<RespuestaDeApi<bool>> BorrarMonitorAsync(Guid id, CancellationToken cancellationToken = default) => Anotar("borrar-monitor", Sencilla);

    public Task<RespuestaDeApi<bool>> CambiarActivoAsync(Guid id, bool activo, CancellationToken cancellationToken = default) => Anotar(activo ? "reanudar" : "pausar", Sencilla);

    public Task<RespuestaDeApi<PruebaDto>> ProbarAsync(JsonElement configuracion, CancellationToken cancellationToken = default)
    {
        UltimaConfiguracionProbada = configuracion;

        return Anotar("probar", Prueba);
    }

    public Task<RespuestaDeApi<PruebaDto>> ProbarMonitorAsync(Guid id, CancellationToken cancellationToken = default) => Anotar("probar-monitor", Prueba);

    public Task<RespuestaDeApi<IReadOnlyList<BarraDiaria>>> BarrasAsync(Guid id, int dias, CancellationToken cancellationToken = default) => Anotar("barras", Barras);

    public Task<RespuestaDeApi<IReadOnlyList<PuntoDeLatencia>>> LatenciaAsync(Guid id, int horas, CancellationToken cancellationToken = default)
    {
        UltimasHoras = horas;

        return Anotar("latencia", Latencia);
    }

    public Task<RespuestaDeApi<DisponibilidadDto>> DisponibilidadAsync(Guid id, CancellationToken cancellationToken = default) => Anotar("disponibilidad", Disponibilidad);

    public Task<RespuestaDeApi<IReadOnlyList<IncidenteDto>>> IncidentesAsync(Guid? monitorId, bool soloAbiertos, CancellationToken cancellationToken = default) => Anotar("incidentes", Incidentes);

    public Task<RespuestaDeApi<IReadOnlyList<GrupoDto>>> GruposAsync(CancellationToken cancellationToken = default) => Anotar("grupos", Grupos);

    public Task<RespuestaDeApi<GrupoDto>> CrearGrupoAsync(CuerpoGrupo cuerpo, CancellationToken cancellationToken = default)
    {
        UltimoCuerpoDeGrupo = cuerpo;

        return Anotar("crear-grupo", GrupoCreado);
    }

    public Task<RespuestaDeApi<bool>> BorrarGrupoAsync(Guid id, CancellationToken cancellationToken = default) => Anotar("borrar-grupo", Sencilla);

    public Task<RespuestaDeApi<IReadOnlyList<MantenimientoDto>>> MantenimientosAsync(CancellationToken cancellationToken = default) => Anotar("mantenimientos", Mantenimientos);

    public Task<RespuestaDeApi<MantenimientoDto>> CrearMantenimientoAsync(CuerpoMantenimiento cuerpo, CancellationToken cancellationToken = default)
    {
        UltimoCuerpoDeMantenimiento = cuerpo;

        return Anotar("crear-mantenimiento", MantenimientoCreado);
    }

    public Task<RespuestaDeApi<bool>> BorrarMantenimientoAsync(Guid id, CancellationToken cancellationToken = default) => Anotar("borrar-mantenimiento", Sencilla);

    public Task<RespuestaDeApi<EstadoPublico>> EstadoPublicoAsync(string slug, CancellationToken cancellationToken = default) => Anotar("publico:" + slug, Publico);
}

/// <summary>La conexión en vivo de mentira: el test emite los mensajes que quiere.</summary>
public sealed class ConexionFalsa : IConexionEnVivo
{
    public bool Conectada { get; set; } = true;

    public bool Iniciada { get; private set; }

    public bool Descartada { get; private set; }

    public event Func<ComprobacionEnVivo, Task>? Recibida;

    public event Func<Task>? Reconectada;

    public event Func<bool, Task>? EstadoCambiado;

    public int SuscriptoresDeRecibida => Recibida?.GetInvocationList().Length ?? 0;

    public Task IniciarAsync(CancellationToken cancellationToken = default)
    {
        Iniciada = true;

        return Task.CompletedTask;
    }

    public Task EmitirAsync(ComprobacionEnVivo mensaje) => Recibida?.Invoke(mensaje) ?? Task.CompletedTask;

    public Task ReconectarAsync() => Reconectada?.Invoke() ?? Task.CompletedTask;

    public Task CambiarEstadoAsync(bool conectada)
    {
        Conectada = conectada;

        return EstadoCambiado?.Invoke(conectada) ?? Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Descartada = true;

        return ValueTask.CompletedTask;
    }
}

public static class Datos
{
    public static readonly DateTimeOffset Ahora = new(2026, 10, 15, 10, 30, 0, TimeSpan.Zero);

    public static MonitorDto Monitor(string nombre = "Mi web", EstadoMonitor estado = EstadoMonitor.Operativo, bool activo = true, int? latencia = 120, decimal? disponibilidad = 99.95m, Guid? id = null, IncidenteAbiertoDto? incidente = null, string url = "https://ejemplo.com/") =>
        new(
            id ?? Guid.NewGuid(),
            nombre,
            "Http",
            JsonDocument.Parse($"{{\"tipo\":\"Http\",\"url\":\"{url}\"}}").RootElement.Clone(),
            60,
            3,
            null,
            null,
            activo,
            estado,
            Ahora.AddHours(-2),
            Ahora.AddMinutes(-1),
            latencia,
            disponibilidad,
            incidente);

    public static PuntoDeLatencia Punto(int horasAtras, double? p50 = 100, double? p95 = 300, int fallidas = 0) =>
        new(Ahora.AddHours(-horasAtras), p50, p50, p95, 60, fallidas, 100m);

    public static BarraDiaria Barra(int diasAtras, EstadoMonitor? peor, decimal? disponibilidad = 100m, double segundosCaido = 0) =>
        new(DateOnly.FromDateTime(Ahora.AddDays(-diasAtras).UtcDateTime), disponibilidad, segundosCaido, peor);
}
