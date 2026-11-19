#!/usr/bin/env bash
# Installs a new version of the online copy. Code moves only by this script, never through the data sync.
# Run as root on the Contabo server with the release built at the hospital/dev machine:
#   dotnet publish backend/src/AlShifa.Api -c Release -r linux-x64 --self-contained -o out  (with the built frontend in out/wwwroot)
#   tar -czf alshifa-<commit>.tar.gz -C out .   then copy it to the server.
#   deploy/linux/deploy-release.sh /tmp/alshifa-<commit>.tar.gz <sha256>
#
# Order: check the archive, unpack beside the running version, back up the online database, migrate, switch, check.
# Any failure puts the previous version and database back. Other sites on this server are not touched.
set -euo pipefail
umask 077
exec 9>/run/lock/alshifa-deploy.lock
flock -n 9 || { echo 'Another AlShifa deployment is running.' >&2; exit 1; }

archive=${1:?archive path}
expected=${2:?sha256 of the archive}
stamp=$(date -u +%Y%m%dT%H%M%SZ)
release=/opt/alshifa/releases/$stamp
backup_dir=/var/backups/alshifa/deployments/$stamp
db=alshifa_mirror
container=alshifa-mysql
verify_db=alshifa_verify_${stamp//[^0-9]/}
verify_created=0

test "$(sha256sum "$archive" | awk '{print $1}')" = "$expected"
# shellcheck disable=SC1091
. /etc/alshifa/mirror-owner.env
test -n "${ALSHIFA_MIRROR_OWNER:-}"

previous=$(readlink -f /opt/alshifa/current)
mkdir -p "$release"
# Database dumps hold patient data: root only.
install -d -m 700 "$backup_dir"
tar -xzf "$archive" -C "$release"
# Built on Windows: the archive carries no Unix modes. Nothing may be writable by others, and the program must run.
chmod -R u=rwX,go=rX "$release"
chmod 755 "$release/AlShifa.Api"
test -x "$release/AlShifa.Api"
test -f "$release/wwwroot/index.html"
chown -R root:root "$release"

root_mysql() {
  docker exec -i "$container" sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot'
}

# The online copy can always be rebuilt from the hospital, but a backup makes going back immediate.
docker exec "$container" sh -c "MYSQL_PWD=\"\$MYSQL_ROOT_PASSWORD\" mysqldump -uroot --single-transaction --quick --no-tablespaces --set-gtid-purged=OFF $db" | gzip > "$backup_dir/$db.sql.gz"
gzip -t "$backup_dir/$db.sql.gz"

switched=0
service_stopped=0
cleanup_verify() {
  if [ "$verify_created" = 1 ]; then
    printf 'DROP DATABASE IF EXISTS `%s`; REVOKE ALL PRIVILEGES ON `%s`.* FROM '\''alshifa_mirror_owner'\''@'\''%%'\'';\n' "$verify_db" "$verify_db" | root_mysql
    verify_created=0
  fi
}
rollback() {
  local rc=$?
  set +e
  cleanup_verify
  [ "$switched" = 1 ] || { [ "$service_stopped" = 0 ] || systemctl start alshifa-api; echo "Deploy stopped before the switch; the running version is unchanged."; return "$rc"; }
  echo "DEPLOY FAILED ($rc): restoring $previous and the database backup"
  systemctl stop alshifa-api
  printf 'DROP DATABASE `%s`; CREATE DATABASE `%s` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;\n' "$db" "$db" | root_mysql
  gunzip -c "$backup_dir/$db.sql.gz" | docker exec -i "$container" sh -c "MYSQL_PWD=\"\$MYSQL_ROOT_PASSWORD\" mysql -uroot $db"
  ln -sfn "$previous" /opt/alshifa/current.next && mv -Tf /opt/alshifa/current.next /opt/alshifa/current
  systemctl start alshifa-api
  return "$rc"
}
trap rollback EXIT

# Restore the actual backup to a separate database and rehearse migrations before touching production.
# The temporary grant is limited to this clone and is removed on success or failure.
verify_connection=${ALSHIFA_MIRROR_OWNER/Database=$db;/Database=$verify_db;}
test "$verify_connection" != "$ALSHIFA_MIRROR_OWNER"
verify_created=1
printf 'CREATE DATABASE `%s` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci; GRANT ALL PRIVILEGES ON `%s`.* TO '\''alshifa_mirror_owner'\''@'\''%%'\'';\n' "$verify_db" "$verify_db" | root_mysql
gunzip -c "$backup_dir/$db.sql.gz" | docker exec -i "$container" sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot "$1"' sh "$verify_db"
core_counts() {
  local database=$1
  printf 'SELECT '\''Patients'\'',COUNT(*) FROM `%s`.Patients UNION ALL SELECT '\''AspNetUsers'\'',COUNT(*) FROM `%s`.AspNetUsers UNION ALL SELECT '\''Products'\'',COUNT(*) FROM `%s`.Products UNION ALL SELECT '\''Invoices'\'',COUNT(*) FROM `%s`.Invoices UNION ALL SELECT '\''Payments'\'',COUNT(*) FROM `%s`.Payments UNION ALL SELECT '\''StockBatches'\'',COUNT(*) FROM `%s`.StockBatches;\n' "$database" "$database" "$database" "$database" "$database" "$database" | root_mysql
}
core_counts "$verify_db" > "$backup_dir/clone-before.txt"
(
  cd "$release"
  sudo -u alshifa env ASPNETCORE_ENVIRONMENT=Production Mirror__Enabled=true \
    ConnectionStrings__Default="$verify_connection" \
    Storage__Path=/var/lib/alshifa/storage DataProtection__KeysPath=/var/lib/alshifa/keys \
    ./AlShifa.Api --migrate --backup-taken
) > "$backup_dir/clone-migration.log" 2>&1
core_counts "$verify_db" > "$backup_dir/clone-after.txt"
cmp "$backup_dir/clone-before.txt" "$backup_dir/clone-after.txt"
cleanup_verify
echo 'Backup restore and migration rehearsal passed; patient, user, product, invoice, payment and stock counts preserved.'

# Retain configuration and keys locally on the server, with the protected database backup.
tar -czf "$backup_dir/server-config.tar.gz" /etc/alshifa /etc/systemd/system/alshifa-api.service /etc/systemd/system/alshifa-api.service.d /var/lib/alshifa/keys 2> "$backup_dir/config-backup.log"
gzip -t "$backup_dir/server-config.tar.gz"
systemctl stop alshifa-api
service_stopped=1
# No writes may be lost if a rollback is needed after the preflight. Refresh the backup after stopping the service.
docker exec "$container" sh -c "MYSQL_PWD=\"\$MYSQL_ROOT_PASSWORD\" mysqldump -uroot --single-transaction --quick --no-tablespaces --set-gtid-purged=OFF $db" | gzip > "$backup_dir/$db.final.sql.gz"
gzip -t "$backup_dir/$db.final.sql.gz"
mv "$backup_dir/$db.final.sql.gz" "$backup_dir/$db.sql.gz"
switched=1
core_counts "$db" > "$backup_dir/production-before.txt"
(
  cd "$release"
  sudo -u alshifa env ASPNETCORE_ENVIRONMENT=Production Mirror__Enabled=true \
    ConnectionStrings__Default="$ALSHIFA_MIRROR_OWNER" \
    Storage__Path=/var/lib/alshifa/storage DataProtection__KeysPath=/var/lib/alshifa/keys \
    ./AlShifa.Api --migrate --backup-taken
) > "$backup_dir/production-migration.log" 2>&1
core_counts "$db" > "$backup_dir/production-after.txt"
cmp "$backup_dir/production-before.txt" "$backup_dir/production-after.txt"
# A new migration may recreate AspNetUsers; the read-only account's sign-in columns must stay writable.
printf "GRANT UPDATE (AccessFailedCount, LockoutEnd, ConcurrencyStamp) ON \`%s\`.\`AspNetUsers\` TO 'alshifa_mirror_ro'@'%%';\n" "$db" | root_mysql

ln -sfn "$release" /opt/alshifa/current.next
mv -Tf /opt/alshifa/current.next /opt/alshifa/current
systemctl start alshifa-api

ok=0
for _ in $(seq 1 45); do
  if curl -fsS http://127.0.0.1:5080/health | grep -q Healthy; then ok=1; break; fi
  sleep 1
done
test "$ok" = 1
curl -fsS --resolve shafa.novatechsoft.com:443:127.0.0.1 https://shafa.novatechsoft.com/ | grep -qi '<!doctype html>'
curl -fsS --resolve shafa.novatechsoft.com:443:127.0.0.1 https://shafa.novatechsoft.com/manifest.webmanifest | grep -q '"start_url": "/login"'
test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/api/sync/mirror/state)" = 401
test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/api/sync/commands/)" = 401

trap - EXIT
echo "Deployed $release (previous: $previous, database backup: $backup_dir)."
echo "The hospital server re-sends everything once after a version change; watch Settings > Backup there."
