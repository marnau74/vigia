using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Vigia.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "grupos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    nombre = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    publico = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grupos", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ventanas_mantenimiento",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    inicio = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fin = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    motivo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    monitor_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ventanas_mantenimiento", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "monitores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    nombre = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    configuracion = table.Column<string>(type: "jsonb", nullable: false),
                    intervalo = table.Column<TimeSpan>(type: "interval", nullable: false),
                    fallos_para_incidente = table.Column<int>(type: "integer", nullable: false),
                    umbral_lento = table.Column<TimeSpan>(type: "interval", nullable: true),
                    grupo_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activo = table.Column<bool>(type: "boolean", nullable: false),
                    creado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitores", x => x.id);
                    table.ForeignKey(
                        name: "fk_monitores_grupos_grupo_id",
                        column: x => x.grupo_id,
                        principalTable: "grupos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "cambios_estado",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    monitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    momento = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    anterior = table.Column<short>(type: "smallint", nullable: false),
                    nuevo = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cambios_estado", x => x.id);
                    table.ForeignKey(
                        name: "fk_cambios_estado_monitores_monitor_id",
                        column: x => x.monitor_id,
                        principalTable: "monitores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "incidentes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    monitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    abierto_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cerrado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cerrado_por = table.Column<short>(type: "smallint", nullable: true),
                    causa = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    fallos = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incidentes", x => x.id);
                    table.ForeignKey(
                        name: "fk_incidentes_monitores_monitor_id",
                        column: x => x.monitor_id,
                        principalTable: "monitores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "resultados_dia",
                columns: table => new
                {
                    monitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    periodo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    peor_p95hora_ms = table.Column<double>(type: "double precision", nullable: true),
                    seg_desconocido = table.Column<double>(type: "double precision", nullable: false),
                    seg_operativo = table.Column<double>(type: "double precision", nullable: false),
                    seg_degradado = table.Column<double>(type: "double precision", nullable: false),
                    seg_sospechoso = table.Column<double>(type: "double precision", nullable: false),
                    seg_caido = table.Column<double>(type: "double precision", nullable: false),
                    seg_mantenimiento = table.Column<double>(type: "double precision", nullable: false),
                    comprobaciones = table.Column<int>(type: "integer", nullable: false),
                    correctas = table.Column<int>(type: "integer", nullable: false),
                    fallidas = table.Column<int>(type: "integer", nullable: false),
                    en_mantenimiento = table.Column<int>(type: "integer", nullable: false),
                    latencia_media_ms = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resultados_dia", x => new { x.monitor_id, x.periodo });
                    table.ForeignKey(
                        name: "fk_resultados_dia_monitores_monitor_id",
                        column: x => x.monitor_id,
                        principalTable: "monitores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "resultados_hora",
                columns: table => new
                {
                    monitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    periodo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    p50ms = table.Column<double>(type: "double precision", nullable: true),
                    p95ms = table.Column<double>(type: "double precision", nullable: true),
                    seg_desconocido = table.Column<double>(type: "double precision", nullable: false),
                    seg_operativo = table.Column<double>(type: "double precision", nullable: false),
                    seg_degradado = table.Column<double>(type: "double precision", nullable: false),
                    seg_sospechoso = table.Column<double>(type: "double precision", nullable: false),
                    seg_caido = table.Column<double>(type: "double precision", nullable: false),
                    seg_mantenimiento = table.Column<double>(type: "double precision", nullable: false),
                    comprobaciones = table.Column<int>(type: "integer", nullable: false),
                    correctas = table.Column<int>(type: "integer", nullable: false),
                    fallidas = table.Column<int>(type: "integer", nullable: false),
                    en_mantenimiento = table.Column<int>(type: "integer", nullable: false),
                    latencia_media_ms = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resultados_hora", x => new { x.monitor_id, x.periodo });
                    table.ForeignKey(
                        name: "fk_resultados_hora_monitores_monitor_id",
                        column: x => x.monitor_id,
                        principalTable: "monitores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "seguimientos",
                columns: table => new
                {
                    monitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    estado = table.Column<short>(type: "smallint", nullable: false),
                    fallos_seguidos = table.Column<int>(type: "integer", nullable: false),
                    desde = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ultima_observacion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    incidente_abierto_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seguimientos", x => x.monitor_id);
                    table.ForeignKey(
                        name: "fk_seguimientos_incidentes_incidente_abierto_id",
                        column: x => x.incidente_abierto_id,
                        principalTable: "incidentes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_seguimientos_monitores_monitor_id",
                        column: x => x.monitor_id,
                        principalTable: "monitores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cambios_estado_monitor_id_momento",
                table: "cambios_estado",
                columns: new[] { "monitor_id", "momento" });

            migrationBuilder.CreateIndex(
                name: "ix_grupos_slug",
                table: "grupos",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incidentes_monitor_id_abierto_en",
                table: "incidentes",
                columns: new[] { "monitor_id", "abierto_en" });

            migrationBuilder.CreateIndex(
                name: "ux_incidentes_un_abierto_por_monitor",
                table: "incidentes",
                column: "monitor_id",
                unique: true,
                filter: "cerrado_en IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_monitores_activo",
                table: "monitores",
                column: "activo");

            migrationBuilder.CreateIndex(
                name: "ix_monitores_grupo_id",
                table: "monitores",
                column: "grupo_id");

            migrationBuilder.CreateIndex(
                name: "ix_resultados_hora_periodo",
                table: "resultados_hora",
                column: "periodo");

            migrationBuilder.CreateIndex(
                name: "ix_seguimientos_incidente_abierto_id",
                table: "seguimientos",
                column: "incidente_abierto_id");

            migrationBuilder.CreateIndex(
                name: "ix_ventanas_mantenimiento_fin",
                table: "ventanas_mantenimiento",
                column: "fin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cambios_estado");

            migrationBuilder.DropTable(
                name: "resultados_dia");

            migrationBuilder.DropTable(
                name: "resultados_hora");

            migrationBuilder.DropTable(
                name: "seguimientos");

            migrationBuilder.DropTable(
                name: "ventanas_mantenimiento");

            migrationBuilder.DropTable(
                name: "incidentes");

            migrationBuilder.DropTable(
                name: "monitores");

            migrationBuilder.DropTable(
                name: "grupos");
        }
    }
}
