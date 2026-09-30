using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Vigia.Dominio.Mantenimiento;
using Vigia.Dominio.Monitores;
using Vigia.Dominio.Seguimiento;

namespace Vigia.Datos.Persistencia.Configuraciones;

internal sealed class GrupoConfiguracion : IEntityTypeConfiguration<Grupo>
{
    public void Configure(EntityTypeBuilder<Grupo> builder)
    {
        builder.ToTable("grupos");
        builder.HasKey(g => g.Id);

        // El dominio es inmutable (propiedades solo con get): EF no las mapea por convención.
        builder.Property(g => g.Slug).HasMaxLength(40).IsRequired();
        builder.Property(g => g.Nombre).HasMaxLength(100).IsRequired();
        builder.Property(g => g.Publico).IsRequired();

        builder.HasIndex(g => g.Slug).IsUnique();
    }
}

internal sealed class MonitorConfiguracion : IEntityTypeConfiguration<Dominio.Monitores.Monitor>
{
    public void Configure(EntityTypeBuilder<Dominio.Monitores.Monitor> builder)
    {
        builder.ToTable("monitores");
        builder.HasKey(m => m.Id);
        builder.Ignore(m => m.Tipo);

        builder.Property(m => m.CreadoEn).IsRequired();
        builder.Property(m => m.Nombre).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Intervalo).IsRequired();
        builder.Property(m => m.FallosParaIncidente).IsRequired();
        builder.Property(m => m.UmbralLento);
        builder.Property(m => m.Activo).IsRequired();

        // La configuración es un documento JSON con su tipo (ver ConversorConfiguracion). Para saber si
        // ha cambiado se compara su JSON: dos configuraciones con los mismos datos son la misma.
        builder.Property(m => m.Configuracion)
            .HasConversion(
                new ConversorConfiguracion(),
                new ValueComparer<ConfiguracionMonitor>(
                    (a, b) => ConversorConfiguracion.Serializar(a!) == ConversorConfiguracion.Serializar(b!),
                    c => ConversorConfiguracion.Serializar(c).GetHashCode(StringComparison.Ordinal),
                    c => ConversorConfiguracion.Deserializar(ConversorConfiguracion.Serializar(c))))
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasOne<Grupo>().WithMany().HasForeignKey(m => m.GrupoId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(m => m.GrupoId);
        builder.HasIndex(m => m.Activo);
    }
}

internal sealed class SeguimientoConfiguracion : IEntityTypeConfiguration<SeguimientoEntidad>
{
    public void Configure(EntityTypeBuilder<SeguimientoEntidad> builder)
    {
        builder.ToTable("seguimientos");
        builder.HasKey(s => s.MonitorId);

        builder.Property(s => s.Estado).HasConversion<short>().IsRequired();

        builder.HasOne<Dominio.Monitores.Monitor>().WithOne().HasForeignKey<SeguimientoEntidad>(s => s.MonitorId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Incidente>().WithMany().HasForeignKey(s => s.IncidenteAbiertoId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class IncidenteConfiguracion : IEntityTypeConfiguration<Incidente>
{
    public void Configure(EntityTypeBuilder<Incidente> builder)
    {
        builder.ToTable("incidentes");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.MonitorId).IsRequired();
        builder.Property(i => i.AbiertoEn).IsRequired();
        builder.Property(i => i.Causa).HasMaxLength(Incidente.LongitudMaximaCausa).IsRequired();
        builder.Property(i => i.Fallos).IsRequired();
        builder.Property(i => i.CerradoPor).HasConversion<short?>();

        builder.HasOne<Dominio.Monitores.Monitor>().WithMany().HasForeignKey(i => i.MonitorId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(i => new { i.MonitorId, i.AbiertoEn });

        // Un monitor no puede tener dos incidentes abiertos a la vez. La máquina de estados ya lo
        // garantiza, y esto lo garantiza además la base de datos aunque dos procesos se pisen.
        builder.HasIndex(i => i.MonitorId).IsUnique().HasFilter("cerrado_en IS NULL").HasDatabaseName("ux_incidentes_un_abierto_por_monitor");
    }
}

internal sealed class CambioDeEstadoConfiguracion : IEntityTypeConfiguration<CambioDeEstadoEntidad>
{
    public void Configure(EntityTypeBuilder<CambioDeEstadoEntidad> builder)
    {
        builder.ToTable("cambios_estado");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).UseIdentityAlwaysColumn();

        builder.Property(c => c.Anterior).HasConversion<short>().IsRequired();
        builder.Property(c => c.Nuevo).HasConversion<short>().IsRequired();

        builder.HasOne<Dominio.Monitores.Monitor>().WithMany().HasForeignKey(c => c.MonitorId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.MonitorId, c.Momento });
    }
}

internal sealed class ResultadoConfiguracion : IEntityTypeConfiguration<ResultadoEntidad>
{
    public void Configure(EntityTypeBuilder<ResultadoEntidad> builder)
    {
        // La tabla está particionada por mes y la crea la migración «ResultadosParticionados» con SQL
        // propio (EF Core no sabe expresar el particionado): aquí solo se describe para leer y escribir.
        builder.ToTable("resultados", tabla => tabla.ExcludeFromMigrations());

        // La clave de una tabla particionada debe incluir la columna de partición.
        builder.HasKey(r => new { r.Id, r.Momento });

        builder.Property(r => r.Error).HasMaxLength(Incidente.LongitudMaximaCausa);
        builder.Property(r => r.Detalles).HasColumnType("jsonb");
    }
}

internal sealed class AgregadoHoraConfiguracion : IEntityTypeConfiguration<AgregadoHora>
{
    public void Configure(EntityTypeBuilder<AgregadoHora> builder)
    {
        builder.ToTable("resultados_hora");
        builder.HasKey(a => new { a.MonitorId, a.Periodo });
        builder.Ignore(a => a.Tiempos);

        builder.HasOne<Dominio.Monitores.Monitor>().WithMany().HasForeignKey(a => a.MonitorId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => a.Periodo);
    }
}

internal sealed class AgregadoDiaConfiguracion : IEntityTypeConfiguration<AgregadoDia>
{
    public void Configure(EntityTypeBuilder<AgregadoDia> builder)
    {
        builder.ToTable("resultados_dia");
        builder.HasKey(a => new { a.MonitorId, a.Periodo });
        builder.Ignore(a => a.Tiempos);

        builder.HasOne<Dominio.Monitores.Monitor>().WithMany().HasForeignKey(a => a.MonitorId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class VentanaMantenimientoConfiguracion : IEntityTypeConfiguration<VentanaMantenimiento>
{
    public void Configure(EntityTypeBuilder<VentanaMantenimiento> builder)
    {
        builder.ToTable("ventanas_mantenimiento");
        builder.HasKey(v => v.Id);
        builder.Ignore(v => v.MonitorIds);

        builder.Property(v => v.Inicio).IsRequired();
        builder.Property(v => v.Fin).IsRequired();
        builder.Property(v => v.Motivo).HasMaxLength(200).IsRequired();

        // Los monitores afectados son una lista de identificadores (uuid[]) que EF lee y escribe a
        // través del campo privado del dominio.
        builder.Property<Guid[]>("_monitorIds").HasColumnName("monitor_ids").IsRequired();

        builder.HasIndex(v => v.Fin);
    }
}
