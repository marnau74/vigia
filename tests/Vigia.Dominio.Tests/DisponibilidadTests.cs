using Shouldly;

using Vigia.Dominio.Disponibilidad;
using Vigia.Dominio.Monitores;

namespace Vigia.Dominio.Tests;

public class TiemposPorEstadoTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static TramoDeEstado Tramo(EstadoMonitor estado, int desdeMinutos, int hastaMinutos) =>
        new(estado, T0.AddMinutes(desdeMinutos), T0.AddMinutes(hastaMinutos));

    [Fact]
    public void Los_tramos_se_suman_por_estado()
    {
        var tiempos = TiemposPorEstado.DeTramos(
            [Tramo(EstadoMonitor.Operativo, 0, 30), Tramo(EstadoMonitor.Caido, 30, 40), Tramo(EstadoMonitor.Operativo, 40, 60)],
            T0,
            T0.AddMinutes(60));

        tiempos[EstadoMonitor.Operativo].ShouldBe(TimeSpan.FromMinutes(50));
        tiempos[EstadoMonitor.Caido].ShouldBe(TimeSpan.FromMinutes(10));
        tiempos[EstadoMonitor.Mantenimiento].ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Solo_cuenta_lo_que_cae_dentro_del_periodo_pedido()
    {
        // Un tramo que empieza antes y acaba después del periodo solo aporta la parte de dentro.
        var tiempos = TiemposPorEstado.DeTramos([Tramo(EstadoMonitor.Caido, -100, 100)], T0, T0.AddMinutes(60));

        tiempos[EstadoMonitor.Caido].ShouldBe(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void Un_tramo_fuera_del_periodo_no_cuenta()
    {
        TiemposPorEstado.DeTramos([Tramo(EstadoMonitor.Caido, 100, 200)], T0, T0.AddMinutes(60))[EstadoMonitor.Caido].ShouldBe(TimeSpan.Zero);
        TiemposPorEstado.DeTramos([Tramo(EstadoMonitor.Caido, -50, -1)], T0, T0.AddMinutes(60))[EstadoMonitor.Caido].ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Los_cambios_de_estado_se_convierten_en_tramos_hasta_el_siguiente_cambio_y_el_ultimo_hasta_ahora()
    {
        var tramos = TiemposPorEstado.TramosDe(
            [(EstadoMonitor.Operativo, T0), (EstadoMonitor.Caido, T0.AddMinutes(30)), (EstadoMonitor.Operativo, T0.AddMinutes(40))],
            T0.AddMinutes(100));

        tramos.ShouldBe(
        [
            Tramo(EstadoMonitor.Operativo, 0, 30),
            Tramo(EstadoMonitor.Caido, 30, 40),
            Tramo(EstadoMonitor.Operativo, 40, 100),
        ]);
    }

    [Fact]
    public void Sin_cambios_no_hay_tramos_y_un_cambio_en_el_futuro_no_crea_un_tramo_negativo()
    {
        TiemposPorEstado.TramosDe([], T0).ShouldBeEmpty();
        TiemposPorEstado.TramosDe([(EstadoMonitor.Operativo, T0.AddMinutes(10))], T0).ShouldBeEmpty();
    }

    [Fact]
    public void Sumar_agregados_de_varias_horas()
    {
        var hora1 = TiemposPorEstado.DeTramos([Tramo(EstadoMonitor.Operativo, 0, 60)], T0, T0.AddHours(1));
        var hora2 = TiemposPorEstado.DeTramos([Tramo(EstadoMonitor.Caido, 60, 90), Tramo(EstadoMonitor.Operativo, 90, 120)], T0.AddHours(1), T0.AddHours(2));

        var suma = TiemposPorEstado.Sumar([hora1, hora2]);

        suma[EstadoMonitor.Operativo].ShouldBe(TimeSpan.FromMinutes(90));
        suma[EstadoMonitor.Caido].ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Dos_agregados_con_los_mismos_tiempos_son_iguales()
    {
        var a = TiemposPorEstado.DeTramos([Tramo(EstadoMonitor.Operativo, 0, 60)], T0, T0.AddHours(1));
        var b = TiemposPorEstado.DeTramos([Tramo(EstadoMonitor.Operativo, 0, 30), Tramo(EstadoMonitor.Operativo, 30, 60)], T0, T0.AddHours(1));

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(TiemposPorEstado.Vacio);
    }

    // --- Solo cuenta el tiempo vigilado ---------------------------------------------------------

    private static IEnumerable<DateTimeOffset> CadaMinuto(int desdeMinutos, int hastaMinutos) =>
        Enumerable.Range(desdeMinutos, hastaMinutos - desdeMinutos).Select(m => T0.AddMinutes(m));

    [Fact]
    public void Con_comprobaciones_continuas_el_tiempo_vigilado_es_el_de_los_tramos()
    {
        var tramos = new[] { Tramo(EstadoMonitor.Operativo, 0, 45), Tramo(EstadoMonitor.Caido, 45, 60) };

        var vigilados = TiemposPorEstado.DeTramosVigilados(tramos, CadaMinuto(0, 60), TimeSpan.FromMinutes(3), T0, T0.AddHours(1));

        vigilados.ShouldBe(TiemposPorEstado.DeTramos(tramos, T0, T0.AddHours(1)));
    }

    [Fact]
    public void Sin_comprobaciones_el_ultimo_estado_no_se_alarga_y_cuenta_como_desconocido()
    {
        // Operativo toda la hora según los cambios, pero solo hubo comprobaciones los diez primeros minutos (se pausó).
        var tiempos = TiemposPorEstado.DeTramosVigilados(
            [Tramo(EstadoMonitor.Operativo, -60, 60)],
            CadaMinuto(0, 10),
            TimeSpan.FromMinutes(3),
            T0,
            T0.AddHours(1));

        tiempos[EstadoMonitor.Operativo].ShouldBe(TimeSpan.FromMinutes(12), "la última, del minuto 9, vale hasta el 12");
        tiempos.Desconocido.ShouldBe(TimeSpan.FromMinutes(48));
        CalculadoraDisponibilidad.Calcular(tiempos)!.Value.Fraccion.ShouldBe(1m, "lo no vigilado no cuenta ni a favor ni en contra");
    }

    [Fact]
    public void Un_estado_caido_sin_vigilar_tampoco_penaliza()
    {
        var tiempos = TiemposPorEstado.DeTramosVigilados([Tramo(EstadoMonitor.Caido, 0, 60)], [], TimeSpan.FromMinutes(3), T0, T0.AddHours(1));

        tiempos.Caido.ShouldBe(TimeSpan.Zero);
        tiempos.Desconocido.ShouldBe(TimeSpan.FromHours(1));
        CalculadoraDisponibilidad.Calcular(tiempos).ShouldBeNull();
    }

    [Fact]
    public void Una_comprobacion_de_antes_del_periodo_cubre_su_principio_y_las_que_se_solapan_no_cuentan_dos_veces()
    {
        var tiempos = TiemposPorEstado.DeTramosVigilados(
            [Tramo(EstadoMonitor.Operativo, -10, 60)],
            [T0.AddMinutes(-1), T0.AddMinutes(30), T0.AddMinutes(31)],
            TimeSpan.FromMinutes(3),
            T0,
            T0.AddHours(1));

        tiempos[EstadoMonitor.Operativo].ShouldBe(TimeSpan.FromMinutes(2 + 4));
        (tiempos[EstadoMonitor.Operativo] + tiempos.Desconocido).ShouldBe(TimeSpan.FromHours(1));
    }
}

public class CalculadoraDisponibilidadTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static TiemposPorEstado Tiempos(params (EstadoMonitor Estado, TimeSpan Duracion)[] partes)
    {
        var tramos = new List<TramoDeEstado>();
        var cursor = T0;

        foreach (var (estado, duracion) in partes)
        {
            tramos.Add(new TramoDeEstado(estado, cursor, cursor + duracion));
            cursor += duracion;
        }

        return TiemposPorEstado.DeTramos(tramos, T0, cursor);
    }

    [Fact]
    public void Sin_caidas_es_el_cien_por_cien()
    {
        var disponibilidad = CalculadoraDisponibilidad.Calcular(Tiempos((EstadoMonitor.Operativo, TimeSpan.FromDays(30))));

        disponibilidad!.Value.Fraccion.ShouldBe(1m);
        disponibilidad.Value.Texto().ShouldBe("100.00 %");
    }

    [Fact]
    public void Cuarenta_y_tres_minutos_caido_en_treinta_dias_son_un_99_9_por_ciento()
    {
        // La pregunta clásica: «un mes de 30 días con 99,9 % permite 43 min 12 s de caída».
        var disponibilidad = CalculadoraDisponibilidad.Calcular(Tiempos(
            (EstadoMonitor.Operativo, TimeSpan.FromDays(15)),
            (EstadoMonitor.Caido, new TimeSpan(0, 43, 12)),
            (EstadoMonitor.Operativo, TimeSpan.FromDays(15) - new TimeSpan(0, 43, 12))));

        disponibilidad!.Value.Porcentaje(1).ShouldBe(99.9m);
        disponibilidad.Value.Texto(1).ShouldBe("99.9 %");
    }

    [Fact]
    public void El_mantenimiento_no_cuenta_ni_a_favor_ni_en_contra()
    {
        // 1 día de mantenimiento no sube ni baja el porcentaje: solo cuentan los otros 9 días.
        var con = CalculadoraDisponibilidad.Calcular(Tiempos(
            (EstadoMonitor.Operativo, TimeSpan.FromDays(8)),
            (EstadoMonitor.Caido, TimeSpan.FromDays(1)),
            (EstadoMonitor.Mantenimiento, TimeSpan.FromDays(1))));
        var sin = CalculadoraDisponibilidad.Calcular(Tiempos(
            (EstadoMonitor.Operativo, TimeSpan.FromDays(8)),
            (EstadoMonitor.Caido, TimeSpan.FromDays(1))));

        con.ShouldBe(sin);
        con!.Value.Fraccion.ShouldBe(8m / 9m);
    }

    [Fact]
    public void Un_mantenimiento_tapando_una_caida_no_la_borra_pero_el_tiempo_de_mantenimiento_no_penaliza()
    {
        var disponibilidad = CalculadoraDisponibilidad.Calcular(Tiempos(
            (EstadoMonitor.Mantenimiento, TimeSpan.FromHours(2)),
            (EstadoMonitor.Operativo, TimeSpan.FromHours(10))));

        disponibilidad!.Value.Fraccion.ShouldBe(1m);
    }

    [Fact]
    public void El_tiempo_desconocido_no_cuenta()
    {
        var disponibilidad = CalculadoraDisponibilidad.Calcular(Tiempos(
            (EstadoMonitor.Desconocido, TimeSpan.FromDays(5)),
            (EstadoMonitor.Operativo, TimeSpan.FromDays(4)),
            (EstadoMonitor.Caido, TimeSpan.FromDays(1))));

        disponibilidad!.Value.Fraccion.ShouldBe(0.8m);
    }

    [Fact]
    public void Sospechoso_y_degradado_cuentan_como_en_pie()
    {
        var disponibilidad = CalculadoraDisponibilidad.Calcular(Tiempos(
            (EstadoMonitor.Operativo, TimeSpan.FromHours(5)),
            (EstadoMonitor.Degradado, TimeSpan.FromHours(2)),
            (EstadoMonitor.Sospechoso, TimeSpan.FromHours(2)),
            (EstadoMonitor.Caido, TimeSpan.FromHours(1))));

        disponibilidad!.Value.Fraccion.ShouldBe(0.9m);
    }

    [Fact]
    public void Sin_datos_no_hay_disponibilidad_en_lugar_de_un_cien_por_cien_inventado()
    {
        CalculadoraDisponibilidad.Calcular(TiemposPorEstado.Vacio).ShouldBeNull();
        CalculadoraDisponibilidad.Calcular(Tiempos((EstadoMonitor.Desconocido, TimeSpan.FromDays(1)))).ShouldBeNull();
        CalculadoraDisponibilidad.Calcular(Tiempos((EstadoMonitor.Mantenimiento, TimeSpan.FromDays(1)))).ShouldBeNull();
    }

    [Fact]
    public void Todo_el_periodo_caido_es_cero()
    {
        CalculadoraDisponibilidad.Calcular(Tiempos((EstadoMonitor.Caido, TimeSpan.FromDays(1))))!.Value.Fraccion.ShouldBe(0m);
    }

    [Fact]
    public void Se_calcula_tambien_directamente_desde_los_tramos_y_un_periodo()
    {
        var tramos = new[]
        {
            new TramoDeEstado(EstadoMonitor.Operativo, T0, T0.AddHours(3)),
            new TramoDeEstado(EstadoMonitor.Caido, T0.AddHours(3), T0.AddHours(4)),
            new TramoDeEstado(EstadoMonitor.Operativo, T0.AddHours(4), T0.AddHours(24)),
        };

        CalculadoraDisponibilidad.Calcular(tramos, T0, T0.AddHours(4))!.Value.Fraccion.ShouldBe(0.75m);
        CalculadoraDisponibilidad.Calcular(tramos, T0, T0.AddHours(24))!.Value.Fraccion.ShouldBe(23m / 24m);
    }

    [Theory]
    [InlineData(0.999996, "99.99 %")] // no se puede enseñar 100 % si hubo una caída
    [InlineData(0.99999, "99.99 %")]
    [InlineData(0.9999, "99.99 %")]
    [InlineData(0.99995, "99.99 %")]
    [InlineData(0.9995, "99.95 %")]
    [InlineData(0.5, "50.00 %")]
    [InlineData(1, "100.00 %")]
    [InlineData(0, "0.00 %")]
    public void El_porcentaje_se_trunca_y_nunca_se_redondea_hacia_arriba(double fraccion, string esperado)
    {
        new PorcentajeDisponibilidad((decimal)fraccion).Texto().ShouldBe(esperado);
    }

    [Fact]
    public void El_texto_admite_otro_numero_de_decimales()
    {
        var disponibilidad = new PorcentajeDisponibilidad(0.99873m);

        disponibilidad.Texto(0).ShouldBe("99 %");
        disponibilidad.Texto(1).ShouldBe("99.8 %");
        disponibilidad.Texto(3).ShouldBe("99.873 %");
    }
}

public class EstadisticaTests
{
    private static IEnumerable<TimeSpan> Milisegundos(IEnumerable<int> valores) => valores.Select(v => TimeSpan.FromMilliseconds(v));

    [Theory]
    [InlineData(50, 50)]
    [InlineData(95, 95)]
    [InlineData(99, 99)]
    [InlineData(100, 100)]
    [InlineData(1, 1)]
    public void Los_percentiles_de_cien_muestras_del_1_al_100_son_el_propio_numero(int percentil, int esperado)
    {
        Estadistica.Percentil(Milisegundos(Enumerable.Range(1, 100)), percentil).ShouldBe(TimeSpan.FromMilliseconds(esperado));
    }

    [Fact]
    public void Las_muestras_no_tienen_que_venir_ordenadas()
    {
        Estadistica.Percentil(Milisegundos([90, 10, 50, 30, 70]), 50).ShouldBe(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void Un_valor_extremo_solo_se_nota_en_el_p95_y_no_en_la_mediana()
    {
        // Diecinueve respuestas de 100 ms y una de 5 s: la mediana no se entera, el p95 sí lo refleja como mucho al llegar al 100.
        var muestras = Milisegundos(Enumerable.Repeat(100, 19).Append(5000)).ToList();

        Estadistica.Percentil(muestras, 50).ShouldBe(TimeSpan.FromMilliseconds(100));
        Estadistica.Percentil(muestras, 95).ShouldBe(TimeSpan.FromMilliseconds(100));
        Estadistica.Percentil(muestras, 100).ShouldBe(TimeSpan.FromMilliseconds(5000));
    }

    [Fact]
    public void Con_una_sola_muestra_todos_los_percentiles_son_esa_muestra()
    {
        Estadistica.Percentil(Milisegundos([42]), 1).ShouldBe(TimeSpan.FromMilliseconds(42));
        Estadistica.Percentil(Milisegundos([42]), 99).ShouldBe(TimeSpan.FromMilliseconds(42));
    }

    [Fact]
    public void Sin_muestras_no_hay_percentil_ni_media()
    {
        Estadistica.Percentil([], 95).ShouldBeNull();
        Estadistica.Media([]).ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Un_percentil_fuera_de_rango_es_un_error_de_programacion(int percentil)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Estadistica.Percentil(Milisegundos([1]), percentil));
    }

    [Fact]
    public void La_media_es_el_promedio()
    {
        Estadistica.Media(Milisegundos([100, 200, 300])).ShouldBe(TimeSpan.FromMilliseconds(200));
    }
}
