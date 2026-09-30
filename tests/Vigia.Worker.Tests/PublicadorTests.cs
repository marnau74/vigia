using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Shouldly;

using Vigia.Datos.Persistencia;
using Vigia.Dominio.Monitores;
using Vigia.Dominio.Seguimiento;
using Vigia.Worker.Planificacion;

namespace Vigia.Worker.Tests;

/// <summary>Lo que el worker cuenta a la API por NOTIFY cada vez que guarda una comprobación.</summary>
public class PublicadorTests : BaseWorkerTest
{
    private static readonly Guid Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static async Task<(NpgsqlConnection Conexion, ConcurrentQueue<string> Cargas)> EscucharAsync(string cadena)
    {
        var conexion = new NpgsqlConnection(cadena);
        var cargas = new ConcurrentQueue<string>();
        await conexion.OpenAsync(Cancelacion);
        conexion.Notification += (_, evento) => cargas.Enqueue(evento.Payload);

        await using (var comando = new NpgsqlCommand($"LISTEN {Notificaciones.ComprobacionGuardada}", conexion))
        {
            await comando.ExecuteNonQueryAsync(Cancelacion);
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (true)
                    {
                        await conexion.WaitAsync(Cancelacion);
                    }
                }
                catch (Exception)
                {
                    // Se cierra al terminar el test.
                }
            },
            CancellationToken.None);

        return (conexion, cargas);
    }

    private static async Task<ComprobacionPublicada> PublicarAsync(EntornoDeWorker entorno, ComprobacionRegistrada comprobacion)
    {
        var (conexion, cargas) = await EscucharAsync(entorno.CadenaConexion);
        await using var _ = conexion;

        await new PublicadorDeComprobaciones(entorno.Servicios.GetRequiredService<IServiceScopeFactory>()).ManejarAsync(comprobacion, Cancelacion);

        await EsperarAsync(() => !cargas.IsEmpty);

        return ComprobacionPublicada.Leer(cargas.Single()).ShouldNotBeNull();
    }

    private static ComprobacionRegistrada Comprobacion(params EventoDeSeguimiento[] eventos) =>
        new(Id, Inicio, true, TimeSpan.FromMilliseconds(87.9), EstadoMonitor.Operativo, eventos);

    [Fact]
    public async Task Una_comprobacion_corriente_se_publica_con_su_estado_y_latencia_sin_incidente()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);

        var publicada = await PublicarAsync(entorno, Comprobacion());

        publicada.MonitorId.ShouldBe(Id);
        publicada.Momento.ShouldBe(Inicio);
        publicada.Correcto.ShouldBeTrue();
        publicada.LatenciaMs.ShouldBe(87);
        publicada.Estado.ShouldBe(EstadoMonitor.Operativo);
        publicada.Incidente.ShouldBeNull();
    }

    [Fact]
    public async Task Abrir_un_incidente_se_marca_como_abierto()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var incidente = Incidente.Abrir(Id, Inicio, "causa", 3);

        var publicada = await PublicarAsync(entorno, Comprobacion(new IncidenteAbierto(Id, incidente, Inicio)) with { Correcto = false, Estado = EstadoMonitor.Caido });

        publicada.Incidente.ShouldBe("abierto");
        publicada.Estado.ShouldBe(EstadoMonitor.Caido);
        publicada.Correcto.ShouldBeFalse();
    }

    [Fact]
    public async Task Cerrar_un_incidente_se_marca_como_cerrado()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);
        var incidente = Incidente.Abrir(Id, Inicio.AddMinutes(-5), "causa", 3);
        incidente.Cerrar(Inicio, MotivoDeCierre.Recuperado);

        var publicada = await PublicarAsync(entorno, Comprobacion(new IncidenteCerrado(Id, incidente, MotivoDeCierre.Recuperado, Inicio)));

        publicada.Incidente.ShouldBe("cerrado");
    }

    [Fact]
    public async Task Un_cambio_de_estado_sin_incidente_no_lo_marca()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);

        var publicada = await PublicarAsync(entorno, Comprobacion(new EstadoCambiado(Id, EstadoMonitor.Operativo, EstadoMonitor.Degradado, Inicio)) with { Estado = EstadoMonitor.Degradado });

        publicada.Incidente.ShouldBeNull();
        publicada.Estado.ShouldBe(EstadoMonitor.Degradado);
    }

    [Fact]
    public async Task Una_latencia_enorme_no_desborda()
    {
        await using var entorno = new EntornoDeWorker(Cadena, Inicio);

        var publicada = await PublicarAsync(entorno, Comprobacion() with { Latencia = TimeSpan.FromDays(40) });

        publicada.LatenciaMs.ShouldBe(int.MaxValue);
    }
}
