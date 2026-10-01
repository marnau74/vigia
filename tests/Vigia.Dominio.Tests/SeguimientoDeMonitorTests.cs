using Shouldly;

using Vigia.Dominio.Monitores;
using Vigia.Dominio.Seguimiento;

namespace Vigia.Dominio.Tests;

/// <summary>
/// Un monitor de prueba al que se le dan comprobaciones con una letra cada una:
/// <c>O</c> = bien y rápido, <c>L</c> = bien pero lento, <c>F</c> = fallo. Pasa un minuto entre una y otra.
/// </summary>
internal sealed class Banco
{
    public static readonly DateTimeOffset Inicio = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    public static readonly TimeSpan Umbral = TimeSpan.FromSeconds(1);

    private int _fallos;

    public Banco(int fallosParaIncidente = 3, TimeSpan? umbralLento = null)
    {
        Reglas = new ReglasDeSeguimiento(fallosParaIncidente, umbralLento);
        Seguimiento = new SeguimientoDeMonitor(Id, Inicio);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public ReglasDeSeguimiento Reglas { get; set; }

    public SeguimientoDeMonitor Seguimiento { get; private set; }

    public DateTimeOffset Ahora { get; private set; } = Inicio;

    public List<EventoDeSeguimiento> Eventos { get; } = [];

    public EstadoMonitor Estado => Seguimiento.Estado;

    /// <summary>Un paso de un minuto con la comprobación indicada; devuelve solo lo que ha pasado en ese paso.</summary>
    public IReadOnlyList<EventoDeSeguimiento> Paso(char resultado)
    {
        Ahora += TimeSpan.FromMinutes(1);

        var observacion = resultado switch
        {
            'O' => new Observacion(true, TimeSpan.FromMilliseconds(80), null, Ahora),
            'L' => new Observacion(true, TimeSpan.FromSeconds(3), null, Ahora),
            'F' => new Observacion(false, TimeSpan.FromSeconds(10), $"error {++_fallos}", Ahora),
            _ => throw new ArgumentException($"Letra desconocida: {resultado}"),
        };

        var eventos = Seguimiento.Registrar(observacion, Reglas);
        Eventos.AddRange(eventos);

        return eventos;
    }

    public Banco Pasos(string secuencia)
    {
        foreach (var letra in secuencia)
        {
            Paso(letra);
        }

        return this;
    }

    public IReadOnlyList<EventoDeSeguimiento> Mantenimiento(bool enVentana)
    {
        Ahora += TimeSpan.FromMinutes(1);
        var eventos = Seguimiento.ActualizarMantenimiento(enVentana, Ahora);
        Eventos.AddRange(eventos);

        return eventos;
    }

    /// <summary>Pasa el tiempo sin ninguna comprobación (el monitor está pausado o el worker, parado).</summary>
    public void Esperar(TimeSpan tiempo) => Ahora += tiempo;

    public int Abiertos => Eventos.OfType<IncidenteAbierto>().Count();

    public int Cerrados => Eventos.OfType<IncidenteCerrado>().Count();
}

public class SeguimientoDeMonitorTests
{
    // --- Del desconocido al operativo --------------------------------------------------------

    [Fact]
    public void Un_monitor_nuevo_no_tiene_datos()
    {
        var banco = new Banco();

        banco.Estado.ShouldBe(EstadoMonitor.Desconocido);
        banco.Seguimiento.IncidenteAbierto.ShouldBeNull();
        banco.Seguimiento.FallosSeguidos.ShouldBe(0);
    }

    [Fact]
    public void La_primera_comprobacion_correcta_lo_deja_operativo_y_se_anota_el_cambio()
    {
        var banco = new Banco();

        var eventos = banco.Paso('O');

        banco.Estado.ShouldBe(EstadoMonitor.Operativo);
        eventos.Count.ShouldBe(1);
        var cambio = eventos[0].ShouldBeOfType<EstadoCambiado>();
        cambio.Anterior.ShouldBe(EstadoMonitor.Desconocido);
        cambio.Nuevo.ShouldBe(EstadoMonitor.Operativo);
        cambio.Momento.ShouldBe(Banco.Inicio.AddMinutes(1));
    }

