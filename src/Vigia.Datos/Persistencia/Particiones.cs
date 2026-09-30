using System.Globalization;

using Microsoft.EntityFrameworkCore;

namespace Vigia.Datos.Persistencia;

/// <summary>
/// La tabla <c>resultados</c> está particionada por mes (<c>PARTITION BY RANGE (momento)</c>): una
/// tabla física por mes, todas visibles como una sola. Se hace por dos razones:
/// <list type="bullet">
/// <item><b>Borrar es instantáneo.</b> La retención de 14 días se cumple eliminando la partición
/// entera de un mes viejo (<c>DROP TABLE</c>, que no recorre las filas) en lugar de un <c>DELETE</c>
/// de millones de filas que deja la tabla llena de huecos y obliga a un <c>VACUUM</c> largo.</item>
/// <item><b>Las consultas por fecha leen poco.</b> Pedir «las últimas 24 horas» solo toca la partición
/// del mes actual (y quizá la anterior); las demás ni se miran.</item>
/// </list>
/// PostgreSQL no crea las particiones solo: hay que crearlas antes de que haga falta. Este servicio las
/// crea por adelantado, y el worker lo ejecuta al arrancar y cada día.
/// </summary>
public sealed class Particiones(VigiaDbContext db)
{
    private const string Padre = "resultados";

    /// <summary>Cuántos meses por delante se mantienen creados: un fallo del worker no debe dejar a la base de datos sin partición donde escribir.</summary>
    public const int MesesPorAdelantado = 3;

    /// <summary>Nombre de la tabla física de un mes: <c>resultados_2026_10</c>.</summary>
    public static string NombreDe(DateTimeOffset mes) =>
        string.Create(CultureInfo.InvariantCulture, $"{Padre}_{mes.UtcDateTime.Year:0000}_{mes.UtcDateTime.Month:00}");

    /// <summary>El primer instante del mes (en UTC) al que pertenece <paramref name="momento"/>.</summary>
    public static DateTimeOffset InicioDelMes(DateTimeOffset momento)
    {
        var utc = momento.UtcDateTime;

        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Crea las particiones del mes actual y de los siguientes, más el mes anterior. Es idempotente.</summary>
    public async Task AsegurarAsync(DateTimeOffset ahora, CancellationToken cancellationToken)
    {
        var mes = InicioDelMes(ahora).AddMonths(-1);

        for (var i = 0; i < MesesPorAdelantado + 2; i++)
        {
            await CrearAsync(mes.AddMonths(i), cancellationToken);
        }
    }

    /// <summary>Crea la partición de un mes si no existe.</summary>
    public async Task CrearAsync(DateTimeOffset mes, CancellationToken cancellationToken)
    {
        var inicio = InicioDelMes(mes);
        var fin = inicio.AddMonths(1);

        // Los nombres y las fechas los genera este código (nunca vienen de fuera), pero un identificador
        // no se puede pasar como parámetro y por eso se construye la sentencia con texto.
        var sentencia = string.Create(
            CultureInfo.InvariantCulture,
            $"CREATE TABLE IF NOT EXISTS {NombreDe(inicio)} PARTITION OF {Padre} FOR VALUES FROM ('{inicio:yyyy-MM-dd} 00:00:00+00') TO ('{fin:yyyy-MM-dd} 00:00:00+00')");

#pragma warning disable EF1002 // Sin datos de usuario: solo un nombre y dos fechas calculadas aquí.
        await db.Database.ExecuteSqlRawAsync(sentencia, cancellationToken);
#pragma warning restore EF1002
    }

    /// <summary>Los meses (inicio de cada uno) que tienen partición ahora mismo, en orden.</summary>
    public async Task<IReadOnlyList<DateTimeOffset>> ListarAsync(CancellationToken cancellationToken)
    {
        var nombres = await db.Database
            .SqlQuery<string>($"""
                SELECT c.relname AS "Value"
                FROM pg_inherits i
                JOIN pg_class c ON c.oid = i.inhrelid
                JOIN pg_class p ON p.oid = i.inhparent
                WHERE p.relname = 'resultados' AND c.relname ~ '^resultados_[0-9][0-9][0-9][0-9]_[0-9][0-9]$'
                ORDER BY c.relname
                """)
            .ToListAsync(cancellationToken);

        return
        [
            .. nombres.Select(nombre =>
            {
                var partes = nombre.Split('_');
                return new DateTimeOffset(int.Parse(partes[1], CultureInfo.InvariantCulture), int.Parse(partes[2], CultureInfo.InvariantCulture), 1, 0, 0, 0, TimeSpan.Zero);
            }),
        ];
    }

    /// <summary>
    /// Aplica la retención: elimina las particiones de meses que terminaron antes de <paramref name="limite"/>
    /// (instantáneo) y borra, en la partición que queda partida por el límite, las filas anteriores a él.
    /// Devuelve las particiones eliminadas.
    /// </summary>
    public async Task<IReadOnlyList<string>> AplicarRetencionAsync(DateTimeOffset limite, CancellationToken cancellationToken)
    {
        var eliminadas = new List<string>();

        foreach (var mes in await ListarAsync(cancellationToken))
        {
            if (mes.AddMonths(1) <= limite)
            {
                var nombre = NombreDe(mes);
#pragma warning disable EF1002 // El nombre lo genera este código a partir de una fecha.
                await db.Database.ExecuteSqlRawAsync($"DROP TABLE IF EXISTS {nombre}", cancellationToken);
#pragma warning restore EF1002
                eliminadas.Add(nombre);
            }
        }

        // La partición del mes del límite conserva días más viejos que él: se borran las filas, que son pocas.
        var corte = limite.ToUniversalTime();
        await db.Database.ExecuteSqlAsync($"DELETE FROM resultados WHERE momento < {corte}", cancellationToken);

        return eliminadas;
    }
}
