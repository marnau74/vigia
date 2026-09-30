using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigia.Datos.Migraciones
{
    /// <summary>
    /// Crea la tabla <c>resultados</c> particionada por mes. EF Core no sabe expresar el particionado
    /// declarativo de PostgreSQL, así que esta migración se escribe a mano (el modelo excluye la tabla
    /// de las migraciones automáticas). Las particiones concretas de cada mes las crea el worker por
    /// adelantado (ver <c>Particiones</c>); aquí solo se crea la del mes en curso y la siguiente para que
    /// una base de datos recién migrada ya pueda recibir resultados.
    /// </summary>
    public partial class ResultadosParticionados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE resultados (
                    id uuid NOT NULL,
                    monitor_id uuid NOT NULL,
                    momento timestamptz NOT NULL,
                    correcto boolean NOT NULL,
                    latencia_ms integer NOT NULL,
                    fallo smallint NOT NULL,
                    error character varying(500),
                    detalles jsonb,
                    en_mantenimiento boolean NOT NULL,
                    -- La clave de una tabla particionada debe incluir la columna de partición.
                    CONSTRAINT pk_resultados PRIMARY KEY (id, momento),
                    CONSTRAINT fk_resultados_monitores_monitor_id FOREIGN KEY (monitor_id) REFERENCES monitores (id) ON DELETE CASCADE
                ) PARTITION BY RANGE (momento);
                """);

            // «Los últimos resultados de este monitor»: la consulta del panel y de los agregados.
            migrationBuilder.Sql("CREATE INDEX ix_resultados_monitor_id_momento ON resultados (monitor_id, momento DESC);");

            // BRIN: un índice diminuto (unos KB por partición) que sirve porque los resultados se
            // insertan en orden de tiempo: los bloques de una tabla ya están ordenados por fecha. Un
            // btree sobre la fecha sería cientos de veces más grande para la misma consulta de rangos.
            migrationBuilder.Sql("CREATE INDEX ix_resultados_momento_brin ON resultados USING brin (momento) WITH (pages_per_range = 32);");

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    mes date := date_trunc('month', now() AT TIME ZONE 'UTC')::date;
                    i integer;
                BEGIN
                    FOR i IN 0..1 LOOP
                        EXECUTE format(
                            'CREATE TABLE IF NOT EXISTS resultados_%s PARTITION OF resultados FOR VALUES FROM (%L) TO (%L)',
                            to_char(mes + (i || ' month')::interval, 'YYYY_MM'),
                            to_char(mes + (i || ' month')::interval, 'YYYY-MM-DD') || ' 00:00:00+00',
                            to_char(mes + ((i + 1) || ' month')::interval, 'YYYY-MM-DD') || ' 00:00:00+00');
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Borrar la tabla padre borra también todas sus particiones.
            migrationBuilder.Sql("DROP TABLE IF EXISTS resultados;");
        }
    }
}