    [Fact]
    public void Mientras_todo_va_bien_no_pasa_nada_mas()
    {
        var banco = new Banco().Pasos("O");

        banco.Paso('O').ShouldBeEmpty();
        banco.Paso('O').ShouldBeEmpty();
        banco.Estado.ShouldBe(EstadoMonitor.Operativo);
    }

    // --- Fallos: sospecha primero, caída después ---------------------------------------------

    [Theory]
    [InlineData("OF", EstadoMonitor.Sospechoso)]
    [InlineData("OFF", EstadoMonitor.Sospechoso)]
    [InlineData("OFFF", EstadoMonitor.Caido)]
    [InlineData("OFFFF", EstadoMonitor.Caido)]
    [InlineData("F", EstadoMonitor.Sospechoso)]
    [InlineData("FFF", EstadoMonitor.Caido)]
    public void Con_tres_fallos_seguidos_por_incidente_se_sospecha_y_se_cae_al_tercero(string secuencia, EstadoMonitor esperado)
    {
        new Banco(fallosParaIncidente: 3).Pasos(secuencia).Estado.ShouldBe(esperado);
    }

    [Fact]
    public void Un_fallo_aislado_no_abre_ningun_incidente_ni_avisa()
    {
        var banco = new Banco().Pasos("OOOFOOO");

        banco.Abiertos.ShouldBe(0);
        banco.Cerrados.ShouldBe(0);
        banco.Estado.ShouldBe(EstadoMonitor.Operativo);
    }

    [Fact]
    public void Dos_fallos_seguidos_y_una_recuperacion_tampoco_abren_incidente()
    {
        var banco = new Banco(fallosParaIncidente: 3).Pasos("OFFO");

        banco.Abiertos.ShouldBe(0);
        banco.Estado.ShouldBe(EstadoMonitor.Operativo);
        banco.Seguimiento.FallosSeguidos.ShouldBe(0);
    }

    [Fact]
    public void El_tercer_fallo_seguido_abre_un_incidente_con_su_causa_y_lo_anota_junto_al_cambio_de_estado()
    {
        var banco = new Banco().Pasos("OFF");

        var eventos = banco.Paso('F');

        banco.Estado.ShouldBe(EstadoMonitor.Caido);
        eventos.Count.ShouldBe(2);
        eventos[0].ShouldBeOfType<EstadoCambiado>().Nuevo.ShouldBe(EstadoMonitor.Caido);
        var abierto = eventos[1].ShouldBeOfType<IncidenteAbierto>();
        abierto.Incidente.EstaAbierto.ShouldBeTrue();
        abierto.Incidente.AbiertoEn.ShouldBe(Banco.Inicio.AddMinutes(4));
        abierto.Incidente.Causa.ShouldBe("error 3");
        abierto.Incidente.Fallos.ShouldBe(3);
        banco.Seguimiento.IncidenteAbierto.ShouldBeSameAs(abierto.Incidente);
    }

    [Fact]
    public void Seguir_fallando_no_abre_mas_incidentes_ni_avisa_otra_vez_pero_actualiza_la_causa()
    {
        var banco = new Banco().Pasos("FFF");

        for (var i = 0; i < 20; i++)
        {
            banco.Paso('F').ShouldBeEmpty("ni cambios de estado ni avisos nuevos mientras siga caído");
        }

        banco.Abiertos.ShouldBe(1);
        banco.Seguimiento.IncidenteAbierto!.Fallos.ShouldBe(23);
        banco.Seguimiento.IncidenteAbierto.Causa.ShouldBe("error 23", "la causa es siempre el último error");
    }

    [Fact]
    public void Con_un_solo_fallo_por_incidente_se_cae_al_primero_sin_pasar_por_sospechoso()
    {
        var banco = new Banco(fallosParaIncidente: 1);

        var eventos = banco.Paso('F');

        banco.Estado.ShouldBe(EstadoMonitor.Caido);
        eventos.OfType<EstadoCambiado>().Single().Anterior.ShouldBe(EstadoMonitor.Desconocido);
        eventos.OfType<IncidenteAbierto>().Count().ShouldBe(1);
    }

    [Fact]
    public void Si_el_monitor_estaba_desconocido_un_fallo_tambien_es_sospechoso()
    {
        new Banco().Pasos("F").Estado.ShouldBe(EstadoMonitor.Sospechoso);
    }

    // --- Recuperación ------------------------------------------------------------------------

