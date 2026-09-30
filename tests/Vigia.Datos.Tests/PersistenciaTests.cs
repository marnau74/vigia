using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Vigia.Datos.Persistencia;
using Vigia.Dominio.Mantenimiento;
using Vigia.Dominio.Monitores;

namespace Vigia.Datos.Tests;

public class MigracionesTests : BaseDeDatosTest
{
    [Fact]
    public async Task La_migracion_crea_todas_las_tablas_con_nombres_en_snake_case()
    {
        await using var db = NuevoContexto();

        var tablas = await db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' AND table_name !~ '^resultados_[0-9]' ORDER BY table_name")
            .ToListAsync(Cancelacion);

        tablas.ShouldBe(
        [
            "__EFMigrationsHistory", "avisos", "cambios_estado", "grupos", "incidentes", "monitores", "resultados", "resultados_dia",
            "resultados_hora", "seguimientos", "ventanas_mantenimiento",
        ]);
    }

    [Fact]
    public async Task La_tabla_de_resultados_esta_particionada_por_rango_de_fecha()
    {
        var estrategia = await ConsultarAsync<string>(
            "SELECT partstrat::text AS \"Value\" FROM pg_partitioned_table WHERE partrelid = 'resultados'::regclass");

        estrategia.ShouldBe("r", "«r» es particionado por rango (range)");
    }

    [Fact]
    public async Task Una_base_recien_migrada_ya_tiene_las_particiones_del_mes_actual_y_el_siguiente()
    {
        await using var db = NuevoContexto();

        var particiones = await new Particiones(db).ListarAsync(Cancelacion);

        particiones.Count.ShouldBe(2);
        particiones[0].ShouldBe(Particiones.InicioDelMes(DateTimeOffset.UtcNow));
        particiones[1].ShouldBe(Particiones.InicioDelMes(DateTimeOffset.UtcNow).AddMonths(1));
    }

    [Fact]
    public async Task Hay_un_indice_brin_por_fecha_y_uno_por_monitor_y_fecha_heredados_por_las_particiones()
    {
        var indices = await ConsultarAsync<string>(
            "SELECT string_agg(indexdef, ' | ' ORDER BY indexname) AS \"Value\" FROM pg_indexes WHERE tablename LIKE 'resultados_20%' AND indexname NOT LIKE '%pkey'");

        indices.ShouldContain("USING brin (momento)");
        indices.ShouldContain("(monitor_id, momento DESC)");
    }

    [Fact]
    public async Task Un_monitor_no_puede_tener_dos_incidentes_abiertos_a_la_vez()
    {
        var monitor = await GuardarMonitorAsync();
        await using var db = NuevoContexto();
        var uno = Vigia.Dominio.Seguimiento.Incidente.Abrir(monitor.Id, Ahora, "primero", 3);
        var otro = Vigia.Dominio.Seguimiento.Incidente.Abrir(monitor.Id, Ahora.AddMinutes(1), "segundo", 3);
        db.Incidentes.AddRange(uno, otro);

        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancelacion));

        error.InnerException!.Message.ShouldContain("ux_incidentes_un_abierto_por_monitor");
    }
}

public class ConversorConfiguracionTests
{
    public static TheoryData<ConfiguracionMonitor> Configuraciones => new()
    {
        new ConfiguracionHttp(new Uri("https://ejemplo.com/salud?x=1")),
        new ConfiguracionHttp(new Uri("http://192.168.1.10:8080/"))
        {
            Metodo = "HEAD",
            CodigosEsperados = Codigos("200-299,404"),
            PalabraClave = "Bienvenido",
            MaxRedirecciones = 2,
            VerificarCertificado = false,
            PermitirRedPrivada = true,
            TiempoMaximo = TimeSpan.FromSeconds(25),
        },
        new ConfiguracionTls("ejemplo.com") { Puerto = 8443, VerificarCertificado = false },
        new ConfiguracionDns("ejemplo.com", TipoRegistroDns.Mx) { Esperados = ["10 correo1.ejemplo.com", "20 correo2.ejemplo.com"], Servidor = "8.8.8.8:53" },
        new ConfiguracionDns("www.ejemplo.com", TipoRegistroDns.A),
        new ConfiguracionTcp("ejemplo.com", 22) { TiempoMaximo = TimeSpan.FromSeconds(3) },
        new ConfiguracionIcmp("router.casa") { PermitirRedPrivada = true },
    };

    private static CodigosHttpEsperados Codigos(string texto)
    {
        CodigosHttpEsperados.TryParse(texto, out var codigos).ShouldBeTrue();
        return codigos;
    }

