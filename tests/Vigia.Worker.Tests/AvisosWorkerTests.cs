using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Vigia.Comprobaciones;
using Vigia.Datos.Persistencia;
using Vigia.Dominio.Avisos;
using Vigia.Dominio.Mantenimiento;
using Vigia.Worker.Avisos;

using MonitorDeDominio = Vigia.Dominio.Monitores.Monitor;

namespace Vigia.Worker.Tests;

/// <summary>Los avisos de punta a punta: del fallo de un servicio al mensaje en el canal, con PostgreSQL real y canales de mentira.</summary>
public class AvisosWorkerTests : BaseWorkerTest
{
    private static readonly TimeSpan Minuto = TimeSpan.FromMinutes(1);

    private static async Task ComprobarVecesAsync(EntornoDeWorker entorno, MonitorDeDominio monitor, int veces, IReadOnlyList<VentanaMantenimiento>? ventanas = null)
    {
        for (var i = 0; i < veces; i++)
        {
            await entorno.Ejecutor.EjecutarAsync(monitor, ventanas ?? [], Cancelacion);
            entorno.Reloj.Advance(Minuto);
        }
    }

    private static async Task<List<Aviso>> AvisosAsync(EntornoDeWorker entorno)
    {
        await using var db = entorno.NuevoContexto();

        return await db.Avisos.AsNoTracking().OrderBy(a => a.CreadoEn).ToListAsync(Cancelacion);
    }

    // --- El criterio de la fase: un aviso de caída y otro de recuperación -----------------------

