using Microsoft.EntityFrameworkCore;

using Vigia.Dominio.Mantenimiento;
using Vigia.Dominio.Monitores;
using Vigia.Dominio.Seguimiento;

namespace Vigia.Datos.Persistencia;

public sealed class VigiaDbContext(DbContextOptions<VigiaDbContext> opciones) : DbContext(opciones)
{
    public DbSet<Grupo> Grupos => Set<Grupo>();

    public DbSet<Dominio.Monitores.Monitor> Monitores => Set<Dominio.Monitores.Monitor>();

    public DbSet<SeguimientoEntidad> Seguimientos => Set<SeguimientoEntidad>();

    public DbSet<Incidente> Incidentes => Set<Incidente>();

    public DbSet<CambioDeEstadoEntidad> CambiosDeEstado => Set<CambioDeEstadoEntidad>();

    public DbSet<ResultadoEntidad> Resultados => Set<ResultadoEntidad>();

    public DbSet<AgregadoHora> ResultadosHora => Set<AgregadoHora>();

    public DbSet<AgregadoDia> ResultadosDia => Set<AgregadoDia>();

    public DbSet<VentanaMantenimiento> VentanasMantenimiento => Set<VentanaMantenimiento>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(VigiaDbContext).Assembly);
    }
}

/// <summary>Las opciones de EF Core comunes a todos los sitios que crean el contexto: los servicios, las migraciones y los tests.</summary>
public static class OpcionesVigia
{
    public static DbContextOptionsBuilder Configurar(DbContextOptionsBuilder opciones)
    {
        ArgumentNullException.ThrowIfNull(opciones);

        return opciones.UseSnakeCaseNamingConvention();
    }
}