    [Theory]
    [MemberData(nameof(Configuraciones))]
    public void Cada_configuracion_sobrevive_a_guardarla_y_leerla_con_todos_sus_campos(ConfiguracionMonitor original)
    {
        var json = ConversorConfiguracion.Serializar(original);
        var leida = ConversorConfiguracion.Deserializar(json);

        leida.GetType().ShouldBe(original.GetType());
        leida.Tipo.ShouldBe(original.Tipo);
        leida.TiempoMaximo.ShouldBe(original.TiempoMaximo);
        leida.PermitirRedPrivada.ShouldBe(original.PermitirRedPrivada);

        // Los records comparan los campos, salvo las listas (por referencia): se comparan a mano.
        ConversorConfiguracion.Serializar(leida).ShouldBe(json);

        switch (original)
        {
            case ConfiguracionHttp http:
                var h = (ConfiguracionHttp)leida;
                h.Url.ShouldBe(http.Url);
                h.Metodo.ShouldBe(http.Metodo);
                h.CodigosEsperados.Texto.ShouldBe(http.CodigosEsperados.Texto);
                h.PalabraClave.ShouldBe(http.PalabraClave);
                h.MaxRedirecciones.ShouldBe(http.MaxRedirecciones);
                h.VerificarCertificado.ShouldBe(http.VerificarCertificado);
                break;
            case ConfiguracionDns dns:
                var d = (ConfiguracionDns)leida;
                d.Nombre.ShouldBe(dns.Nombre);
                d.Registro.ShouldBe(dns.Registro);
                d.Esperados.ShouldBe(dns.Esperados);
                d.Servidor.ShouldBe(dns.Servidor);
                break;
            case ConfiguracionTls tls:
                var t = (ConfiguracionTls)leida;
                (t.Host, t.Puerto, t.VerificarCertificado).ShouldBe((tls.Host, tls.Puerto, tls.VerificarCertificado));
                break;
            case ConfiguracionTcp tcp:
                ((ConfiguracionTcp)leida).ShouldBe(tcp);
                break;
            case ConfiguracionIcmp icmp:
                ((ConfiguracionIcmp)leida).ShouldBe(icmp);
                break;
        }
    }

    [Fact]
    public void El_documento_lleva_su_tipo_y_no_guarda_los_campos_vacios()
    {
        var json = JsonNode.Parse(ConversorConfiguracion.Serializar(new ConfiguracionTcp("ejemplo.com", 22)))!.AsObject();

        json["tipo"]!.GetValue<string>().ShouldBe("Tcp");
        json["host"]!.GetValue<string>().ShouldBe("ejemplo.com");
        json["puerto"]!.GetValue<int>().ShouldBe(22);
        json.ContainsKey("esperados").ShouldBeFalse();
    }

    [Fact]
    public void Un_tipo_desconocido_en_la_base_de_datos_falla_con_un_mensaje_claro()
    {
        var error = Should.Throw<Exception>(() => ConversorConfiguracion.Deserializar("""{"tipo":"Ftp","host":"x"}"""));

        error.ShouldNotBeNull();
    }

    [Fact]
    public void Los_codigos_http_invalidos_guardados_no_se_aceptan_en_silencio()
    {
        Should.Throw<System.Text.Json.JsonException>(() =>
            ConversorConfiguracion.Deserializar("""{"tipo":"Http","url":"https://ejemplo.com/","codigosEsperados":"abc"}"""));
    }
}