    [Fact]
    public void Al_recuperarse_se_cierra_el_incidente_con_su_duracion_y_se_avisa_una_sola_vez()
    {
        var banco = new Banco().Pasos("OFFF");
        var incidente = banco.Seguimiento.IncidenteAbierto!;

        banco.Pasos("FF"); // dos minutos más caído
        var eventos = banco.Paso('O');

        banco.Estado.ShouldBe(EstadoMonitor.Operativo);
        banco.Seguimiento.IncidenteAbierto.ShouldBeNull();
        var cerrado = eventos.OfType<IncidenteCerrado>().Single();
        cerrado.Incidente.ShouldBeSameAs(incidente);
        cerrado.Motivo.ShouldBe(MotivoDeCierre.Recuperado);
        incidente.EstaAbierto.ShouldBeFalse();
        incidente.CerradoEn.ShouldBe(Banco.Inicio.AddMinutes(7));
        incidente.Duracion(Banco.Inicio.AddDays(1)).ShouldBe(TimeSpan.FromMinutes(3), "de la apertura (minuto 4, tercer fallo) al cierre (minuto 7)");
        banco.Paso('O').ShouldBeEmpty("una vez recuperado no se avisa más");
        banco.Cerrados.ShouldBe(1);
    }

    [Fact]
    public void Tras_recuperarse_los_fallos_seguidos_empiezan_de_cero()
    {
        var banco = new Banco().Pasos("FFFOFF");

        banco.Estado.ShouldBe(EstadoMonitor.Sospechoso, "solo dos fallos desde la recuperación");
        banco.Abiertos.ShouldBe(1);
    }

    [Fact]
    public void Un_ciclo_de_caida_y_recuperacion_repetido_da_un_aviso_de_cada_por_ciclo()
    {
        var banco = new Banco().Pasos("O" + string.Concat(Enumerable.Repeat("FFFOO", 4)));

        banco.Abiertos.ShouldBe(4);
        banco.Cerrados.ShouldBe(4);
        banco.Eventos.OfType<IncidenteAbierto>().Select(e => e.Incidente.Id).Distinct().Count().ShouldBe(4);
    }

    // --- Lentitud ----------------------------------------------------------------------------

    [Fact]
    public void Una_respuesta_lenta_pero_correcta_es_degradado_y_vuelve_a_operativo_al_acelerar()
    {
        var banco = new Banco(umbralLento: Banco.Umbral);

        banco.Pasos("OL").Estado.ShouldBe(EstadoMonitor.Degradado);
        banco.Paso('L').ShouldBeEmpty();
        banco.Paso('O');

        banco.Estado.ShouldBe(EstadoMonitor.Operativo);
    }

    [Fact]
    public void Sin_umbral_de_lentitud_nunca_hay_degradado()
    {
        new Banco(umbralLento: null).Pasos("OLLL").Estado.ShouldBe(EstadoMonitor.Operativo);
    }

    [Fact]
    public void Justo_en_el_umbral_todavia_es_operativo()
    {
        var banco = new Banco(umbralLento: TimeSpan.FromSeconds(1));
        var observacion = new Observacion(true, TimeSpan.FromSeconds(1), null, Banco.Inicio.AddMinutes(1));

        banco.Seguimiento.Registrar(observacion, banco.Reglas);

        banco.Seguimiento.Estado.ShouldBe(EstadoMonitor.Operativo);
    }

    [Fact]
    public void Recuperarse_con_una_respuesta_lenta_cierra_el_incidente_y_queda_degradado()
    {
        var banco = new Banco(umbralLento: Banco.Umbral).Pasos("FFF");

        var eventos = banco.Paso('L');

        banco.Estado.ShouldBe(EstadoMonitor.Degradado);
        eventos.OfType<IncidenteCerrado>().Count().ShouldBe(1);
    }

    [Fact]
    public void Un_fallo_desde_degradado_lleva_a_sospechoso()
    {
        new Banco(umbralLento: Banco.Umbral).Pasos("LF").Estado.ShouldBe(EstadoMonitor.Sospechoso);
    }

    // --- Mantenimiento -----------------------------------------------------------------------

    [Fact]
    public void Entrar_en_mantenimiento_desde_operativo_solo_cambia_el_estado()
    {
        var banco = new Banco().Pasos("OO");

        var eventos = banco.Mantenimiento(true);

        banco.Estado.ShouldBe(EstadoMonitor.Mantenimiento);
        eventos.Count.ShouldBe(1);
        eventos[0].ShouldBeOfType<EstadoCambiado>().Nuevo.ShouldBe(EstadoMonitor.Mantenimiento);
    }

