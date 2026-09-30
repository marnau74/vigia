using Shouldly;

using Vigia.Dominio.Monitores;
using Vigia.Worker.Planificacion;

using MonitorDeDominio = Vigia.Dominio.Monitores.Monitor;

namespace Vigia.Worker.Tests;

public class ColaDeVencimientosTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    private static MonitorDeDominio Monitor(int numero = 1, int segundos = 60, Uri? url = null) =>
        MonitorDeDominio.Crear(
            $"Monitor {numero}",
            new ConfiguracionHttp(url ?? new Uri($"https://ejemplo{numero}.com/")),
            TimeSpan.FromSeconds(segundos),
            3,
            null,
            null,
            Ahora).Valor;

    /// <summary>Lo que devuelve la base de datos en cada recarga: otro objeto con el mismo id.</summary>
    private static MonitorDeDominio Recargado(MonitorDeDominio monitor, int? segundos = null, Uri? url = null)
    {
        var copia = MonitorDeDominio.Crear(
            monitor.Nombre,
            url is null ? monitor.Configuracion : new ConfiguracionHttp(url),
            segundos is { } s ? TimeSpan.FromSeconds(s) : monitor.Intervalo,
            monitor.FallosParaIncidente,
            monitor.UmbralLento,
            monitor.GrupoId,
            monitor.CreadoEn).Valor;

        typeof(MonitorDeDominio).GetField("<Id>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(copia, monitor.Id);

        return copia;
    }

    [Fact]
    public void Un_monitor_nuevo_vence_dentro_de_su_primer_intervalo()
    {
        var cola = new ColaDeVencimientos();
        var monitor = Monitor();

        cola.Sincronizar([monitor], Ahora);

        var vence = cola.ProximoVencimiento()!.Value;
        vence.ShouldBeGreaterThanOrEqualTo(Ahora);
        vence.ShouldBeLessThan(Ahora + monitor.Intervalo);
    }

    [Fact]
    public void El_reparto_inicial_es_estable_para_el_mismo_monitor()
    {
        var id = Guid.NewGuid();

        ColaDeVencimientos.PrimerVencimiento(id, TimeSpan.FromMinutes(1), Ahora)
            .ShouldBe(ColaDeVencimientos.PrimerVencimiento(id, TimeSpan.FromMinutes(1), Ahora));
    }

    [Fact]
    public void Cien_monitores_no_vencen_todos_a_la_vez()
    {
        var cola = new ColaDeVencimientos();
        cola.Sincronizar([.. Enumerable.Range(1, 100).Select(n => Monitor(n))], Ahora);

        var vencidosYa = cola.TomarVencidos(Ahora, int.MaxValue);
        var vencidosAlFinal = cola.TomarVencidos(Ahora + TimeSpan.FromSeconds(60), int.MaxValue);

        vencidosYa.Count.ShouldBeLessThan(10, "el reparto debe dispersar los primeros vencimientos");
        (vencidosYa.Count + vencidosAlFinal.Count).ShouldBe(100);
    }

    [Fact]
    public void Solo_se_sacan_los_vencidos_y_en_orden()
    {
        var cola = new ColaDeVencimientos();
        var monitores = Enumerable.Range(1, 20).Select(n => Monitor(n)).ToList();
        cola.Sincronizar(monitores, Ahora);

        var mitad = cola.TomarVencidos(Ahora + TimeSpan.FromSeconds(30), int.MaxValue);

        mitad.ShouldAllBe(v => v.Previsto <= Ahora + TimeSpan.FromSeconds(30));
        mitad.Select(v => v.Previsto).ShouldBe(mitad.Select(v => v.Previsto).Order());
    }

    [Fact]
    public void El_maximo_limita_cuantos_se_sacan_de_una_vez()
    {
        var cola = new ColaDeVencimientos();
        cola.Sincronizar([.. Enumerable.Range(1, 10).Select(n => Monitor(n))], Ahora);

        cola.TomarVencidos(Ahora + TimeSpan.FromMinutes(5), 3).Count.ShouldBe(3);
        cola.EnCurso.ShouldBe(3);
    }

    [Fact]
    public void Un_monitor_en_curso_no_vuelve_a_salir_hasta_completarse()
    {
        var cola = new ColaDeVencimientos();
        cola.Sincronizar([Monitor()], Ahora);
        var tomado = cola.TomarVencidos(Ahora + TimeSpan.FromMinutes(10), int.MaxValue).Single();

        cola.TomarVencidos(Ahora + TimeSpan.FromMinutes(20), int.MaxValue).ShouldBeEmpty("una comprobación lenta no se solapa con la siguiente");
        cola.ProximoVencimiento().ShouldBeNull();

        cola.Completar(tomado.Monitor.Id, tomado.Previsto, tomado.Previsto.AddSeconds(5));

        cola.EnCurso.ShouldBe(0);
        cola.ProximoVencimiento().ShouldBe(tomado.Previsto.AddSeconds(60));
    }

    [Fact]
    public void El_siguiente_vencimiento_se_cuenta_desde_el_previsto_y_no_desde_cuando_termino()
    {
        var cola = new ColaDeVencimientos();
        cola.Sincronizar([Monitor()], Ahora);
        var tomado = cola.TomarVencidos(Ahora + TimeSpan.FromMinutes(1), int.MaxValue).Single();

        cola.Completar(tomado.Monitor.Id, tomado.Previsto, tomado.Previsto.AddSeconds(20));

        cola.ProximoVencimiento().ShouldBe(tomado.Previsto.AddSeconds(60), "el ritmo no se desplaza aunque la comprobación tarde");
    }

    [Fact]
    public void Si_se_quedo_atras_salta_lo_perdido_en_lugar_de_recuperarlo_a_rafagas()
    {
        var cola = new ColaDeVencimientos();
        cola.Sincronizar([Monitor()], Ahora);
        var tomado = cola.TomarVencidos(Ahora + TimeSpan.FromMinutes(1), int.MaxValue).Single();
        var tarde = tomado.Previsto.AddSeconds(185);

        cola.Completar(tomado.Monitor.Id, tomado.Previsto, tarde);

        cola.ProximoVencimiento().ShouldBe(tomado.Previsto.AddSeconds(240), "el primer múltiplo del intervalo posterior a «ahora»");
        cola.TomarVencidos(tarde, int.MaxValue).ShouldBeEmpty();
    }

    [Fact]
    public void Los_monitores_que_dejan_de_estar_activos_se_quitan()
    {
        var cola = new ColaDeVencimientos();
        var uno = Monitor(1);
        var otro = Monitor(2);
        cola.Sincronizar([uno, otro], Ahora);

        cola.Sincronizar([otro], Ahora);

        cola.Cantidad.ShouldBe(1);
        cola.TomarVencidos(Ahora + TimeSpan.FromHours(1), int.MaxValue).Select(v => v.Monitor.Id).ShouldBe([otro.Id]);
    }

    [Fact]
    public void Quitar_un_monitor_mientras_se_comprueba_no_lo_reprograma()
    {
        var cola = new ColaDeVencimientos();
        cola.Sincronizar([Monitor()], Ahora);
        var tomado = cola.TomarVencidos(Ahora + TimeSpan.FromMinutes(1), int.MaxValue).Single();
        cola.Sincronizar([], Ahora);

        cola.Completar(tomado.Monitor.Id, tomado.Previsto, tomado.Previsto);

        cola.Cantidad.ShouldBe(0);
        cola.ProximoVencimiento().ShouldBeNull();
    }

    [Fact]
    public void Un_monitor_modificado_se_comprueba_enseguida()
    {
        var cola = new ColaDeVencimientos();
        var original = Monitor(1, url: new Uri("https://viejo.example/"));
        cola.Sincronizar([original], Ahora);
        var ahora = Ahora + TimeSpan.FromSeconds(1);

        cola.Sincronizar([Recargado(original, url: new Uri("https://nuevo.example/"))], ahora);

        cola.ProximoVencimiento().ShouldBe(ahora);
    }

    [Fact]
    public void Recargar_monitores_iguales_no_altera_el_calendario()
    {
        var cola = new ColaDeVencimientos();
        var monitor = Monitor();
        cola.Sincronizar([monitor], Ahora);
        var previsto = cola.ProximoVencimiento();

        // Los monitores llegan de la base de datos como objetos nuevos en cada recarga.
        cola.Sincronizar([Recargado(monitor)], Ahora + TimeSpan.FromSeconds(20));

        cola.ProximoVencimiento().ShouldBe(previsto);
        cola.Cantidad.ShouldBe(1);
    }

    [Fact]
    public void Cambiar_el_intervalo_de_uno_en_curso_se_aplica_al_completarlo()
    {
        var cola = new ColaDeVencimientos();
        var monitor = Monitor(segundos: 60);
        cola.Sincronizar([monitor], Ahora);
        var tomado = cola.TomarVencidos(Ahora + TimeSpan.FromMinutes(1), int.MaxValue).Single();
        cola.Sincronizar([Recargado(monitor, segundos: 300)], Ahora + TimeSpan.FromMinutes(1));

        cola.Completar(tomado.Monitor.Id, tomado.Previsto, tomado.Previsto);

        cola.ProximoVencimiento().ShouldBe(tomado.Previsto.AddSeconds(300));
    }

    [Fact]
    public void Esta_ocioso_solo_sin_nada_vencido_ni_en_curso()
    {
        var cola = new ColaDeVencimientos();
        cola.Sincronizar([Monitor()], Ahora);
        var vence = cola.ProximoVencimiento()!.Value;

        cola.EstaOcioso(vence.AddSeconds(-1)).ShouldBeTrue();
        cola.EstaOcioso(vence).ShouldBeFalse("vencido y sin tomar");

        var tomado = cola.TomarVencidos(vence, int.MaxValue).Single();
        cola.EstaOcioso(vence).ShouldBeFalse("en curso");

        cola.Completar(tomado.Monitor.Id, tomado.Previsto, vence);
        cola.EstaOcioso(vence).ShouldBeTrue();
    }
}
