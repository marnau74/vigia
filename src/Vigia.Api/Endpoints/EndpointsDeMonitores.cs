using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Vigia.Api.Consultas;
using Vigia.Api.Contratos;
using Vigia.Comprobaciones;
using Vigia.Datos.Persistencia;
using Vigia.Dominio.Comun;
using Vigia.Dominio.Mantenimiento;
using Vigia.Dominio.Monitores;

using MonitorDeDominio = Vigia.Dominio.Monitores.Monitor;

namespace Vigia.Api.Endpoints;

public static class EndpointsDeMonitores
{
    public static void MapMonitores(this IEndpointRouteBuilder rutas)
    {
        ArgumentNullException.ThrowIfNull(rutas);

        MapGrupos(rutas.MapGroup("/grupos").WithTags("Grupos"));
        MapMonitoresCrud(rutas.MapGroup("/monitores").WithTags("Monitores"));
        MapMantenimientos(rutas.MapGroup("/mantenimientos").WithTags("Mantenimiento"));

        rutas.MapPost("/probar", ProbarConfiguracionAsync).WithTags("Monitores")
            .WithSummary("Ejecuta una comprobación con la configuración indicada, sin guardar nada («Probar ahora» del formulario).");
    }

    // --- Monitores -----------------------------------------------------------------------------------

    private static void MapMonitoresCrud(RouteGroupBuilder rutas)
    {
        rutas.MapGet("/", async (Guid? grupo, ConsultasDePanel consultas, CancellationToken ct) => Results.Ok(await consultas.ListarMonitoresAsync(grupo, ct)));

        rutas.MapGet("/{id:guid}", async (Guid id, ConsultasDePanel consultas, CancellationToken ct) =>
            await consultas.ObtenerMonitorAsync(id, ct) is { } monitor ? Results.Ok(monitor) : Respuestas.NoEncontrado("El monitor"));

        rutas.MapPost("/", CrearMonitorAsync);
        rutas.MapPut("/{id:guid}", ModificarMonitorAsync);
        rutas.MapDelete("/{id:guid}", BorrarMonitorAsync);
        rutas.MapPost("/{id:guid}/pausar", (Guid id, VigiaDbContext db, CancellationToken ct) => CambiarActivoAsync(id, activo: false, db, ct));
        rutas.MapPost("/{id:guid}/reanudar", (Guid id, VigiaDbContext db, CancellationToken ct) => CambiarActivoAsync(id, activo: true, db, ct));
        rutas.MapPost("/{id:guid}/probar", ProbarMonitorAsync);
    }

    private static async Task<IResult> CrearMonitorAsync(CuerpoMonitor cuerpo, VigiaDbContext db, ConsultasDePanel consultas, TimeProvider reloj, CancellationToken ct)
    {
        var leido = await LeerAsync(cuerpo, db, ct);

        if (leido.EsFallo)
        {
            return Respuestas.Invalido(leido.Error);
        }

        var (configuracion, umbral) = leido.Valor;

        var creado = MonitorDeDominio.Crear(
            cuerpo.Nombre ?? string.Empty,
            configuracion,
            TimeSpan.FromSeconds(cuerpo.IntervaloSegundos),
            cuerpo.FallosParaIncidente,
            umbral,
            cuerpo.GrupoId,
            reloj.GetUtcNow());

        if (creado.EsFallo)
        {
            return Respuestas.Invalido(creado.Error);
        }

        db.Monitores.Add(creado.Valor);
        await db.GuardarYAvisarDeCambioAsync(ct);

        return Results.Created($"/api/monitores/{creado.Valor.Id}", await consultas.ObtenerMonitorAsync(creado.Valor.Id, ct));
    }

    private static async Task<IResult> ModificarMonitorAsync(Guid id, CuerpoMonitor cuerpo, VigiaDbContext db, ConsultasDePanel consultas, CancellationToken ct)
    {
        var monitor = await db.Monitores.FirstOrDefaultAsync(m => m.Id == id, ct);

        if (monitor is null)
        {
            return Respuestas.NoEncontrado("El monitor");
        }

        var leido = await LeerAsync(cuerpo, db, ct);

        if (leido.EsFallo)
        {
            return Respuestas.Invalido(leido.Error);
        }

        var (configuracion, umbral) = leido.Valor;
        var modificado = monitor.Modificar(cuerpo.Nombre ?? string.Empty, configuracion, TimeSpan.FromSeconds(cuerpo.IntervaloSegundos), cuerpo.FallosParaIncidente, umbral, cuerpo.GrupoId);

        if (modificado.EsFallo)
        {
            return modificado.Error == ErroresMonitor.CambioDeTipo
                ? Respuestas.Conflicto(modificado.Error.Codigo, modificado.Error.Mensaje)
                : Respuestas.Invalido(modificado.Error);
        }

        await db.GuardarYAvisarDeCambioAsync(ct);

        return Results.Ok(await consultas.ObtenerMonitorAsync(id, ct));
    }

