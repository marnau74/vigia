#!/usr/bin/env bash
# Comprueba que la última copia SE PUEDE restaurar, sin tocar la base de datos de verdad: la carga en una base temporal,
# cuenta lo que hay y la borra. Una copia que nunca se ha restaurado es una esperanza, no una copia.
#   ./probar-restauracion.sh [fichero.dump]     (por defecto, la más reciente de /var/backups/vigia)
set -euo pipefail

DIR="${DIR_COPIAS:-/var/backups/vigia}"
copia="${1:-$(find "$DIR" -maxdepth 1 -name 'vigia-*.dump' -printf '%T@ %p\n' 2>/dev/null | sort -rn | head -n 1 | cut -d' ' -f2-)}"
if [ -z "$copia" ] || [ ! -r "$copia" ]; then
  echo "No hay ninguna copia que probar en $DIR" >&2
  exit 1
fi

cd "$(dirname "$0")"
prueba="vigia_prueba_restauracion"

psql_() { docker compose exec -T postgres psql -U vigia -d postgres -v ON_ERROR_STOP=1 -At "$@"; }

psql_ -c "DROP DATABASE IF EXISTS $prueba WITH (FORCE)" -c "CREATE DATABASE $prueba"
trap 'psql_ -c "DROP DATABASE IF EXISTS '"$prueba"' WITH (FORCE)" >/dev/null' EXIT

docker compose exec -T postgres pg_restore -U vigia -d "$prueba" --no-owner --exit-on-error < "$copia"

monitores=$(docker compose exec -T postgres psql -U vigia -d "$prueba" -At -c "SELECT count(*) FROM monitores")
migraciones=$(docker compose exec -T postgres psql -U vigia -d "$prueba" -At -c 'SELECT count(*) FROM "__EFMigrationsHistory"')
[ "$migraciones" -gt 0 ] || { echo "La copia no trae el historial de migraciones: no es una copia válida." >&2; exit 1; }

echo "Restauración de prueba correcta: $monitores monitores y $migraciones migraciones en $(basename "$copia")."
