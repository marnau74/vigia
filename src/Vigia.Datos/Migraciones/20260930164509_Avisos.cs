using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigia.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class Avisos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "avisos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    incidente_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo = table.Column<short>(type: "smallint", nullable: false),
                    canal = table.Column<short>(type: "smallint", nullable: false),
                    destino = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    creado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    intentos = table.Column<int>(type: "integer", nullable: false),
                    proximo_intento_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    enviado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    abandonado = table.Column<bool>(type: "boolean", nullable: false),
                    ultimo_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_avisos", x => x.id);
                    table.ForeignKey(
                        name: "fk_avisos_incidentes_incidente_id",
                        column: x => x.incidente_id,
                        principalTable: "incidentes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_avisos_monitores_monitor_id",
                        column: x => x.monitor_id,
                        principalTable: "monitores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_avisos_monitor_id",
                table: "avisos",
                column: "monitor_id");

            migrationBuilder.CreateIndex(
                name: "ix_avisos_pendientes",
                table: "avisos",
                column: "proximo_intento_en",
                filter: "enviado_en IS NULL AND abandonado = false");

            migrationBuilder.CreateIndex(
                name: "ux_avisos_sin_duplicados",
                table: "avisos",
                columns: new[] { "incidente_id", "tipo", "canal", "destino" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "avisos");
        }
    }
}