    private static async Task<IResult> BorrarMonitorAsync(Guid id, VigiaDbContext db, CancellationToken ct)
    {
        var monitor = await db.Monitores.FirstOrDefaultAsync(m => m.Id == id, ct);

        if (monitor is null)
        {
            return Respuestas.NoEncontrado("El monitor");
        }

        // La base de datos borra en cascada su histórico, sus incidentes y sus avisos.
        db.Monitores.Remove(monitor);
        await db.GuardarYAvisarDeCambioAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> CambiarActivoAsync(Guid id, bool activo, VigiaDbContext db, CancellationToken ct)
    {
        var monitor = await db.Monitores.FirstOrDefaultAsync(m => m.Id == id, ct);

        if (monitor is null)
        {
            return Respuestas.NoEncontrado("El monitor");
        }

        if (activo)
        {
            monitor.Reanudar();
        }
        else
        {
            monitor.Pausar();
        }

        await db.GuardarYAvisarDeCambioAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ProbarMonitorAsync(Guid id, VigiaDbContext db, EjecutorComprobaciones ejecutor, CancellationToken ct)
    {
        var monitor = await db.Monitores.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);

        return monitor is null ? Respuestas.NoEncontrado("El monitor") : Results.Ok(Convertir(await ejecutor.EjecutarAsync(monitor.Configuracion, ct)));
    }

    private static async Task<IResult> ProbarConfiguracionAsync(CuerpoProbar cuerpo, EjecutorComprobaciones ejecutor, CancellationToken ct)
    {
        var leida = LeerConfiguracion(cuerpo.Configuracion);

        if (leida.EsFallo)
        {
            return Respuestas.Invalido(leida.Error);
        }

        var validacion = ValidacionDeConfiguracion.Validar(leida.Valor);

        return validacion.EsFallo ? Respuestas.Invalido(validacion.Error) : Results.Ok(Convertir(await ejecutor.EjecutarAsync(leida.Valor, ct)));
    }

    private static PruebaDto Convertir(ResultadoComprobacion resultado) =>
        new(resultado.Correcto, (int)resultado.Latencia.TotalMilliseconds, resultado.Fallo.ToString(), resultado.Error, resultado.Detalles);

    private static readonly ErrorDominio ConfiguracionInvalida =
        new("monitor.configuracion_invalida", "La configuración debe ser un objeto con el campo «tipo» (Http, Tls, Dns, Tcp o Icmp) y los datos de ese tipo.");

    private static readonly ErrorDominio GrupoInexistente =
        new("monitor.grupo_inexistente", "El grupo indicado no existe.");

    private static readonly ErrorDominio UmbralNegativo =
        new("monitor.umbral_lento_invalido", "El umbral de lentitud debe ser mayor que cero y menor que el tiempo máximo.");

    /// <summary>Interpreta la configuración que llega como JSON. Cualquier cosa que no encaje es un error 400, nunca un 500.</summary>
    internal static Resultado<ConfiguracionMonitor> LeerConfiguracion(JsonElement configuracion)
    {
        if (configuracion.ValueKind != JsonValueKind.Object)
        {
            return Resultado.Fallo<ConfiguracionMonitor>(ConfiguracionInvalida);
        }

        try
        {
            return Resultado.Exito(ConversorConfiguracion.Deserializar(configuracion.GetRawText()));
        }
        catch (Exception excepcion) when (excepcion is JsonException or ArgumentException or InvalidOperationException or FormatException or NotSupportedException or KeyNotFoundException)
        {
            return Resultado.Fallo<ConfiguracionMonitor>(ConfiguracionInvalida);
        }
    }

    private static async Task<Resultado<(ConfiguracionMonitor Configuracion, TimeSpan? Umbral)>> LeerAsync(CuerpoMonitor cuerpo, VigiaDbContext db, CancellationToken ct)
    {
        var configuracion = LeerConfiguracion(cuerpo.Configuracion);

        if (configuracion.EsFallo)
        {
            return Resultado.Fallo<(ConfiguracionMonitor, TimeSpan?)>(configuracion.Error);
        }

        if (cuerpo.GrupoId is { } grupo && !await db.Grupos.AnyAsync(g => g.Id == grupo, ct))
        {
            return Resultado.Fallo<(ConfiguracionMonitor, TimeSpan?)>(GrupoInexistente);
        }

        if (cuerpo.UmbralLentoMs is <= 0)
        {
            return Resultado.Fallo<(ConfiguracionMonitor, TimeSpan?)>(UmbralNegativo);
        }

        return Resultado.Exito((configuracion.Valor, cuerpo.UmbralLentoMs is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null));
    }

    // --- Grupos -----------------------------------------------------------------------------------------

    private static void MapGrupos(RouteGroupBuilder rutas)
    {
        rutas.MapGet("/", async (VigiaDbContext db, CancellationToken ct) =>
        {
            var cuentas = await db.Monitores.AsNoTracking().Where(m => m.GrupoId != null).GroupBy(m => m.GrupoId).Select(g => new { Grupo = g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Grupo!.Value, x => x.Total, ct);
            var grupos = await db.Grupos.AsNoTracking().OrderBy(g => g.Nombre).ToListAsync(ct);

            return Results.Ok(grupos.Select(g => new GrupoDto(g.Id, g.Slug, g.Nombre, g.Publico, cuentas.GetValueOrDefault(g.Id))));
        });

        rutas.MapPost("/", async (CuerpoGrupo cuerpo, VigiaDbContext db, CancellationToken ct) =>
        {
            var creado = Grupo.Crear(cuerpo.Slug ?? string.Empty, cuerpo.Nombre ?? string.Empty, cuerpo.Publico);

            if (creado.EsFallo)
            {
                return Respuestas.Invalido(creado.Error);
            }

            db.Grupos.Add(creado.Valor);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException excepcion) when (excepcion.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Respuestas.Conflicto("grupo.slug_repetido", "Ya existe un grupo con ese identificador.");
            }

            return Results.Created($"/api/grupos/{creado.Valor.Id}", new GrupoDto(creado.Valor.Id, creado.Valor.Slug, creado.Valor.Nombre, creado.Valor.Publico, 0));
        });

        rutas.MapPut("/{id:guid}", async (Guid id, CuerpoModificarGrupo cuerpo, VigiaDbContext db, CancellationToken ct) =>
        {
            var grupo = await db.Grupos.FirstOrDefaultAsync(g => g.Id == id, ct);

            if (grupo is null)
            {
                return Respuestas.NoEncontrado("El grupo");
            }

            var modificado = grupo.Modificar(cuerpo.Nombre ?? string.Empty, cuerpo.Publico);

            if (modificado.EsFallo)
            {
                return Respuestas.Invalido(modificado.Error);
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(new GrupoDto(grupo.Id, grupo.Slug, grupo.Nombre, grupo.Publico, await db.Monitores.CountAsync(m => m.GrupoId == id, ct)));
        });

        // Los monitores del grupo no se borran: pasan a no tener grupo (la clave foránea lo hace sola).
        rutas.MapDelete("/{id:guid}", async (Guid id, VigiaDbContext db, CancellationToken ct) =>
        {
            var grupo = await db.Grupos.FirstOrDefaultAsync(g => g.Id == id, ct);

            if (grupo is null)
            {
                return Respuestas.NoEncontrado("El grupo");
            }

            db.Grupos.Remove(grupo);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });
    }

    // --- Mantenimiento -----------------------------------------------------------------------------------

    private static void MapMantenimientos(RouteGroupBuilder rutas)
    {
        rutas.MapGet("/", async (VigiaDbContext db, TimeProvider reloj, CancellationToken ct) =>
        {
            var ahora = reloj.GetUtcNow();
            var ventanas = await db.VentanasMantenimiento.AsNoTracking().Where(v => v.Fin > ahora).OrderBy(v => v.Inicio).ToListAsync(ct);

            return Results.Ok(ventanas.Select(Convertir));
        });

        rutas.MapPost("/", async (CuerpoMantenimiento cuerpo, VigiaDbContext db, CancellationToken ct) =>
        {
            var ids = cuerpo.MonitorIds?.Distinct().ToList() ?? [];
            var creada = VentanaMantenimiento.Crear(ids, cuerpo.Inicio, cuerpo.Fin, cuerpo.Motivo ?? string.Empty);

            if (creada.EsFallo)
            {
                return Respuestas.Invalido(creada.Error);
            }

            if (await db.Monitores.CountAsync(m => ids.Contains(m.Id), ct) != ids.Count)
            {
                return Respuestas.Invalido("mantenimiento.monitor_inexistente", "Alguno de los monitores indicados no existe.");
            }

            db.VentanasMantenimiento.Add(creada.Valor);
            await db.GuardarYAvisarDeCambioAsync(ct);

            return Results.Created($"/api/mantenimientos/{creada.Valor.Id}", Convertir(creada.Valor));
        });

        rutas.MapDelete("/{id:guid}", async (Guid id, VigiaDbContext db, CancellationToken ct) =>
        {
            var ventana = await db.VentanasMantenimiento.FirstOrDefaultAsync(v => v.Id == id, ct);

            if (ventana is null)
            {
                return Respuestas.NoEncontrado("La ventana de mantenimiento");
            }

            db.VentanasMantenimiento.Remove(ventana);
            await db.GuardarYAvisarDeCambioAsync(ct);

            return Results.NoContent();
        });
    }

    private static MantenimientoDto Convertir(VentanaMantenimiento v) => new(v.Id, v.MonitorIds, v.Inicio, v.Fin, v.Motivo);
}

public sealed record CuerpoProbar(JsonElement Configuracion);
