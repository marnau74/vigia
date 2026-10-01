#!/usr/bin/env bash
# Copia de seguridad de la base de datos: un volcado comprimido y verificado, con rotación.
#   DIR_COPIAS       dónde se guardan (por defecto /var/backups/vigia)
#   RETENCION_DIAS   cuántos días se conservan (por defecto 14)
#   DESTINO_RSYNC    opcional: copia fuera del servidor, p. ej. usuario@otra-maquina:/copias/vigia/
# Se ejecuta a diario con el temporizador que instala el playbook de Ansible (ver docs/despliegue.md).
set -euo pipefail

DIR="${DIR_COPIAS:-/var/backups/vigia}"
RETENCION_DIAS="${RETENCION_DIAS:-14}"

cd "$(dirname "$0")"
mkdir -p "$DIR"
fichero="$DIR/vigia-$(date -u +%Y%m%dT%H%M%SZ).dump"

# Se vuelca a un fichero temporal y solo se «publica» si es un volcado legible: una copia a medias no debe parecer buena.
docker compose exec -T postgres pg_dump -U vigia -d vigia -Fc > "$fichero.tmp"
docker compose exec -T postgres pg_restore --list < "$fichero.tmp" > /dev/null
mv "$fichero.tmp" "$fichero"
chmod 600 "$fichero"

find "$DIR" -name 'vigia-*.dump' -mtime +"$RETENCION_DIAS" -delete
find "$DIR" -name 'vigia-*.dump.tmp' -mmin +60 -delete

if [ -n "${DESTINO_RSYNC:-}" ]; then
    # Solo se añaden copias, nunca se borran fuera: si aquí se perdieran las locales (disco, error, intrusión),
    # una réplica con --delete borraría también las de fuera, que son justo las que deben sobrevivir.
    # La retención del destino se gestiona allí (ver docs/despliegue.md).
    rsync -a --include='vigia-*.dump' --exclude='*' "$DIR/" "$DESTINO_RSYNC"
fi

echo "Copia hecha: $fichero ($(du -h "$fichero" | cut -f1))"