    [Fact]
    public void Durante_el_mantenimiento_los_fallos_no_cuentan_no_abren_incidente_ni_avisan()
    {
        var banco = new Banco().Pasos("OO");
        banco.Mantenimiento(true);

        for (var i = 0; i < 10; i++)
        {
            banco.Paso('F').ShouldBeEmpty();
        }

        banco.Estado.ShouldBe(EstadoMonitor.Mantenimiento);
        banco.Abiertos.ShouldBe(0);
        banco.Seguimiento.FallosSeguidos.ShouldBe(0);
    }

    [Fact]
    public void Al_salir_del_mantenimiento_se_vuelve_a_desconocido_y_los_fallos_empiezan_de_cero()
    {
        var banco = new Banco().Pasos("OFF");
        banco.Mantenimiento(true);
        banco.Pasos("FFFF"); // se ignoran

        banco.Mantenimiento(false).OfType<EstadoCambiado>().Single().Nuevo.ShouldBe(EstadoMonitor.Desconocido);

        // Si el servicio sigue caído, se detecta por el camino normal: tres fallos hacen falta.
        banco.Pasos("FF").Estado.ShouldBe(EstadoMonitor.Sospechoso);
        banco.Abiertos.ShouldBe(0);
        banco.Paso('F');
        banco.Estado.ShouldBe(EstadoMonitor.Caido);
        banco.Abiertos.ShouldBe(1);
    }

    [Fact]
    public void Al_salir_del_mantenimiento_con_el_servicio_sano_no_hay_aviso_de_recuperacion()
    {
        var banco = new Banco().Pasos("OO");
        banco.Mantenimiento(true);
        banco.Mantenimiento(false);

        banco.Paso('O');

        banco.Estado.ShouldBe(EstadoMonitor.Operativo);
        banco.Abiertos.ShouldBe(0);
        banco.Cerrados.ShouldBe(0);
    }

    [Fact]
    public void Entrar_en_mantenimiento_estando_caido_cierra_el_incidente_por_mantenimiento_sin_dar_por_recuperado_nada()
    {
        var banco = new Banco().Pasos("OFFF");
        var incidente = banco.Seguimiento.IncidenteAbierto!;

        var eventos = banco.Mantenimiento(true);

        eventos.OfType<EstadoCambiado>().Single().Nuevo.ShouldBe(EstadoMonitor.Mantenimiento);
        var cerrado = eventos.OfType<IncidenteCerrado>().Single();
        cerrado.Motivo.ShouldBe(MotivoDeCierre.Mantenimiento);
        incidente.CerradoPor.ShouldBe(MotivoDeCierre.Mantenimiento);
        banco.Seguimiento.IncidenteAbierto.ShouldBeNull();
    }

    [Fact]
    public void Actualizar_el_mantenimiento_es_idempotente()
    {
        var banco = new Banco().Pasos("OO");

        banco.Mantenimiento(false).ShouldBeEmpty("no estaba en mantenimiento");
        banco.Mantenimiento(true).ShouldNotBeEmpty();
        banco.Mantenimiento(true).ShouldBeEmpty("ya está en mantenimiento");
        banco.Mantenimiento(false).ShouldNotBeEmpty();
        banco.Mantenimiento(false).ShouldBeEmpty();
    }

    // --- Observaciones repetidas o antiguas --------------------------------------------------

    [Fact]
    public void Una_observacion_repetida_o_anterior_a_la_ultima_se_ignora()
    {
        var banco = new Banco().Pasos("FF");
        var repetida = new Observacion(false, TimeSpan.Zero, "repetida", banco.Ahora);
        var antigua = new Observacion(false, TimeSpan.Zero, "antigua", banco.Ahora.AddMinutes(-10));

        banco.Seguimiento.Registrar(repetida, banco.Reglas).ShouldBeEmpty();
        banco.Seguimiento.Registrar(antigua, banco.Reglas).ShouldBeEmpty();

        banco.Seguimiento.FallosSeguidos.ShouldBe(2, "no cuentan dos veces");
        banco.Estado.ShouldBe(EstadoMonitor.Sospechoso);
    }

