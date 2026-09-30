using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using Vigia.Dominio.Monitores;

namespace Vigia.Datos.Persistencia;

/// <summary>
/// Guarda la configuración de un monitor como un documento JSON en una columna <c>jsonb</c>. Cada tipo
/// de monitor tiene campos distintos, y en lugar de una tabla con veinte columnas casi siempre vacías
/// se guarda un documento con su tipo, que también permite añadir opciones nuevas sin migrar la tabla.
/// </summary>
/// <remarks>
/// El dominio no sabe nada de JSON: las clases de configuración no llevan atributos de serialización,
/// y toda la traducción está aquí, en la capa de datos.
/// </remarks>
public sealed class ConversorConfiguracion()
    : ValueConverter<ConfiguracionMonitor, string>(
        configuracion => Serializar(configuracion),
        texto => Deserializar(texto))
{
    private static readonly JsonSerializerOptions Opciones = CrearOpciones();

    public static string Serializar(ConfiguracionMonitor configuracion)
    {
        ArgumentNullException.ThrowIfNull(configuracion);

        // Se serializa el tipo concreto (no el abstracto) para que salgan todos sus campos.
        var nodo = JsonSerializer.SerializeToNode(configuracion, configuracion.GetType(), Opciones)!.AsObject();
        nodo["tipo"] = configuracion.Tipo.ToString();

        return nodo.ToJsonString();
    }

    public static ConfiguracionMonitor Deserializar(string texto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texto);

        var nodo = JsonNode.Parse(texto) as JsonObject ?? throw new JsonException("La configuración debe ser un objeto JSON.");

        var nombreDelTipo = nodo["tipo"] is JsonValue valor && valor.TryGetValue<string>(out var texto2) ? texto2 : null;

        if (!Enum.TryParse<TipoMonitor>(nombreDelTipo, ignoreCase: true, out var tipo) || !Enum.IsDefined(tipo))
        {
            throw new JsonException($"Tipo de monitor desconocido o ausente: «{nombreDelTipo}».");
        }

        return tipo switch
        {
            TipoMonitor.Http => (ConfiguracionMonitor?)nodo.Deserialize<ConfiguracionHttp>(Opciones),
            TipoMonitor.Tls => (ConfiguracionMonitor?)nodo.Deserialize<ConfiguracionTls>(Opciones),
            TipoMonitor.Dns => (ConfiguracionMonitor?)nodo.Deserialize<ConfiguracionDns>(Opciones),
            TipoMonitor.Tcp => (ConfiguracionMonitor?)nodo.Deserialize<ConfiguracionTcp>(Opciones),
            TipoMonitor.Icmp => (ConfiguracionMonitor?)nodo.Deserialize<ConfiguracionIcmp>(Opciones),
            _ => throw new JsonException($"Tipo de monitor desconocido: {tipo}."),
        } ?? throw new JsonException("La configuración está vacía.");
    }

    private static JsonSerializerOptions CrearOpciones()
    {
        var opciones = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        opciones.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        opciones.Converters.Add(new ConversorCodigosHttp());

        return opciones;
    }

    private sealed class ConversorCodigosHttp : JsonConverter<CodigosHttpEsperados>
    {
        public override CodigosHttpEsperados Read(ref Utf8JsonReader lector, Type tipo, JsonSerializerOptions opciones) =>
            CodigosHttpEsperados.TryParse(lector.GetString(), out var codigos)
                ? codigos
                : throw new JsonException("Los códigos HTTP esperados guardados no son válidos.");

        public override void Write(Utf8JsonWriter escritor, CodigosHttpEsperados valor, JsonSerializerOptions opciones) =>
            escritor.WriteStringValue(valor.Texto);
    }
}
