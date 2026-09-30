using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

using Vigia.Web.Servicios;

namespace Vigia.Web.Components;

/// <summary>Cómo se dibujan las páginas del panel: interactivas (SignalR), sin prerenderizar para no llamar dos veces a la API.</summary>
public static class Modos
{
    public static readonly IComponentRenderMode Interactivo = new InteractiveServerRenderMode(prerender: false);
}

/// <summary>Lo común a las páginas del panel: la API, la navegación, el reloj y qué hacer cuando la sesión caduca.</summary>
public abstract class PaginaDelPanel : ComponentBase
{
    [Inject]
    protected IClienteDeApi Api { get; set; } = null!;

    [Inject]
    protected NavigationManager Navegacion { get; set; } = null!;

    [Inject]
    protected TimeProvider Reloj { get; set; } = null!;

    /// <summary>
    /// Interpreta la respuesta de la API. Si la sesión caducó, lleva a entrar de nuevo (y devuelve false); si falló por otra razón,
    /// devuelve false con el mensaje; si fue bien, true.
    /// </summary>
    protected bool Correcta<T>(RespuestaDeApi<T> respuesta, out string? error)
    {
        ArgumentNullException.ThrowIfNull(respuesta);

        if (respuesta.SesionCaducada)
        {
            error = null;
            Navegacion.NavigateTo("/entrar", forceLoad: true);

            return false;
        }

        error = respuesta.Error?.Mensaje;

        return respuesta.EsExito;
    }
}
