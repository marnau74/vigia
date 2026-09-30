using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Vigia.Comprobaciones.Red;

namespace Vigia.Comprobaciones.Tests.Apoyo;

/// <summary>Un DNS de mentira: cada nombre resuelve a lo que diga el test, y se cuentan las consultas.</summary>
public sealed class ResolvedorFalso : IResolvedorDirecciones
{
    private readonly Dictionary<string, Func<int, IPAddress[]>> _respuestas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _llamadas = new(StringComparer.OrdinalIgnoreCase);

    public ResolvedorFalso Asignar(string nombre, params string[] direcciones) =>
        AsignarSecuencia(nombre, _ => [.. direcciones.Select(IPAddress.Parse)]);

    /// <summary>La respuesta depende de qué consulta es (1.ª, 2.ª…): así se simula el <i>DNS rebinding</i>.</summary>
    public ResolvedorFalso AsignarSecuencia(string nombre, Func<int, IPAddress[]> respuesta)
    {
        _respuestas[nombre] = respuesta;
        return this;
    }

    public int Llamadas(string nombre) => _llamadas.GetValueOrDefault(nombre);

    public Task<IReadOnlyList<IPAddress>> ResolverAsync(string host, CancellationToken cancellationToken)
    {
        var numero = _llamadas[host] = _llamadas.GetValueOrDefault(host) + 1;

        return _respuestas.TryGetValue(host, out var respuesta)
            ? Task.FromResult<IReadOnlyList<IPAddress>>(respuesta(numero))
            : throw new NoSeResuelveException($"El nombre «{host}» no se resuelve (HostNotFound).");
    }
}

/// <summary>Anota a qué direcciones se pidió conectar y las lleva al servidor local que el test indique.</summary>
public sealed class ConectorHaciaPuerto(int puertoLocal) : IConector
{
    public List<(IPAddress Direccion, int Puerto)> Peticiones { get; } = [];

    public async Task<Socket> ConectarAsync(IPAddress direccion, int puerto, CancellationToken cancellationToken)
    {
        lock (Peticiones)
        {
            Peticiones.Add((direccion, puerto));
        }

        return await new ConectorSocket().ConectarAsync(IPAddress.Loopback, puertoLocal, cancellationToken);
    }
}

/// <summary>Una conexión que nunca termina de establecerse (destino que no contesta).</summary>
public sealed class ConectorColgado : IConector
{
    public TaskCompletionSource Intentando { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<Socket> ConectarAsync(IPAddress direccion, int puerto, CancellationToken cancellationToken)
    {
        Intentando.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);

        throw new InvalidOperationException("No se debería llegar aquí.");
    }
}

/// <summary>Falla para unas direcciones y, para las demás, conecta al servidor local.</summary>
public sealed class ConectorQueFallaPara(HashSet<string> falla, int puertoLocal) : IConector
{
    public List<IPAddress> Intentos { get; } = [];

    public async Task<Socket> ConectarAsync(IPAddress direccion, int puerto, CancellationToken cancellationToken)
    {
        Intentos.Add(direccion);

        return falla.Contains(direccion.ToString())
            ? throw new SocketException((int)SocketError.ConnectionRefused)
            : await new ConectorSocket().ConectarAsync(IPAddress.Loopback, puertoLocal, cancellationToken);
    }
}

public sealed class EnviadorPingFalso(Func<IPAddress, Task<RespuestaPing>> respuesta) : IEnviadorPing
{
    public List<IPAddress> Destinos { get; } = [];

    public Task<RespuestaPing> EnviarAsync(IPAddress direccion, TimeSpan tiempoMaximo, CancellationToken cancellationToken)
    {
        Destinos.Add(direccion);
        return respuesta(direccion);
    }
}

/// <summary>Los servicios de las comprobaciones, con el reloj, el DNS y la red que el test decida.</summary>
public sealed class Entorno : IDisposable
{
    private readonly ServiceProvider _proveedor;

    public Entorno(Action<IServiceCollection>? configurar = null, DateTimeOffset? ahora = null)
    {
        Reloj = new FakeTimeProvider(ahora ?? DateTimeOffset.UtcNow);
        Resolvedor = new ResolvedorFalso();

        var servicios = new ServiceCollection();
        servicios.AddSingleton<TimeProvider>(Reloj);
        servicios.AddSingleton<IResolvedorDirecciones>(Resolvedor);
        configurar?.Invoke(servicios);
        servicios.AddComprobaciones();

        _proveedor = servicios.BuildServiceProvider();
    }

    public FakeTimeProvider Reloj { get; }

    public ResolvedorFalso Resolvedor { get; }

    public T Obtener<T>()
        where T : notnull => _proveedor.GetRequiredService<T>();

    public void Dispose() => _proveedor.Dispose();
}
