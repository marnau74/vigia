// El worker hace las comprobaciones, aunque la API se reinicie o se despliegue. Es un servicio web
// mínimo solo para exponer /health y /alive a Aspire, a Docker y al proxy.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();

/// <summary>Visible para los tests de integración (WebApplicationFactory).</summary>
public partial class Program;
