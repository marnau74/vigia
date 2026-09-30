using Shouldly;

using Vigia.Dominio.Avisos;
using Vigia.Dominio.Seguimiento;

namespace Vigia.Dominio.Tests;

public class AvisosTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly DestinoDeAviso Correo = new(CanalAviso.Correo, "guardia@ejemplo.com");
    private static readonly DestinoDeAviso Telegram = new(CanalAviso.Telegram, "123456");

    private static IReadOnlyList<Aviso> Avisar(Banco banco, params DestinoDeAviso[] destinos) =>
        PlanDeAvisos.Crear(banco.Eventos, destinos, Ahora);

    // --- Qué avisa y qué no ------------------------------------------------------------------

    [Fact]
    public void Una_caida_simulada_genera_un_unico_aviso_de_caida_y_otro_de_recuperacion()
    {
        var banco = new Banco().Pasos("OOOFFFFFFOO");

        var avisos = Avisar(banco, Correo);

        avisos.Select(a => a.Tipo).ShouldBe([TipoAviso.Caida, TipoAviso.Recuperacion]);
    }

    [Theory]
    [InlineData("OOOOOO")]
    [InlineData("OFOFOF")]
    [InlineData("OFFOFFO")]
    [InlineData("LLLLLL")]
    [InlineData("OLOLOL")]
    public void Sin_incidente_no_se_avisa_a_nadie(string secuencia)
    {
        Avisar(new Banco().Pasos(secuencia), Correo, Telegram).ShouldBeEmpty();
    }

    [Fact]
    public void Seguir_caido_no_repite_el_aviso()
    {
        var banco = new Banco().Pasos("OFFF" + new string('F', 50));

        Avisar(banco, Correo).Count.ShouldBe(1);
    }

    [Fact]
    public void Un_incidente_abierto_sin_cerrar_solo_tiene_aviso_de_caida()
    {
        var avisos = Avisar(new Banco().Pasos("OFFF"), Correo);

        avisos.ShouldHaveSingleItem().Tipo.ShouldBe(TipoAviso.Caida);
    }

    [Fact]
    public void Cada_destino_recibe_su_propio_aviso()
    {
        var avisos = Avisar(new Banco().Pasos("OFFFO"), Correo, Telegram);

        avisos.Count.ShouldBe(4);
        avisos.Count(a => a.Canal == CanalAviso.Correo).ShouldBe(2);
        avisos.Count(a => a.Canal == CanalAviso.Telegram).ShouldBe(2);
        avisos.Select(a => (a.Tipo, a.Canal, a.Destino)).Distinct().Count().ShouldBe(4);
    }

    [Fact]
    public void Sin_destinos_no_hay_avisos()
    {
        Avisar(new Banco().Pasos("OFFFO")).ShouldBeEmpty();
    }

    [Fact]
    public void El_aviso_de_recuperacion_es_del_mismo_incidente_que_el_de_caida()
    {
        var banco = new Banco().Pasos("OFFFO");

        var avisos = Avisar(banco, Correo);

        avisos.Select(a => a.IncidenteId).Distinct().Count().ShouldBe(1);
        avisos[0].IncidenteId.ShouldBe(banco.Eventos.OfType<IncidenteAbierto>().Single().Incidente.Id);
        avisos.ShouldAllBe(a => a.MonitorId == banco.Id);
    }

    [Fact]
    public void Dos_incidentes_distintos_avisan_dos_veces_cada_uno()
    {
        var avisos = Avisar(new Banco().Pasos("OFFFOOFFFO"), Correo);

        avisos.Select(a => a.Tipo).ShouldBe([TipoAviso.Caida, TipoAviso.Recuperacion, TipoAviso.Caida, TipoAviso.Recuperacion]);
        avisos.Select(a => a.IncidenteId).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void Un_mantenimiento_que_cierra_el_incidente_no_avisa_de_recuperacion()
    {
        var banco = new Banco().Pasos("OFFF");
        banco.Mantenimiento(true);

        var avisos = Avisar(banco, Correo);

        avisos.ShouldHaveSingleItem().Tipo.ShouldBe(TipoAviso.Caida);
        banco.Eventos.OfType<IncidenteCerrado>().Single().Motivo.ShouldBe(MotivoDeCierre.Mantenimiento);
    }

    [Fact]
    public void Un_fallo_durante_el_mantenimiento_no_avisa()
    {
        var banco = new Banco().Pasos("OO");
        banco.Mantenimiento(true);
        banco.Pasos("FFFFFF");

        Avisar(banco, Correo).ShouldBeEmpty();
    }

    [Fact]
    public void Recuperarse_a_un_estado_lento_tambien_avisa_de_la_recuperacion()
    {
        var banco = new Banco(umbralLento: Banco.Umbral).Pasos("OFFFL");

        Avisar(banco, Correo).Select(a => a.Tipo).ShouldBe([TipoAviso.Caida, TipoAviso.Recuperacion]);
    }

    [Fact]
    public void Los_avisos_aparecen_en_el_orden_en_que_pasaron_los_hechos()
    {
        var banco = new Banco().Pasos("OFFFOFFFO");

        Avisar(banco, Correo).Select(a => a.Tipo).ShouldBe(
            [TipoAviso.Caida, TipoAviso.Recuperacion, TipoAviso.Caida, TipoAviso.Recuperacion]);
    }

    /// <summary>
    /// La garantía importante: sea cual sea la secuencia, nunca hay dos avisos iguales, ninguna
    /// recuperación sin caída previa del mismo incidente, y cada incidente avisa como mucho una vez de cada cosa.
    /// </summary>
    [Fact]
    public void Con_diez_mil_secuencias_aleatorias_nunca_se_duplica_ni_se_avisa_sin_motivo()
    {
        var azar = new Random(20261001);
        var letras = "OOOOFFFL";

        for (var prueba = 0; prueba < 10_000; prueba++)
        {
            var banco = new Banco(azar.Next(1, 5), Banco.Umbral);
            var pasos = azar.Next(1, 40);

            for (var i = 0; i < pasos; i++)
            {
                if (azar.Next(25) == 0)
                {
                    banco.Mantenimiento(azar.Next(2) == 0);
                }
                else
                {
                    banco.Paso(letras[azar.Next(letras.Length)]);
                }
            }

            var avisos = Avisar(banco, Correo, Telegram);

            avisos.Select(a => (a.IncidenteId, a.Tipo, a.Canal, a.Destino)).Distinct().Count()
                .ShouldBe(avisos.Count, $"aviso duplicado en la prueba {prueba}");

            var abiertos = banco.Eventos.OfType<IncidenteAbierto>().Select(e => e.Incidente.Id).ToHashSet();
            avisos.Where(a => a.Tipo == TipoAviso.Caida).Select(a => a.IncidenteId).ToHashSet().SetEquals(abiertos).ShouldBeTrue();

            var recuperados = avisos.Where(a => a.Tipo == TipoAviso.Recuperacion).Select(a => a.IncidenteId).ToHashSet();
            recuperados.IsSubsetOf(abiertos).ShouldBeTrue($"recuperación sin caída en la prueba {prueba}");

            var cerradosPorRecuperacion = banco.Eventos.OfType<IncidenteCerrado>().Where(e => e.Motivo == MotivoDeCierre.Recuperado).Select(e => e.Incidente.Id).ToHashSet();
            recuperados.SetEquals(cerradosPorRecuperacion).ShouldBeTrue();
        }
    }

    // --- El aviso y sus reintentos -------------------------------------------------------------

    private static Aviso Nuevo() => Aviso.Crear(Guid.NewGuid(), Guid.NewGuid(), TipoAviso.Caida, Correo, Ahora);

    [Fact]
    public void Un_aviso_nuevo_esta_pendiente_y_se_puede_enviar_ya()
    {
        var aviso = Nuevo();

        aviso.EstaPendiente.ShouldBeTrue();
        aviso.ProximoIntentoEn.ShouldBe(Ahora);
        aviso.Intentos.ShouldBe(0);
        aviso.Destino.ShouldBe("guardia@ejemplo.com");
    }

    [Fact]
    public void El_destino_se_guarda_sin_espacios_sobrantes()
    {
        Aviso.Crear(Guid.NewGuid(), Guid.NewGuid(), TipoAviso.Caida, new DestinoDeAviso(CanalAviso.Correo, "  ana@ejemplo.com \n"), Ahora)
            .Destino.ShouldBe("ana@ejemplo.com");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_destino_vacio_no_es_valido(string direccion)
    {
        Should.Throw<ArgumentException>(() => Aviso.Crear(Guid.NewGuid(), Guid.NewGuid(), TipoAviso.Caida, new DestinoDeAviso(CanalAviso.Correo, direccion), Ahora));
    }

    [Fact]
    public void Enviado_deja_de_estar_pendiente()
    {
        var aviso = Nuevo();
        aviso.RegistrarFallo("sin conexión", Ahora);

        aviso.MarcarEnviado(Ahora.AddMinutes(1));

        aviso.EstaPendiente.ShouldBeFalse();
        aviso.EnviadoEn.ShouldBe(Ahora.AddMinutes(1));
        aviso.UltimoError.ShouldBeNull();
    }

    [Fact]
    public void Cada_fallo_espera_mas_que_el_anterior()
    {
        var aviso = Nuevo();
        var esperas = new List<TimeSpan>();

        for (var i = 0; i < Aviso.MaximoIntentos - 1; i++)
        {
            aviso.RegistrarFallo("error", Ahora);
            esperas.Add(aviso.ProximoIntentoEn - Ahora);
        }

        esperas.ShouldBe(esperas.Order());
        esperas.Distinct().Count().ShouldBe(esperas.Count);
        esperas[0].ShouldBe(TimeSpan.FromSeconds(30));
        aviso.EstaPendiente.ShouldBeTrue();
    }

    [Fact]
    public void Tras_el_maximo_de_intentos_se_abandona()
    {
        var aviso = Nuevo();

        for (var i = 0; i < Aviso.MaximoIntentos; i++)
        {
            aviso.RegistrarFallo("error", Ahora);
        }

        aviso.Abandonado.ShouldBeTrue();
        aviso.EstaPendiente.ShouldBeFalse();
        aviso.Intentos.ShouldBe(Aviso.MaximoIntentos);
    }

    [Fact]
    public void Las_esperas_cubren_todos_los_reintentos()
    {
        Aviso.Esperas.Length.ShouldBe(Aviso.MaximoIntentos - 1);
    }

    [Fact]
    public void El_error_guardado_se_recorta()
    {
        var aviso = Nuevo();

        aviso.RegistrarFallo(new string('x', 2000), Ahora);

        aviso.UltimoError!.Length.ShouldBe(500);
    }

    [Fact]
    public void Reservar_aparta_el_aviso_hasta_la_hora_indicada()
    {
        var aviso = Nuevo();

        aviso.Reservar(Ahora.AddMinutes(5));

        aviso.ProximoIntentoEn.ShouldBe(Ahora.AddMinutes(5));
        aviso.EstaPendiente.ShouldBeTrue();
    }

    // --- El texto -------------------------------------------------------------------------------

    private static Incidente IncidenteDePrueba(int fallos = 3, string causa = "Sin respuesta en 10 s.", TimeSpan? duracion = null)
    {
        var incidente = Incidente.Abrir(Guid.NewGuid(), new DateTimeOffset(2026, 10, 1, 3, 14, 5, TimeSpan.FromHours(2)), causa, fallos);

        if (duracion is { } cierre)
        {
            incidente.Cerrar(incidente.AbiertoEn + cierre, MotivoDeCierre.Recuperado);
        }

        return incidente;
    }

    [Fact]
    public void El_aviso_de_caida_dice_que_servicio_desde_cuando_y_por_que()
    {
        var texto = TextoDeAviso.De(TipoAviso.Caida, "Mi web", IncidenteDePrueba(causa: "Conexión rechazada: no hay nada escuchando en ese puerto."));

        texto.Asunto.ShouldBe("[Vigía] «Mi web» está caído");
        texto.Cuerpo.ShouldContain("«Mi web» no responde.");
        texto.Cuerpo.ShouldContain("Caído desde: 2026-10-01 01:14:05 UTC");
        texto.Cuerpo.ShouldContain("Fallos seguidos: 3");
        texto.Cuerpo.ShouldContain("Causa: Conexión rechazada: no hay nada escuchando en ese puerto.");
    }

    [Fact]
    public void El_aviso_de_recuperacion_dice_cuanto_duro_la_caida()
    {
        var texto = TextoDeAviso.De(TipoAviso.Recuperacion, "Mi web", IncidenteDePrueba(duracion: TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(30)));

        texto.Asunto.ShouldBe("[Vigía] «Mi web» se ha recuperado");
        texto.Cuerpo.ShouldContain("vuelve a responder");
        texto.Cuerpo.ShouldContain("Recuperado: 2026-10-01 01:26:35 UTC");
        texto.Cuerpo.ShouldContain("Duración de la caída: 12 min 30 s");
    }

    [Fact]
    public void Las_horas_se_dan_siempre_en_utc_aunque_el_incidente_viniera_con_otra_zona()
    {
        var texto = TextoDeAviso.De(TipoAviso.Caida, "X", IncidenteDePrueba());

        texto.Cuerpo.ShouldContain("UTC");
        texto.Cuerpo.ShouldNotContain("+02:00");
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(60, "1 min 0 s")]
    [InlineData(192, "3 min 12 s")]
    [InlineData(3600, "1 h 0 min")]
    [InlineData(7500, "2 h 5 min")]
    [InlineData(86400, "1 d 0 h")]
    [InlineData(200000, "2 d 7 h")]
    public void Las_duraciones_se_escriben_con_las_dos_unidades_mayores(int segundos, string esperado)
    {
        TextoDeAviso.Duracion(TimeSpan.FromSeconds(segundos)).ShouldBe(esperado);
    }

    [Fact]
    public void El_texto_es_identico_para_la_misma_entrada()
    {
        var incidente = IncidenteDePrueba();

        TextoDeAviso.De(TipoAviso.Caida, "A", incidente).ShouldBe(TextoDeAviso.De(TipoAviso.Caida, "A", incidente));
    }

    [Fact]
    public void No_se_puede_redactar_sin_nombre_ni_incidente()
    {
        Should.Throw<ArgumentException>(() => TextoDeAviso.De(TipoAviso.Caida, " ", IncidenteDePrueba()));
        Should.Throw<ArgumentNullException>(() => TextoDeAviso.De(TipoAviso.Caida, "A", null!));
    }
}