public class MonitoresEnBaseDeDatosTests : BaseDeDatosTest
{
    [Fact]
    public async Task Un_monitor_se_guarda_y_se_recupera_con_su_configuracion_y_sus_reglas()
    {
        var configuracion = new ConfiguracionHttp(new Uri("https://ejemplo.com/salud")) { PalabraClave = "ok", TiempoMaximo = TimeSpan.FromSeconds(15) };
        var guardado = await GuardarMonitorAsync("API de pagos", configuracion: configuracion);

        await using var db = NuevoContexto();
        var leido = (await new RepositorioMonitores(db).ListarActivosAsync(Cancelacion)).Single();

        leido.Id.ShouldBe(guardado.Id);
        leido.Nombre.ShouldBe("API de pagos");
        leido.Tipo.ShouldBe(TipoMonitor.Http);
        leido.Intervalo.ShouldBe(TimeSpan.FromSeconds(60));
        leido.FallosParaIncidente.ShouldBe(3);
        leido.UmbralLento.ShouldBe(TimeSpan.FromSeconds(2));
        leido.Activo.ShouldBeTrue();
        leido.CreadoEn.ShouldBe(guardado.CreadoEn);
        var http = leido.Configuracion.ShouldBeOfType<ConfiguracionHttp>();
        http.Url.ShouldBe(configuracion.Url);
        http.PalabraClave.ShouldBe("ok");
        http.TiempoMaximo.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task La_configuracion_se_guarda_como_un_documento_jsonb_consultable()
    {
        await GuardarMonitorAsync(configuracion: new ConfiguracionTcp("db.ejemplo.com", 5432));

        (await ConsultarAsync<string>("SELECT configuracion->>'host' AS \"Value\" FROM monitores")).ShouldBe("db.ejemplo.com");
        (await ConsultarAsync<string>("SELECT pg_typeof(configuracion)::text AS \"Value\" FROM monitores")).ShouldBe("jsonb");
    }

    [Fact]
    public async Task Los_monitores_pausados_no_se_listan_como_activos()
    {
        var activo = await GuardarMonitorAsync("Activo");
        var pausado = await GuardarMonitorAsync("Pausado");

        await using (var db = NuevoContexto())
        {
            var fila = await db.Monitores.SingleAsync(m => m.Id == pausado.Id, Cancelacion);
            fila.Pausar();
            await db.SaveChangesAsync(Cancelacion);
        }

        await using var lectura = NuevoContexto();
        (await new RepositorioMonitores(lectura).ListarActivosAsync(Cancelacion)).Select(m => m.Id).ShouldBe([activo.Id]);
    }

    [Fact]
    public async Task Leer_un_monitor_sin_tocarlo_no_genera_ninguna_modificacion()
    {
        // Si la comparación de la configuración diera «distinto» siempre, cada lectura acabaría en un UPDATE.
        await GuardarMonitorAsync(configuracion: new ConfiguracionDns("ejemplo.com", TipoRegistroDns.A) { Esperados = ["93.184.216.34"] });

        await using var db = NuevoContexto();
        _ = await db.Monitores.SingleAsync(Cancelacion);

        db.ChangeTracker.DetectChanges();
        db.ChangeTracker.HasChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task Un_cambio_de_configuracion_si_se_detecta_y_se_guarda()
    {
        var monitor = await GuardarMonitorAsync();

        await using (var db = NuevoContexto())
        {
            var fila = await db.Monitores.SingleAsync(m => m.Id == monitor.Id, Cancelacion);
            fila.Modificar("Renombrado", new ConfiguracionHttp(new Uri("https://otra.example/")), TimeSpan.FromSeconds(120), 2, null, null).EsExito.ShouldBeTrue();
            db.ChangeTracker.DetectChanges();
            db.ChangeTracker.HasChanges().ShouldBeTrue();
            await db.SaveChangesAsync(Cancelacion);
        }

        await using var lectura = NuevoContexto();
        var leido = await lectura.Monitores.AsNoTracking().SingleAsync(Cancelacion);
        leido.Nombre.ShouldBe("Renombrado");
        leido.Intervalo.ShouldBe(TimeSpan.FromSeconds(120));
        ((ConfiguracionHttp)leido.Configuracion).Url.Host.ShouldBe("otra.example");
    }

    [Fact]
    public async Task Las_ventanas_de_mantenimiento_vigentes_incluyen_las_de_ahora_y_las_futuras_pero_no_las_pasadas()
    {
        var monitor = await GuardarMonitorAsync();

        await using (var db = NuevoContexto())
        {
            db.VentanasMantenimiento.AddRange(
                VentanaMantenimiento.Crear([monitor.Id], Ahora.AddDays(-3), Ahora.AddDays(-2), "pasada").Valor,
                VentanaMantenimiento.Crear([monitor.Id], Ahora.AddHours(-1), Ahora.AddHours(1), "en curso").Valor,
                VentanaMantenimiento.Crear([monitor.Id, Guid.NewGuid()], Ahora.AddDays(1), Ahora.AddDays(1).AddHours(2), "futura").Valor);
            await db.SaveChangesAsync(Cancelacion);
        }

        await using var lectura = NuevoContexto();
        var vigentes = await new RepositorioMonitores(lectura).ListarVentanasVigentesAsync(Ahora, Cancelacion);

        vigentes.Select(v => v.Motivo).ShouldBe(["en curso", "futura"], ignoreOrder: true);
        vigentes.Single(v => v.Motivo == "futura").MonitorIds.Count.ShouldBe(2);
        VentanaMantenimiento.AlgunaCubre(vigentes, monitor.Id, Ahora).ShouldBeTrue();
    }
}
