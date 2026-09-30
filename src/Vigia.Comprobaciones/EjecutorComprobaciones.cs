using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Vigia.Comprobaciones.Dns;
using Vigia.Comprobaciones.Http;
using Vigia.Comprobaciones.Red;
using Vigia.Comprobaciones.Tls;
using Vigia.Dominio.Monitores;

namespace Vigia.Comprobaciones;

/// <summary>
/// Elige el comprobador que corresponde a cada configuración. El planificador solo conoce esta
/// clase: añadir un tipo nuevo es añadir un comprobador y registrarlo, sin tocar el planificador.
/// </summary>
public sealed class EjecutorComprobaciones
{
    private readonly Dictionary<TipoMonitor, IComprobador> _comprobadores;

    public EjecutorComprobaciones(IEnumerable<IComprobador> comprobadores)
    {
        ArgumentNullException.ThrowIfNull(comprobadores);

        _comprobadores = comprobadores.ToDictionary(c => c.Tipo);
    }

    public Task<ResultadoComprobacion> EjecutarAsync(ConfiguracionMonitor configuracion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuracion);

        return _comprobadores.TryGetValue(configuracion.Tipo, out var comprobador)
            ? comprobador.ComprobarAsync(configuracion, cancellationToken)
            : throw new InvalidOperationException($"No hay ningún comprobador registrado para el tipo {configuracion.Tipo}.");
    }
}

public static class ServiciosComprobaciones
{
    /// <summary>Registra los cinco comprobadores y la red que usan. Lo que ya esté registrado (un reloj, un resolvedor DNS) se respeta.</summary>
    public static IServiceCollection AddComprobaciones(this IServiceCollection servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        servicios.TryAddSingleton(TimeProvider.System);
        servicios.TryAddSingleton<IResolvedorDirecciones, ResolvedorSistema>();
        servicios.TryAddSingleton<IConector, ConectorSocket>();
        servicios.TryAddSingleton<IEnviadorPing, EnviadorPingSistema>();
        servicios.TryAddSingleton<GuardiaDeDestinos>();
        servicios.AddClientesHttp();

        AddComprobador<ComprobadorHttp>(servicios);
        AddComprobador<ComprobadorTls>(servicios);
        AddComprobador<ComprobadorDns>(servicios);
        AddComprobador<ComprobadorTcp>(servicios);
        AddComprobador<ComprobadorIcmp>(servicios);
        servicios.AddSingleton<EjecutorComprobaciones>();

        return servicios;
    }

    /// <summary>Se registra por su tipo (para inyectarlo o probarlo solo) y por su interfaz (para que el ejecutor lo encuentre).</summary>
    private static void AddComprobador<T>(IServiceCollection servicios)
        where T : class, IComprobador
    {
        servicios.AddSingleton<T>();
        servicios.AddSingleton<IComprobador>(proveedor => proveedor.GetRequiredService<T>());
    }
}
