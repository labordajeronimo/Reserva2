#!/usr/bin/env bash
# Aplica un script SQL de migración (generado con `dotnet ef migrations script ... --idempotent`)
# a la base de producción. Se corre EN EL SERVIDOR, como root:
#   bash aplicar-migracion.sh /root/20261002-mercadopago.sql
# Es idempotente: si la migración ya estaba aplicada, no hace nada. Toma los datos de conexión
# del servicio reserva2-api, igual que backup.sh.
set -euo pipefail

ARCHIVO="${1:-}"
CONTENEDOR=sqlserver
SERVICIO_API=reserva2-api
SQLCMD=/opt/mssql-tools18/bin/sqlcmd

if [[ -z "$ARCHIVO" || ! -f "$ARCHIVO" ]]; then
  echo "Uso: bash aplicar-migracion.sh archivo.sql"
  exit 1
fi

PID_API=$(systemctl show "$SERVICIO_API" -p MainPID --value)
if [[ "$PID_API" =~ ^[1-9][0-9]*$ && -r "/proc/$PID_API/environ" ]]; then
  CONEXION=$(tr '\0' '\n' < "/proc/$PID_API/environ" | sed -n 's/^ConnectionStrings__DefaultConnection=//p')
else
  CONEXION=$(systemctl show "$SERVICIO_API" -p Environment --value | tr ' ' '\n' \
    | sed -n 's/^"\{0,1\}ConnectionStrings__DefaultConnection=\(.*\)$/\1/p' | sed 's/"$//')
fi
valor() { echo "$CONEXION" | tr ';' '\n' | sed -n "s/^[[:space:]]*\($1\)=//Ip" | head -1; }
BASE=$(valor 'Database\|Initial Catalog')
USUARIO=$(valor 'Uid\|User Id\|User')
PASSWORD=$(valor 'Password\|Pwd')

if [[ -z "$BASE" || -z "$USUARIO" || -z "$PASSWORD" ]]; then
  echo "ERROR: no pude leer base/usuario/contraseña de ConnectionStrings__DefaultConnection en $SERVICIO_API."
  exit 1
fi

DESTINO_TMP="/tmp/migracion-$(date +%s).sql"
docker cp "$ARCHIVO" "$CONTENEDOR:$DESTINO_TMP"
# La contraseña entra por stdin (no queda visible en la lista de procesos). -b: corta ante
# el primer error; el script trae su propia transacción, así que un error no deja la base a medias.
docker exec -i "$CONTENEDOR" bash -c '
  IFS= read -r SQLCMDPASSWORD; export SQLCMDPASSWORD
  '"$SQLCMD"' -S localhost -U "$0" -d "$1" -C -b -i "$2"
' "$USUARIO" "$BASE" "$DESTINO_TMP" <<< "$PASSWORD"
# docker cp deja el archivo como root y el usuario del contenedor (mssql) no lo puede borrar.
docker exec -u 0 "$CONTENEDOR" rm -f "$DESTINO_TMP" || true
echo "OK: migración aplicada a $BASE."