    [Fact]
    public async Task Una_caida_simulada_genera_un_unico_aviso_de_caida_y_otro_de_recuperacion_por_canal()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);

        // Hasta la caída y mucho después: seguir caído no vuelve a avisar.
        entorno.Comprobador.Respuesta = (_, _) => Fallo("Sin respuesta en 10 s.");
        await ComprobarVecesAsync(entorno, monitor, 12);
        await entorno.Avisos.ProcesarAsync(Cancelacion);

        // Y la recuperación.
        entorno.Comprobador.Respuesta = (_, _) => ResultadoComprobacion.Exito(TimeSpan.FromMilliseconds(90));
        await ComprobarVecesAsync(entorno, monitor, 5);
        await entorno.Avisos.ProcesarAsync(Cancelacion);
        await entorno.Avisos.ProcesarAsync(Cancelacion);

        foreach (var canal in new[] { entorno.Correo, entorno.Telegram })
        {
            var enviados = canal.Enviados.ToList();
            enviados.Count.ShouldBe(2, $"un aviso de caída y otro de recuperación por {canal.Canal}");
            enviados[0].Texto.Asunto.ShouldBe("[Vigía] «Monitor 1» está caído");
            enviados[0].Texto.Cuerpo.ShouldContain("Sin respuesta en 10 s.");
            enviados[1].Texto.Asunto.ShouldBe("[Vigía] «Monitor 1» se ha recuperado");
            enviados[1].Texto.Cuerpo.ShouldContain("Duración de la caída");
        }

        entorno.Correo.Enviados.First().Destino.ShouldBe("guardia@ejemplo.com");
        entorno.Telegram.Enviados.First().Destino.ShouldBe("99");

        var guardados = await AvisosAsync(entorno);
        guardados.Count.ShouldBe(4);
        guardados.ShouldAllBe(a => a.EnviadoEn != null && !a.Abandonado);
    }

    [Fact]
    public async Task Un_fallo_aislado_no_avisa_a_nadie()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        var llamada = 0;
        entorno.Comprobador.Respuesta = (_, _) => ++llamada is 7 or 8 ? Fallo() : ResultadoComprobacion.Exito(TimeSpan.FromMilliseconds(50));

        await ComprobarVecesAsync(entorno, monitor, 10);
        await entorno.Avisos.ProcesarAsync(Cancelacion);

        (await AvisosAsync(entorno)).ShouldBeEmpty();
        entorno.Correo.Enviados.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sin_destinos_configurados_el_incidente_se_guarda_pero_no_hay_avisos()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio, conAvisos: false);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();

        await ComprobarVecesAsync(entorno, monitor, 5);

        await using var db = entorno.NuevoContexto();
        (await db.Incidentes.CountAsync(Cancelacion)).ShouldBe(1);
        (await db.Avisos.CountAsync(Cancelacion)).ShouldBe(0);
    }

    [Fact]
    public async Task Un_mantenimiento_que_cierra_el_incidente_no_manda_aviso_de_recuperacion()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();
        await ComprobarVecesAsync(entorno, monitor, 4);

        var ventana = VentanaMantenimiento.Crear([monitor.Id], entorno.Reloj.GetUtcNow().AddMinutes(-1), entorno.Reloj.GetUtcNow().AddHours(1), "Migración").Valor;
        await ComprobarVecesAsync(entorno, monitor, 2, [ventana]);
        await entorno.Avisos.ProcesarAsync(Cancelacion);

        var avisos = await AvisosAsync(entorno);
        avisos.ShouldAllBe(a => a.Tipo == TipoAviso.Caida);
        avisos.Count.ShouldBe(2);
    }

    [Fact]
    public async Task El_aviso_se_crea_en_la_misma_transaccion_que_el_incidente()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();

        await ComprobarVecesAsync(entorno, monitor, 3);

        // Sin haber enviado nada todavía, el incidente y sus avisos ya están guardados juntos.
        await using var db = entorno.NuevoContexto();
        var incidente = await db.Incidentes.SingleAsync(Cancelacion);
        var avisos = await db.Avisos.ToListAsync(Cancelacion);
        avisos.Count.ShouldBe(2);
        avisos.ShouldAllBe(a => a.IncidenteId == incidente.Id && a.MonitorId == monitor.Id && a.Tipo == TipoAviso.Caida && a.EstaPendiente);
        entorno.Correo.Enviados.ShouldBeEmpty();
    }

    // --- Reintentos --------------------------------------------------------------------------------

    [Fact]
    public async Task Si_el_canal_falla_se_reintenta_despues_y_solo_llega_un_mensaje()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();
        entorno.Correo.Antes = (intento, _) => intento <= 2 ? throw new AvisoNoEnviadoException("Conexión rechazada") : Task.CompletedTask;
        await ComprobarVecesAsync(entorno, monitor, 3);

        (await entorno.Avisos.ProcesarAsync(Cancelacion)).ShouldBe(1, "solo Telegram llegó a la primera");
        entorno.Correo.Enviados.ShouldBeEmpty();

        // Aún no toca reintentar: la espera tras el primer fallo es de 30 s.
        entorno.Reloj.Advance(TimeSpan.FromSeconds(10));
        (await entorno.Avisos.ProcesarAsync(Cancelacion)).ShouldBe(0);
        entorno.Correo.Intentos.ShouldBe(1);

        entorno.Reloj.Advance(TimeSpan.FromSeconds(25));
        (await entorno.Avisos.ProcesarAsync(Cancelacion)).ShouldBe(0, "el segundo intento también falla");
        entorno.Correo.Intentos.ShouldBe(2);

        entorno.Reloj.Advance(TimeSpan.FromMinutes(3));
        (await entorno.Avisos.ProcesarAsync(Cancelacion)).ShouldBe(1);

        entorno.Correo.Enviados.Count.ShouldBe(1);
        entorno.Telegram.Enviados.Count.ShouldBe(1, "un canal caído no repite los avisos de los demás");

        var correo = (await AvisosAsync(entorno)).Single(a => a.Canal == CanalAviso.Correo);
        correo.Intentos.ShouldBe(2);
        correo.EnviadoEn.ShouldNotBeNull();
        correo.UltimoError.ShouldBeNull();
    }

    [Fact]
    public async Task Tras_agotar_los_intentos_se_abandona_y_se_guarda_el_motivo()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();
        entorno.Telegram.Antes = (_, _) => throw new AvisoNoEnviadoException("Telegram respondió 403: bot was blocked by the user");
        await ComprobarVecesAsync(entorno, monitor, 3);

        for (var i = 0; i < Aviso.MaximoIntentos + 2; i++)
        {
            await entorno.Avisos.ProcesarAsync(Cancelacion);
            entorno.Reloj.Advance(TimeSpan.FromHours(3));
        }

        entorno.Telegram.Intentos.ShouldBe(Aviso.MaximoIntentos, "no se insiste para siempre");
        var telegram = (await AvisosAsync(entorno)).Single(a => a.Canal == CanalAviso.Telegram);
        telegram.Abandonado.ShouldBeTrue();
        telegram.UltimoError.ShouldBe("Telegram respondió 403: bot was blocked by the user");
        (await AvisosAsync(entorno)).Single(a => a.Canal == CanalAviso.Correo).EnviadoEn.ShouldNotBeNull();
    }

    [Fact]
    public async Task Un_envio_que_no_termina_se_corta_y_cuenta_como_fallo()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();
        entorno.Correo.Antes = async (_, cancelacion) => await Task.Delay(Timeout.Infinite, cancelacion);
        await ComprobarVecesAsync(entorno, monitor, 3);

        var tarea = entorno.Avisos.ProcesarAsync(Cancelacion);
        await EsperarAsync(() => entorno.Correo.Intentos == 1);
        entorno.Reloj.Advance(TimeSpan.FromSeconds(31));
        await tarea;

        var correo = (await AvisosAsync(entorno)).Single(a => a.Canal == CanalAviso.Correo);
        correo.Intentos.ShouldBe(1);
        correo.UltimoError.ShouldNotBeNull().ShouldContain("tardó más de 30 s");
        correo.EstaPendiente.ShouldBeTrue();
    }

    [Fact]
    public async Task Un_aviso_reservado_por_un_proceso_que_murio_se_recupera_al_vencer_la_reserva()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();
        await ComprobarVecesAsync(entorno, monitor, 3);

        // Otro proceso los reclama y muere sin enviarlos ni anotar nada.
        await using (var db = entorno.NuevoContexto())
        {
            (await new RepositorioAvisos(db).ReclamarAsync(entorno.Reloj.GetUtcNow(), TimeSpan.FromMinutes(2), 10, Cancelacion)).Count.ShouldBe(2);
        }

        (await entorno.Avisos.ProcesarAsync(Cancelacion)).ShouldBe(0, "mientras dura la reserva nadie más los toca");

        entorno.Reloj.Advance(TimeSpan.FromMinutes(3));
        (await entorno.Avisos.ProcesarAsync(Cancelacion)).ShouldBe(2, "pero no se pierden");
    }

    // --- Varias instancias ---------------------------------------------------------------------------

    [Fact]
    public async Task Dos_procesadores_a_la_vez_no_envian_nunca_el_mismo_aviso_dos_veces()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();

        var monitores = new List<MonitorDeDominio>();

        for (var n = 1; n <= 15; n++)
        {
            monitores.Add(await GuardarAsync(n));
        }

        // Tres fallos por monitor, una ronda por minuto (como los comprobaría el worker).
        for (var ronda = 0; ronda < 3; ronda++)
        {
            foreach (var monitor in monitores)
            {
                await entorno.Ejecutor.EjecutarAsync(monitor, [], Cancelacion);
            }

            entorno.Reloj.Advance(Minuto);
        }

        entorno.Correo.Antes = async (_, cancelacion) => await Task.Delay(5, cancelacion);
        entorno.Telegram.Antes = async (_, cancelacion) => await Task.Delay(5, cancelacion);

        var otro = new ProcesadorDeAvisos(
            entorno.Servicios.GetRequiredService<IServiceScopeFactory>(),
            [entorno.Correo, entorno.Telegram],
            entorno.Servicios.GetRequiredService<Vigia.Worker.Planificacion.MetricasVigia>(),
            entorno.Servicios.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpcionesDeAvisos>>(),
            entorno.Reloj,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcesadorDeAvisos>.Instance);

        var resultados = await Task.WhenAll(entorno.Avisos.ProcesarAsync(Cancelacion), otro.ProcesarAsync(Cancelacion));

        resultados.Sum().ShouldBe(30, "15 monitores × 2 canales");
        entorno.Correo.Enviados.Select(e => e.Texto.Asunto).Distinct().Count().ShouldBe(15);
        entorno.Correo.Enviados.Count.ShouldBe(15, "ni un correo repetido");
        entorno.Telegram.Enviados.Count.ShouldBe(15);
        (await AvisosAsync(entorno)).ShouldAllBe(a => a.EnviadoEn != null);
    }

    // --- Al enviar se cuenta la verdad de ahora ---------------------------------------------------------

    [Fact]
    public async Task Los_avisos_pendientes_se_redactan_con_el_estado_actual_y_salen_en_orden()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo("Error 500");
        await ComprobarVecesAsync(entorno, monitor, 3);
        entorno.Comprobador.Respuesta = (_, _) => ResultadoComprobacion.Exito(TimeSpan.FromMilliseconds(40));
        await ComprobarVecesAsync(entorno, monitor, 1);

        await entorno.Avisos.ProcesarAsync(Cancelacion);

        var textos = entorno.Correo.Enviados.Select(e => e.Texto.Asunto).ToList();
        textos.ShouldBe(["[Vigía] «Monitor 1» está caído", "[Vigía] «Monitor 1» se ha recuperado"], ignoreOrder: true);
    }

    // --- Mantenimiento de los avisos -------------------------------------------------------------------------

    [Fact]
    public async Task La_retencion_borra_los_avisos_enviados_antiguos_y_conserva_los_pendientes()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var monitor = await GuardarAsync(1);
        entorno.Comprobador.Respuesta = (_, _) => Fallo();
        await ComprobarVecesAsync(entorno, monitor, 3);
        entorno.Telegram.Antes = (_, _) => throw new AvisoNoEnviadoException("caído");
        await entorno.Avisos.ProcesarAsync(Cancelacion);

        await using var db = entorno.NuevoContexto();
        var borrados = await new RepositorioAvisos(db).PurgarAsync(entorno.Reloj.GetUtcNow().AddDays(1), Cancelacion);

        borrados.ShouldBe(1, "solo el enviado; el de Telegram sigue pendiente de reintento");
        (await db.Avisos.SingleAsync(Cancelacion)).Canal.ShouldBe(CanalAviso.Telegram);
    }
}
