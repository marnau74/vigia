using Vigia.Dominio.Seguimiento;

namespace Vigia.Dominio.Avisos;

/// <summary>
/// Decide qué avisos provocan los eventos de una comprobación. Son las dos únicas transiciones que
/// avisan: abrir un incidente (caída) y cerrarlo porque el servicio volvió (recuperación). Todo lo demás
/// (un fallo aislado, un estado «sospechoso», un servicio lento, un mantenimiento) no avisa a nadie.
/// </summary>
public static class PlanDeAvisos
{
    public static IReadOnlyList<Aviso> Crear(IEnumerable<EventoDeSeguimiento> eventos, IReadOnlyCollection<DestinoDeAviso> destinos, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(eventos);
        ArgumentNullException.ThrowIfNull(destinos);

        var avisos = new List<Aviso>();

        foreach (var evento in eventos)
        {
            (Incidente Incidente, TipoAviso Tipo)? aviso = evento switch
            {
                IncidenteAbierto abierto => (abierto.Incidente, TipoAviso.Caida),
                IncidenteCerrado { Motivo: MotivoDeCierre.Recuperado } cerrado => (cerrado.Incidente, TipoAviso.Recuperacion),
                _ => null,
            };

            if (aviso is not { } elegido)
            {
                continue;
            }

            avisos.AddRange(destinos.Select(destino => Aviso.Crear(elegido.Incidente.Id, evento.MonitorId, elegido.Tipo, destino, ahora)));
        }

        return avisos;
    }
}
