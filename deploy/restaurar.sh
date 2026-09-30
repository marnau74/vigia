#!/usr/bin/env bash
# Restaura una copia de seguridad SOBRE la base de datos actual (la sustituye entera).
#   ./restaurar.sh /var/backups/vigia/vigia-20261015T030000Z.dump
# Para el worker, la API y la web mientras dura, y al terminar las arranca (el worker aplica las migraciones que falten).
set -euo pipefail

copia="${1:?Uso: $0 <fichero.dump>}"
[ -r "$copia" ] || { echo "No se puede leer $copia" >&2; exit 1; }

cd "$(dirname "$0")"

if [ "${CONFIRMAR:-}" != "si" ]; then
    echo "Esto BORRA la base de datos actual y la sustituye por $copia."
    read -r -p "Escribe «si» para continuar: " respuesta
    [ "$respuesta" = "si" ] || { echo "Cancelado."; exit 1; }
fi

docker compose stop web api worker
docker compose exec -T postgres psql -U vigia -d postgres -v ON_ERROR_STOP=1 \
    -c "DROP DATABASE IF EXISTS vigia WITH (FORCE)" -c "CREATE DATABASE vigia OWNER vigia"
docker compose exec -T postgres pg_restore -U vigia -d vigia --no-owner --exit-on-error < "$copia"
docker compose up -d
echo "Restauración terminada."
