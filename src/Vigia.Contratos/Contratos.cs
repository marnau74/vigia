using System.Text.Json;

using Vigia.Dominio.Monitores;

namespace Vigia.Contratos;

// Lo que entra y sale por la API. Son tipos propios y no las clases del dominio: el dominio puede cambiar por
// dentro sin romper a quien consume la API, y la API no expone nada que no haya decidido enseñar.

public sealed record SolicitudAcceso(string? Contrasena);

public sealed record RespuestaAcceso(string Token, DateTimeOffset ExpiraEn);

/// <param name="Configuracion">
/// Un objeto con el campo <c>tipo</c> (<c>Http</c>, <c>Tls</c>, <c>Dns</c>, <c>Tcp</c> o <c>Icmp</c>) y los datos de ese tipo
/// (por ejemplo <c>{"tipo":"Http","url":"https://ejemplo.com/","tiempoMaximo":"00:00:10"}</c>).
/// </param>
public sealed record CuerpoMonitor(
    string? Nombre,
    JsonElement Configuracion,
    int IntervaloSegundos,
    int FallosParaIncidente,
    int? UmbralLentoMs,
    Guid? GrupoId);

public sealed record CuerpoGrupo(string? Slug, string? Nombre, bool Publico);

public sealed record CuerpoModificarGrupo(string? Nombre, bool Publico);

public sealed record CuerpoMantenimiento(IReadOnlyList<Guid>? MonitorIds, DateTimeOffset Inicio, DateTimeOffset Fin, string? Motivo);

public sealed record GrupoDto(Guid Id, string Slug, string Nombre, bool Publico, int Monitores);

public sealed record IncidenteAbiertoDto(Guid Id, DateTimeOffset AbiertoEn, string Causa, int Fallos);

public sealed record MonitorDto(
    Guid Id,
    string Nombre,
    string Tipo,
    JsonElement Configuracion,
    int IntervaloSegundos,
    int FallosParaIncidente,
    int? UmbralLentoMs,
    Guid? GrupoId,
    bool Activo,
    EstadoMonitor Estado,
    DateTimeOffset? EstadoDesde,
    DateTimeOffset? UltimaComprobacion,
    int? UltimaLatenciaMs,
    decimal? Disponibilidad30Dias,
    IncidenteAbiertoDto? IncidenteAbierto);

public sealed record IncidenteDto(Guid Id, Guid MonitorId, string Monitor, DateTimeOffset AbiertoEn, DateTimeOffset? CerradoEn, string? CerradoPor, double DuracionSegundos, int Fallos, string Causa);

public sealed record ResultadoDto(DateTimeOffset Momento, bool Correcto, int LatenciaMs, string? Fallo, string? Error, bool EnMantenimiento);

public sealed record PuntoDeLatencia(DateTimeOffset Hora, double? MediaMs, double? P50Ms, double? P95Ms, int Correctas, int Fallidas, decimal? Disponibilidad);

public sealed record DisponibilidadDto(decimal? Ultimas24Horas, decimal? Ultimos7Dias, decimal? Ultimos30Dias, decimal? Ultimos90Dias);

public sealed record BarraDiaria(DateOnly Dia, decimal? Disponibilidad, double SegundosCaido, EstadoMonitor? PeorEstado);

public sealed record MantenimientoDto(Guid Id, IReadOnlyList<Guid> MonitorIds, DateTimeOffset Inicio, DateTimeOffset Fin, string Motivo);

public sealed record PruebaDto(bool Correcto, int LatenciaMs, string Fallo, string? Error, IReadOnlyDictionary<string, string> Detalles);

// --- La página de estado pública: solo lo que se puede enseñar a cualquiera -----------------------------

public sealed record ServicioPublico(string Nombre, EstadoMonitor Estado, decimal? Disponibilidad90Dias, IReadOnlyList<BarraDiaria> Barras);

public sealed record IncidentePublico(string Servicio, DateTimeOffset AbiertoEn, DateTimeOffset? CerradoEn, double DuracionSegundos);

public sealed record EstadoPublico(string Grupo, string Slug, EstadoGeneral General, DateTimeOffset ActualizadoEn, IReadOnlyList<ServicioPublico> Servicios, IReadOnlyList<IncidentePublico> Incidentes);

/// <summary>El estado del conjunto, con una sola palabra que siempre va acompañada de texto (nunca solo de color).</summary>
public enum EstadoGeneral
{
    SinDatos = 0,
    Operativo = 1,
    Degradado = 2,
    IncidenteParcial = 3,
    Caido = 4,
    Mantenimiento = 5,
}

public static class EstadoGeneralDe
{
    public static EstadoGeneral Calcular(IReadOnlyCollection<EstadoMonitor> estados)
    {
        ArgumentNullException.ThrowIfNull(estados);

        var conocidos = estados.Where(e => e != EstadoMonitor.Desconocido).ToList();

        if (conocidos.Count == 0)
        {
            return EstadoGeneral.SinDatos;
        }

        var caidos = conocidos.Count(e => e == EstadoMonitor.Caido);

        if (caidos == conocidos.Count)
        {
            return EstadoGeneral.Caido;
        }

        if (caidos > 0)
        {
            return EstadoGeneral.IncidenteParcial;
        }

        if (conocidos.Any(e => e == EstadoMonitor.Degradado))
        {
            return EstadoGeneral.Degradado;
        }

        return conocidos.Any(e => e == EstadoMonitor.Mantenimiento) ? EstadoGeneral.Mantenimiento : EstadoGeneral.Operativo;
    }
}

/// <summary>El mensaje que el hub de tiempo real envía al panel cada vez que el worker guarda una comprobación.</summary>
/// <param name="Incidente">«abierto» o «cerrado» si la comprobación abrió o cerró un incidente; vacío si no.</param>
public sealed record ComprobacionEnVivo(Guid MonitorId, DateTimeOffset Momento, bool Correcto, int LatenciaMs, EstadoMonitor Estado, string? Incidente);
