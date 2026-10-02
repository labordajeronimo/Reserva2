#!/usr/bin/env bash
# Backup diario de Reserva2: base de datos (SQL Server en el contenedor Docker "sqlserver")
# más los archivos subidos (/var/lib/reserva2/uploads: logos, fotos y comprobantes de seña).
#
# Deja todo en /var/backups/reserva2 y borra lo que tenga más de 14 días. Si RCLONE_DESTINO
# está definido (ej. "gdrive:reserva2-backups"), además copia el backup del día fuera del
# droplet con rclone.
#
# La conexión a la base NO se repite acá: se lee de la misma variable de entorno que usa la
# API (ConnectionStrings__DefaultConnection del servicio systemd reserva2-api), así que si
# cambia la contraseña del sa, el backup sigue funcionando sin tocar este archivo.
#
# Restaurar (ver el PR / README para el paso a paso):
#   gunzip -k reserva2-db-FECHA.bak.gz
#   docker cp reserva2-db-FECHA.bak sqlserver:/var/opt/mssql/backup/
#   sqlcmd ... -Q "RESTORE DATABASE [Reserva2Db] FROM DISK = N'/var/opt/mssql/backup/reserva2-db-FECHA.bak' WITH REPLACE"
#   tar -xzf reserva2-uploads-FECHA.tar.gz -C /var/lib/reserva2

set -euo pipefail

DESTINO=/var/backups/reserva2
DIAS_RETENCION=14
CONTENEDOR=sqlserver
SERVICIO_API=reserva2-api
UPLOADS=/var/lib/reserva2/uploads
SQLCMD=/opt/mssql-tools18/bin/sqlcmd
RCLONE_DESTINO="${RCLONE_DESTINO:-}"

FECHA=$(date +%Y%m%d-%H%M%S)
log() { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"; }

umask 077
mkdir -p "$DESTINO"
chmod 700 "$DESTINO"

# --- Datos de conexión, sacados del servicio de la API ---
CONEXION=$(systemctl show "$SERVICIO_API" -p Environment --value | tr ' ' '\n' \
  | sed -n 's/^ConnectionStrings__DefaultConnection=//p')
valor() { echo "$CONEXION" | tr ';' '\n' | sed -n "s/^[[:space:]]*\($1\)=//Ip" | head -1; }
BASE=$(valor 'Database\|Initial Catalog')
USUARIO=$(valor 'Uid\|User Id\|User')
PASSWORD=$(valor 'Password\|Pwd')

if [[ -z "$BASE" || -z "$USUARIO" || -z "$PASSWORD" ]]; then
  log "ERROR: no pude leer base/usuario/contraseña de ConnectionStrings__DefaultConnection en $SERVICIO_API."
  exit 1
fi

# --- 1. Base de datos ---
ARCHIVO_BAK="reserva2-db-$FECHA.bak"
log "Backup de la base $BASE..."
# La contraseña entra por stdin (no queda visible en la lista de procesos) y sqlcmd la toma
# de SQLCMDPASSWORD. -C: el certificado del SQL Server del contenedor es autofirmado.
docker exec -i "$CONTENEDOR" bash -c '
  set -e
  IFS= read -r SQLCMDPASSWORD; export SQLCMDPASSWORD
  mkdir -p /var/opt/mssql/backup
  '"$SQLCMD"' -S localhost -U "$0" -C -b \
    -Q "BACKUP DATABASE [$1] TO DISK = N'"'"'/var/opt/mssql/backup/$2'"'"' WITH INIT, CHECKSUM"
' "$USUARIO" "$BASE" "$ARCHIVO_BAK" <<< "$PASSWORD"

docker cp "$CONTENEDOR:/var/opt/mssql/backup/$ARCHIVO_BAK" "$DESTINO/$ARCHIVO_BAK"
docker exec "$CONTENEDOR" rm -f "/var/opt/mssql/backup/$ARCHIVO_BAK"
gzip -f "$DESTINO/$ARCHIVO_BAK"
log "OK: $DESTINO/$ARCHIVO_BAK.gz ($(du -h "$DESTINO/$ARCHIVO_BAK.gz" | cut -f1))"

# --- 2. Archivos subidos ---
ARCHIVO_UPLOADS="reserva2-uploads-$FECHA.tar.gz"
log "Backup de $UPLOADS..."
tar -czf "$DESTINO/$ARCHIVO_UPLOADS" -C "$(dirname "$UPLOADS")" "$(basename "$UPLOADS")"
log "OK: $DESTINO/$ARCHIVO_UPLOADS ($(du -h "$DESTINO/$ARCHIVO_UPLOADS" | cut -f1))"

# --- 3. Rotación local ---
find "$DESTINO" -maxdepth 1 -type f -name 'reserva2-*' -mtime +$((DIAS_RETENCION - 1)) -print -delete \
  | sed 's/^/Borrado por antigüedad: /'

# --- 4. Copia fuera del droplet (opcional) ---
if [[ -n "$RCLONE_DESTINO" ]]; then
  log "Copiando a $RCLONE_DESTINO..."
  rclone copy "$DESTINO/$ARCHIVO_BAK.gz" "$RCLONE_DESTINO"
  rclone copy "$DESTINO/$ARCHIVO_UPLOADS" "$RCLONE_DESTINO"
  # Afuera se guardan un poco más que en el droplet.
  rclone delete "$RCLONE_DESTINO" --min-age 30d
  log "OK: copia remota."
fi

log "Backup terminado."
