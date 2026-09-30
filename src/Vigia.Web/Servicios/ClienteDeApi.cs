using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Components.Authorization;

using Vigia.Contratos;

namespace Vigia.Web.Servicios;

/// <summary>Un error de la API, tal como lo devuelve (<c>application/problem+json</c> con un código estable).</summary>
public sealed record ErrorDeApi(string Codigo, string Mensaje);

/// <summary>El resultado de una llamada a la API: el valor, o el error, o que la sesión caducó.</summary>
public sealed record RespuestaDeApi<T>(T? Valor, ErrorDeApi? Error, bool SesionCaducada)
{
    public bool EsExito => Error is null && !SesionCaducada;

}

/// <summary>Las formas de construir una <see cref="RespuestaDeApi{T}"/>.</summary>
public static class RespuestaDeApi
{
    public static RespuestaDeApi<T> Exito<T>(T valor) => new(valor, null, false);

    public static RespuestaDeApi<T> Fallo<T>(ErrorDeApi error) => new(default, error, false);

    public static RespuestaDeApi<T> Caducada<T>() => new(default, null, true);
}

/// <summary>Lo que el panel y la página de estado necesitan de la API. Es una interfaz para poder probar las páginas sin red.</summary>
public interface IClienteDeApi
{
    Task<RespuestaDeApi<RespuestaAcceso>> AccederAsync(string contrasena, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<IReadOnlyList<MonitorDto>>> MonitoresAsync(CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<MonitorDto>> MonitorAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<MonitorDto>> CrearMonitorAsync(CuerpoMonitor cuerpo, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<MonitorDto>> ModificarMonitorAsync(Guid id, CuerpoMonitor cuerpo, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<bool>> BorrarMonitorAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<bool>> CambiarActivoAsync(Guid id, bool activo, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<PruebaDto>> ProbarAsync(JsonElement configuracion, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<PruebaDto>> ProbarMonitorAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<IReadOnlyList<BarraDiaria>>> BarrasAsync(Guid id, int dias, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<IReadOnlyList<PuntoDeLatencia>>> LatenciaAsync(Guid id, int horas, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<DisponibilidadDto>> DisponibilidadAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<IReadOnlyList<IncidenteDto>>> IncidentesAsync(Guid? monitorId, bool soloAbiertos, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<IReadOnlyList<GrupoDto>>> GruposAsync(CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<GrupoDto>> CrearGrupoAsync(CuerpoGrupo cuerpo, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<bool>> BorrarGrupoAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<IReadOnlyList<MantenimientoDto>>> MantenimientosAsync(CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<MantenimientoDto>> CrearMantenimientoAsync(CuerpoMantenimiento cuerpo, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<bool>> BorrarMantenimientoAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RespuestaDeApi<EstadoPublico>> EstadoPublicoAsync(string slug, CancellationToken cancellationToken = default);
}

/// <summary>
/// El cliente HTTP de la API. El token de sesión va en la cookie de inicio de sesión del panel: cada llamada lo
/// lee del usuario actual, así que el panel nunca lo guarda en ningún otro sitio.
/// </summary>
public sealed class ClienteDeApi(HttpClient http, AuthenticationStateProvider estado) : IClienteDeApi
{
    public const string ClaimDelToken = "api_token";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<RespuestaDeApi<RespuestaAcceso>> AccederAsync(string contrasena, CancellationToken cancellationToken = default) =>
        EnviarAsync<RespuestaAcceso>(HttpMethod.Post, "api/acceso", new SolicitudAcceso(contrasena), conSesion: false, cancellationToken);

    public async Task<RespuestaDeApi<IReadOnlyList<MonitorDto>>> MonitoresAsync(CancellationToken cancellationToken = default) => Lista(await EnviarAsync<List<MonitorDto>>(HttpMethod.Get, "api/monitores", null, true, cancellationToken));

    public Task<RespuestaDeApi<MonitorDto>> MonitorAsync(Guid id, CancellationToken cancellationToken = default) => EnviarAsync<MonitorDto>(HttpMethod.Get, $"api/monitores/{id}", null, true, cancellationToken);

    public Task<RespuestaDeApi<MonitorDto>> CrearMonitorAsync(CuerpoMonitor cuerpo, CancellationToken cancellationToken = default) => EnviarAsync<MonitorDto>(HttpMethod.Post, "api/monitores", cuerpo, true, cancellationToken);

    public Task<RespuestaDeApi<MonitorDto>> ModificarMonitorAsync(Guid id, CuerpoMonitor cuerpo, CancellationToken cancellationToken = default) => EnviarAsync<MonitorDto>(HttpMethod.Put, $"api/monitores/{id}", cuerpo, true, cancellationToken);

    public Task<RespuestaDeApi<bool>> BorrarMonitorAsync(Guid id, CancellationToken cancellationToken = default) => SinCuerpoAsync(HttpMethod.Delete, $"api/monitores/{id}", cancellationToken);

    public Task<RespuestaDeApi<bool>> CambiarActivoAsync(Guid id, bool activo, CancellationToken cancellationToken = default) =>
        SinCuerpoAsync(HttpMethod.Post, $"api/monitores/{id}/{(activo ? "reanudar" : "pausar")}", cancellationToken);

    public Task<RespuestaDeApi<PruebaDto>> ProbarAsync(JsonElement configuracion, CancellationToken cancellationToken = default) =>
        EnviarAsync<PruebaDto>(HttpMethod.Post, "api/probar", new { configuracion }, true, cancellationToken);

    public Task<RespuestaDeApi<PruebaDto>> ProbarMonitorAsync(Guid id, CancellationToken cancellationToken = default) => EnviarAsync<PruebaDto>(HttpMethod.Post, $"api/monitores/{id}/probar", null, true, cancellationToken);

    public async Task<RespuestaDeApi<IReadOnlyList<BarraDiaria>>> BarrasAsync(Guid id, int dias, CancellationToken cancellationToken = default) => Lista(await EnviarAsync<List<BarraDiaria>>(HttpMethod.Get, $"api/monitores/{id}/barras?dias={dias}", null, true, cancellationToken));

    public async Task<RespuestaDeApi<IReadOnlyList<PuntoDeLatencia>>> LatenciaAsync(Guid id, int horas, CancellationToken cancellationToken = default) => Lista(await EnviarAsync<List<PuntoDeLatencia>>(HttpMethod.Get, $"api/monitores/{id}/latencia?horas={horas}", null, true, cancellationToken));

    public Task<RespuestaDeApi<DisponibilidadDto>> DisponibilidadAsync(Guid id, CancellationToken cancellationToken = default) => EnviarAsync<DisponibilidadDto>(HttpMethod.Get, $"api/monitores/{id}/disponibilidad", null, true, cancellationToken);

    public async Task<RespuestaDeApi<IReadOnlyList<IncidenteDto>>> IncidentesAsync(Guid? monitorId, bool soloAbiertos, CancellationToken cancellationToken = default) =>
        Lista(await EnviarAsync<List<IncidenteDto>>(
            HttpMethod.Get,
            monitorId is { } id ? $"api/monitores/{id}/incidentes?limite=20" : $"api/incidentes?abiertos={soloAbiertos.ToString().ToLowerInvariant()}&limite=50",
            null,
            true,
            cancellationToken));

    public async Task<RespuestaDeApi<IReadOnlyList<GrupoDto>>> GruposAsync(CancellationToken cancellationToken = default) => Lista(await EnviarAsync<List<GrupoDto>>(HttpMethod.Get, "api/grupos", null, true, cancellationToken));

    public Task<RespuestaDeApi<GrupoDto>> CrearGrupoAsync(CuerpoGrupo cuerpo, CancellationToken cancellationToken = default) => EnviarAsync<GrupoDto>(HttpMethod.Post, "api/grupos", cuerpo, true, cancellationToken);

    public Task<RespuestaDeApi<bool>> BorrarGrupoAsync(Guid id, CancellationToken cancellationToken = default) => SinCuerpoAsync(HttpMethod.Delete, $"api/grupos/{id}", cancellationToken);

    public async Task<RespuestaDeApi<IReadOnlyList<MantenimientoDto>>> MantenimientosAsync(CancellationToken cancellationToken = default) => Lista(await EnviarAsync<List<MantenimientoDto>>(HttpMethod.Get, "api/mantenimientos", null, true, cancellationToken));

    public Task<RespuestaDeApi<MantenimientoDto>> CrearMantenimientoAsync(CuerpoMantenimiento cuerpo, CancellationToken cancellationToken = default) => EnviarAsync<MantenimientoDto>(HttpMethod.Post, "api/mantenimientos", cuerpo, true, cancellationToken);

    public Task<RespuestaDeApi<bool>> BorrarMantenimientoAsync(Guid id, CancellationToken cancellationToken = default) => SinCuerpoAsync(HttpMethod.Delete, $"api/mantenimientos/{id}", cancellationToken);

    public Task<RespuestaDeApi<EstadoPublico>> EstadoPublicoAsync(string slug, CancellationToken cancellationToken = default) =>
        EnviarAsync<EstadoPublico>(HttpMethod.Get, $"api/publico/estado/{Uri.EscapeDataString(slug)}", null, conSesion: false, cancellationToken);

    private static RespuestaDeApi<IReadOnlyList<T>> Lista<T>(RespuestaDeApi<List<T>> respuesta) =>
        respuesta.EsExito ? RespuestaDeApi.Exito<IReadOnlyList<T>>(respuesta.Valor!) : new RespuestaDeApi<IReadOnlyList<T>>(null, respuesta.Error, respuesta.SesionCaducada);

    private async Task<RespuestaDeApi<bool>> SinCuerpoAsync(HttpMethod metodo, string ruta, CancellationToken cancellationToken)
    {
        using var respuesta = await EnviarPeticionAsync(metodo, ruta, null, true, cancellationToken);

        return await InterpretarAsync<bool>(respuesta, leerCuerpo: false, cancellationToken);
    }

    private async Task<RespuestaDeApi<T>> EnviarAsync<T>(HttpMethod metodo, string ruta, object? cuerpo, bool conSesion, CancellationToken cancellationToken)
    {
        using var respuesta = await EnviarPeticionAsync(metodo, ruta, cuerpo, conSesion, cancellationToken);

        return await InterpretarAsync<T>(respuesta, leerCuerpo: true, cancellationToken);
    }

    private async Task<HttpResponseMessage> EnviarPeticionAsync(HttpMethod metodo, string ruta, object? cuerpo, bool conSesion, CancellationToken cancellationToken)
    {
        using var peticion = new HttpRequestMessage(metodo, ruta);

        if (cuerpo is not null)
        {
            peticion.Content = JsonContent.Create(cuerpo, options: Json);
        }

        if (conSesion)
        {
            var usuario = (await estado.GetAuthenticationStateAsync()).User;
            var token = usuario.FindFirst(ClaimDelToken)?.Value;

            if (!string.IsNullOrEmpty(token))
            {
                peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        try
        {
            return await http.SendAsync(peticion, cancellationToken);
        }
        catch (HttpRequestException)
        {
            // La API no contesta: se presenta como una respuesta de error normal, sin tumbar la página.
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private static async Task<RespuestaDeApi<T>> InterpretarAsync<T>(HttpResponseMessage respuesta, bool leerCuerpo, CancellationToken cancellationToken)
    {
        if (respuesta.StatusCode == HttpStatusCode.Unauthorized && respuesta.RequestMessage?.RequestUri?.AbsolutePath != "/api/acceso")
        {
            return RespuestaDeApi.Caducada<T>();
        }

        if (respuesta.IsSuccessStatusCode)
        {
            if (!leerCuerpo)
            {
                return RespuestaDeApi.Exito<T>(default!);
            }

            try
            {
                var valor = await respuesta.Content.ReadFromJsonAsync<T>(Json, cancellationToken);

                return valor is null ? RespuestaDeApi.Fallo<T>(new ErrorDeApi("respuesta_vacia", "La API respondió sin datos.")) : RespuestaDeApi.Exito(valor);
            }
            catch (JsonException)
            {
                return RespuestaDeApi.Fallo<T>(new ErrorDeApi("respuesta_ilegible", "La respuesta de la API no tiene el formato esperado."));
            }
        }

        return RespuestaDeApi.Fallo<T>(await LeerErrorAsync(respuesta, cancellationToken));
    }

    private static async Task<ErrorDeApi> LeerErrorAsync(HttpResponseMessage respuesta, CancellationToken cancellationToken)
    {
        if (respuesta.StatusCode is HttpStatusCode.ServiceUnavailable && respuesta.Content.Headers.ContentLength is null or 0)
        {
            return new ErrorDeApi("api_no_disponible", "No se puede contactar con la API. Inténtalo de nuevo en unos segundos.");
        }

        if (respuesta.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new ErrorDeApi("demasiadas_peticiones", "Demasiados intentos seguidos. Espera un minuto.");
        }

        try
        {
            var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
            var codigo = problema.TryGetProperty("codigo", out var c) ? c.GetString() : null;
            var titulo = problema.TryGetProperty("title", out var t) ? t.GetString() : null;

            return new ErrorDeApi(codigo ?? $"http_{(int)respuesta.StatusCode}", titulo ?? $"La API respondió {(int)respuesta.StatusCode}.");
        }
        catch (Exception excepcion) when (excepcion is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new ErrorDeApi($"http_{(int)respuesta.StatusCode}", $"La API respondió {(int)respuesta.StatusCode}.");
        }
    }
}