    // --- Cambios de reglas y reconstrucción --------------------------------------------------

    [Fact]
    public void Si_se_baja_el_limite_de_fallos_se_aplica_en_la_siguiente_comprobacion()
    {
        var banco = new Banco(fallosParaIncidente: 5).Pasos("FF");
        banco.Estado.ShouldBe(EstadoMonitor.Sospechoso);

        banco.Reglas = new ReglasDeSeguimiento(2, null);
        banco.Paso('F');

        banco.Estado.ShouldBe(EstadoMonitor.Caido);
    }

    [Fact]
    public void Un_seguimiento_reconstruido_desde_lo_guardado_sigue_donde_estaba_sin_duplicar_el_incidente()
    {
        var original = new Banco().Pasos("OFFF");
        var guardado = original.Seguimiento;

        var restaurado = new SeguimientoDeMonitor(
            guardado.MonitorId, guardado.Estado, guardado.FallosSeguidos, guardado.Desde, guardado.UltimaObservacion, guardado.IncidenteAbierto);

        restaurado.Registrar(new Observacion(false, TimeSpan.Zero, "sigue caído", original.Ahora.AddMinutes(1)), original.Reglas).ShouldBeEmpty();
        restaurado.IncidenteAbierto!.Fallos.ShouldBe(4);

        var cierre = restaurado.Registrar(new Observacion(true, TimeSpan.Zero, null, original.Ahora.AddMinutes(2)), original.Reglas);
        cierre.OfType<IncidenteCerrado>().Count().ShouldBe(1);
    }

    // --- Propiedades: secuencias al azar contra un modelo de referencia ----------------------

    [Fact]
    public void En_diez_mil_secuencias_al_azar_los_incidentes_y_los_estados_cumplen_siempre_sus_reglas()
    {
        var azar = new Random(20260930);

        for (var secuencia = 0; secuencia < 10_000; secuencia++)
        {
            var limite = azar.Next(1, 6);
            var banco = new Banco(limite, azar.Next(2) == 0 ? Banco.Umbral : null);
            var consecutivos = 0; // el modelo: fallos seguidos, sin contar lo que pase en mantenimiento
            var enMantenimiento = false;
            var abiertosAlEntrar = 0;

            for (var paso = 0; paso < 60; paso++)
            {
                var accion = azar.Next(10);

                if (accion == 0)
                {
                    enMantenimiento = !enMantenimiento;
                    abiertosAlEntrar = banco.Abiertos;
                    banco.Mantenimiento(enMantenimiento);
                    consecutivos = 0;
                }
                else
                {
                    var letra = accion < 6 ? 'O' : accion < 7 ? 'L' : 'F';
                    var abiertosAntes = banco.Abiertos;
                    banco.Paso(letra);

                    if (!enMantenimiento)
                    {
                        consecutivos = letra == 'F' ? consecutivos + 1 : 0;
                    }
                    else
                    {
                        banco.Abiertos.ShouldBe(abiertosAntes, "en mantenimiento no se abre ningún incidente");
                    }
                }

                // Invariantes
                (banco.Seguimiento.IncidenteAbierto is not null).ShouldBe(banco.Estado == EstadoMonitor.Caido, $"secuencia {secuencia}, paso {paso}");
                (banco.Abiertos - banco.Cerrados).ShouldBe(banco.Estado == EstadoMonitor.Caido ? 1 : 0, "abiertos menos cerrados es 1 solo si está caído");
                banco.Seguimiento.FallosSeguidos.ShouldBe(enMantenimiento ? 0 : consecutivos);

                if (!enMantenimiento)
                {
                    (banco.Estado == EstadoMonitor.Caido).ShouldBe(consecutivos >= limite, $"caído justo cuando hay {limite} fallos seguidos");
                }
                else
                {
                    banco.Estado.ShouldBe(EstadoMonitor.Mantenimiento);
                }
            }

            _ = abiertosAlEntrar;
        }
    }
}

public class SinVigilanciaTests
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(3);

    private static Banco ConVigencia(int fallos = 3)
    {
        var banco = new Banco(fallos);
        banco.Reglas = banco.Reglas with { Vigencia = Vigencia };

        return banco;
    }

