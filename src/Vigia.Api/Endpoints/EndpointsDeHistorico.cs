using Microsoft.EntityFrameworkCore;

using Vigia.Api.Consultas;
using Vigia.Contratos;
using Vigia.Datos.Persistencia;

namespace Vigia.Api.Endpoints;

/// <summary>El histórico de un monitor: resultados, latencia, disponibilidad, barras diarias e incidentes.</summary>
public static class EndpointsDeHistorico
{
    public static void MapHistorico(this IEndpointRouteBuilder rutas)
    {
        ArgumentNullException.ThrowIfNull(rutas);

        var monitores = rutas.MapGroup("/monitores/{id:guid}").WithTags("Histórico");

        monitores.MapGet("/resultados", async (Guid id, DateTimeOffset? desde, DateTimeOffset? hasta, int? limite, VigiaDbContext db, ConsultasDePanel consultas, TimeProvider reloj, CancellationToken ct) =>
        {
            var fin = hasta ?? reloj.GetUtcNow().AddSeconds(1);
            var inicio = desde ?? fin.AddHours(-24);

            if (inicio >= fin)
            {
                return Respuestas.Invalido("historico.rango_invalido", "«desde» debe ser anterior a «hasta».");
            }

            if (fin - inicio > ConsultasDePanel.VentanaMaximaDeResultados)
            {
                return Respuestas.Invalido("historico.rango_demasiado_grande", $"El detalle de cada comprobación solo se conserva y se consulta por tramos de hasta {ConsultasDePanel.VentanaMaximaDeResultados.TotalDays:0} días; para más tiempo usa la latencia por hora y la disponibilidad.");
            }

            if (limite is <= 0)
            {
                return Respuestas.Invalido("historico.limite_invalido", "El límite debe ser mayor que cero.");
            }

            return await ExisteAsync(db, id, ct)
                ? Results.Ok(await consultas.ResultadosAsync(id, inicio, fin, limite ?? 500, ct))
                : Respuestas.NoEncontrado("El monitor");
        });

        monitores.MapGet("/latencia", async (Guid id, int? horas, VigiaDbContext db, ConsultasDePanel consultas, CancellationToken ct) =>
        {
            if (horas is <= 0 or > 24 * 90)
            {
                return Respuestas.Invalido("historico.horas_invalidas", "Las horas deben estar entre 1 y 2160 (90 días).");
            }

            return await ExisteAsync(db, id, ct) ? Results.Ok(await consultas.LatenciaAsync(id, horas ?? 24, ct)) : Respuestas.NoEncontrado("El monitor");
        });

        monitores.MapGet("/disponibilidad", async (Guid id, VigiaDbContext db, ConsultasDePanel consultas, CancellationToken ct) =>
            await ExisteAsync(db, id, ct) ? Results.Ok(await consultas.DisponibilidadAsync(id, ct)) : Respuestas.NoEncontrado("El monitor"));

        monitores.MapGet("/barras", async (Guid id, int? dias, VigiaDbContext db, ConsultasDePanel consultas, CancellationToken ct) =>
        {
            if (dias is <= 0 or > 90)
            {
                return Respuestas.Invalido("historico.dias_invalidos", "Los días deben estar entre 1 y 90.");
            }

            return await ExisteAsync(db, id, ct) ? Results.Ok(await consultas.BarrasAsync(id, dias ?? 90, ct)) : Respuestas.NoEncontrado("El monitor");
        });

        monitores.MapGet("/incidentes", async (Guid id, int? limite, VigiaDbContext db, ConsultasDePanel consultas, CancellationToken ct) =>
        {
            if (limite is <= 0 or > 200)
            {
                return Respuestas.Invalido("historico.limite_invalido", "El límite debe estar entre 1 y 200.");
            }

            return await ExisteAsync(db, id, ct) ? Results.Ok(await consultas.IncidentesAsync(id, soloAbiertos: false, limite ?? 50, ct)) : Respuestas.NoEncontrado("El monitor");
        });

        rutas.MapGet("/incidentes", async (bool? abiertos, int? limite, ConsultasDePanel consultas, CancellationToken ct) =>
            limite is <= 0 or > 200
                ? Respuestas.Invalido("historico.limite_invalido", "El límite debe estar entre 1 y 200.")
                : Results.Ok(await consultas.IncidentesAsync(null, abiertos ?? false, limite ?? 50, ct)))
            .WithTags("Histórico");
    }

    private static Task<bool> ExisteAsync(VigiaDbContext db, Guid id, CancellationToken ct) => db.Monitores.AnyAsync(m => m.Id == id, ct);
}
