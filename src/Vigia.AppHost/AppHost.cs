// Entorno local completo con un solo comando: dotnet run --project src/Vigia.AppHost
// Levanta PostgreSQL y Mailpit en contenedores, y la API y el worker conectados a ellos, con el
// panel de Aspire (trazas, métricas y logs) en el navegador. Los correos de aviso no salen a
// internet: se leen en la bandeja de Mailpit, en http://localhost:8026.

var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL 17: la misma versión que usan los tests.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("17")
    .WithDataVolume("vigia-postgres")
    .WithLifetime(ContainerLifetime.Persistent);

var baseDeDatos = postgres.AddDatabase("vigia");

// Mailpit: un servidor de correo de pruebas que guarda todo lo que recibe y lo enseña en una web.
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit")
    .WithEndpoint(port: 1026, targetPort: 1025, name: "smtp")
    .WithHttpEndpoint(port: 8026, targetPort: 8025, name: "bandeja");

var smtp = mailpit.GetEndpoint("smtp");

builder.AddProject<Projects.Vigia_Api>("api")
    .WithReference(baseDeDatos)
    .WaitFor(baseDeDatos)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Vigia_Worker>("worker")
    .WithReference(baseDeDatos)
    .WaitFor(baseDeDatos)
    .WithEnvironment("Correo__Servidor", smtp.Property(EndpointProperty.Host))
    .WithEnvironment("Correo__Puerto", smtp.Property(EndpointProperty.Port))
    .WaitFor(mailpit)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