    [Fact]
    public void Un_hueco_mayor_que_la_vigencia_pasa_a_desconocido_desde_que_vencio_la_ultima_comprobacion()
    {
        var banco = ConVigencia().Pasos("OO");
        var ultima = banco.Ahora;

        banco.Esperar(TimeSpan.FromHours(2));
        var eventos = banco.Paso('O');

        var cambios = eventos.OfType<EstadoCambiado>().ToList();
        cambios.Select(c => c.Nuevo).ShouldBe([EstadoMonitor.Desconocido, EstadoMonitor.Operativo]);
        cambios[0].Momento.ShouldBe(ultima + Vigencia, "el último estado solo vale mientras lo respalda la comprobación");
        banco.Estado.ShouldBe(EstadoMonitor.Operativo);
    }

    [Fact]
    public void Un_retraso_dentro_de_la_vigencia_no_es_un_hueco()
    {
        var banco = ConVigencia().Pasos("O");

        banco.Esperar(TimeSpan.FromMinutes(1));
        var eventos = banco.Paso('O');

        eventos.ShouldBeEmpty("dos minutos entre comprobaciones caben en una vigencia de tres");
    }

    [Fact]
    public void Un_hueco_estando_caido_cierra_el_incidente_sin_vigilancia_y_sin_avisar_y_si_sigue_caido_se_abre_otro()
    {
        var banco = ConVigencia().Pasos("FFF");
        var primero = banco.Seguimiento.IncidenteAbierto!;
        var ultima = banco.Ahora;

        banco.Esperar(TimeSpan.FromHours(1));
        banco.Pasos("FFF");

        primero.CerradoPor.ShouldBe(MotivoDeCierre.SinVigilancia);
        primero.CerradoEn.ShouldBe(ultima + Vigencia);
        banco.Abiertos.ShouldBe(2, "al volver a mirar sigue caído: es otro incidente, avisado por el camino normal");
        Avisos.PlanDeAvisos.Crear(banco.Eventos, [new Avisos.DestinoDeAviso(Avisos.CanalAviso.Correo, "guardia@ejemplo.com")], banco.Ahora)
            .Count(a => a.Tipo == Avisos.TipoAviso.Recuperacion).ShouldBe(0, "nadie ha visto que se recuperase");
    }

    [Fact]
    public void Sin_vigencia_en_las_reglas_no_se_miran_los_huecos()
    {
        var banco = new Banco().Pasos("O");

        banco.Esperar(TimeSpan.FromDays(1));

        banco.Paso('O').ShouldBeEmpty();
    }

    [Fact]
    public void Dejar_de_vigilar_pasa_a_desconocido_cierra_el_incidente_y_es_idempotente()
    {
        var banco = ConVigencia().Pasos("FFF");
        var incidente = banco.Seguimiento.IncidenteAbierto!;

        var eventos = banco.Seguimiento.DejarDeVigilar(banco.Ahora.AddMinutes(1));

        eventos.Select(e => e.GetType()).ShouldBe([typeof(EstadoCambiado), typeof(IncidenteCerrado)]);
        banco.Estado.ShouldBe(EstadoMonitor.Desconocido);
        banco.Seguimiento.FallosSeguidos.ShouldBe(0);
        incidente.CerradoPor.ShouldBe(MotivoDeCierre.SinVigilancia);
        banco.Seguimiento.DejarDeVigilar(banco.Ahora.AddMinutes(2)).ShouldBeEmpty();
    }

    [Fact]
    public void Dejar_de_vigilar_nunca_anota_un_cambio_anterior_al_estado_actual()
    {
        var banco = ConVigencia().Pasos("OF");
        var desde = banco.Seguimiento.Desde;

        var cambio = banco.Seguimiento.DejarDeVigilar(desde.AddHours(-1)).OfType<EstadoCambiado>().Single();

        cambio.Momento.ShouldBe(desde);
    }

    [Fact]
    public void La_vigencia_de_un_monitor_es_de_tres_intervalos()
    {
        var monitor = Monitores.Monitor.Crear("Web", new ConfiguracionHttp(new Uri("https://ejemplo.com/")), TimeSpan.FromMinutes(5), 3, null, null, Banco.Inicio).Valor;

        monitor.VigenciaDeUnaComprobacion.ShouldBe(TimeSpan.FromMinutes(15));
        ReglasDeSeguimiento.De(monitor).Vigencia.ShouldBe(TimeSpan.FromMinutes(15));
    }
}
