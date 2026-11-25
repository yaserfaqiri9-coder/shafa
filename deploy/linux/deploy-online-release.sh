#!/usr/bin/env bash
# Installs a new version of the online copy on the CURRENT production layout (Nov 2026):
#   - the service runs /opt/alshifa/api (and /opt/alshifa/current points to the same release);
#   - the effective database is the one named by the LAST EnvironmentFile of alshifa-api;
#   - the old AlShifa.DatabaseBackups hosting-startup assembly is replaced by the built-in handler.
# Usage (root, on the server):
#   deploy-online-release.sh /root/alshifa-<id>.tar.gz <sha256> --check-only   # rehearsal only, production untouched
#   deploy-online-release.sh /root/alshifa-<id>.tar.gz <sha256>                # rehearsal, then cutover
#   ALSHIFA_ACCEPTED_SOURCE_SCHEMAS=<hospital's last migration> deploy-online-release.sh ...   # online ahead of the hospital
# Order: verify archive, unpack beside the running version, back up the active database, rehearse the migration on a
# restored clone, stop, back up again, migrate, switch, check. Any failure after the stop restores the previous release
# and the database from the backup taken after the stop. No database is ever dropped; sync identity and keys are untouched.
set -euo pipefail
umask 077
exec 9>/run/lock/alshifa-deploy.lock
flock -n 9 || { echo 'Another AlShifa deployment is running.' >&2; exit 1; }

archive=${1:?archive path}
expected=${2:?sha256 of the archive}
mode=${3:-}
stamp=$(date -u +%Y%m%dT%H%M%SZ)
release=/opt/alshifa/releases/$stamp
backup_dir=/var/backups/alshifa/deployments/$stamp
container=alshifa-mysql
dropin=/etc/systemd/system/alshifa-api.service.d/zzzzz-release-$stamp.conf
verify_db=alshifa_verify_${stamp//[^0-9]/}

test "$(sha256sum "$archive" | awk '{print $1}')" = "$expected"

# The database the running service really uses: the last EnvironmentFile that sets the connection wins.
active_db=""
for file in $(systemctl show alshifa-api -p EnvironmentFiles --value | tr ' ' '\n' | grep '^/' ); do
  line=$(grep -E '^ConnectionStrings__Default=' "$file" 2>/dev/null | tail -1 || true)
  [ -n "$line" ] && active_db=$(printf '%s' "$line" | sed -E 's/.*Database=([^;"]+).*/\1/')
done
case "$active_db" in alshifa_mirror|alshifa_rb_[0-9_a-f]*) ;; *) echo "Unexpected active database '$active_db'." >&2; exit 1;; esac
previous=$(readlink -f /opt/alshifa/api)
test "$previous" = "$(readlink -f /opt/alshifa/current)"
echo "Active database: $active_db; running release: $previous"

# shellcheck disable=SC1091
. /etc/alshifa/mirror-owner.env
owner=${ALSHIFA_MIRROR_OWNER:?}
owner_active=$(printf '%s' "$owner" | sed -E "s/Database=[^;]+;/Database=$active_db;/")
owner_verify=$(printf '%s' "$owner" | sed -E "s/Database=[^;]+;/Database=$verify_db;/")

root_mysql() { docker exec -i "$container" sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot "$@"' sh "$@"; }
dump() { docker exec "$container" sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysqldump -uroot --single-transaction --quick --routines --events --triggers --hex-blob --no-tablespaces --set-gtid-purged=OFF "$1"' sh "$1"; }
counts() {
  local d=$1 q="" t
  for t in Patients Visits Invoices Payments Products StockBatches AspNetUsers SyncStates SyncCommands; do
    q="$q${q:+ UNION ALL }SELECT '$t',COUNT(*) FROM \`$d\`.\`$t\`"
  done
  printf '%s;\n' "$q" | root_mysql -N -B
}
migrate() {
  ( cd "$release" && runuser -u alshifa -- env ASPNETCORE_ENVIRONMENT=Production Mirror__Enabled=true \
      ConnectionStrings__Default="$1" Storage__Path=/var/lib/alshifa/storage DataProtection__KeysPath=/var/lib/alshifa/keys \
      ./AlShifa.Api --migrate --backup-taken )
}

mkdir -p "$release"
install -d -m 700 "$backup_dir"
tar -xzf "$archive" -C "$release"
test -f "$release/AlShifa.Api" && test -f "$release/wwwroot/index.html"
# Owned by root, readable and runnable only by the service group: the previous cutover failed on exactly this.
chown -R root:alshifa "$release"
chmod -R u=rwX,g=rX,o= "$release"
chmod 750 "$release/AlShifa.Api"
runuser -u alshifa -- test -x "$release/AlShifa.Api"
runuser -u alshifa -- test -r "$release/wwwroot/index.html"

# Rehearsal: the real backup restored into a clone, migrated with the new release. Production is not touched.
dump "$active_db" | gzip > "$backup_dir/rehearsal.sql.gz"
gzip -t "$backup_dir/rehearsal.sql.gz"
cleanup_verify() {
  printf 'DROP DATABASE IF EXISTS `%s`; REVOKE ALL PRIVILEGES ON `%s`.* FROM '\''alshifa_mirror_owner'\''@'\''%%'\'';\n' "$verify_db" "$verify_db" | root_mysql 2>/dev/null || true
}
trap cleanup_verify EXIT
printf 'CREATE DATABASE `%s` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci; GRANT ALL PRIVILEGES ON `%s`.* TO '\''alshifa_mirror_owner'\''@'\''%%'\'';\n' "$verify_db" "$verify_db" | root_mysql
gunzip -c "$backup_dir/rehearsal.sql.gz" | root_mysql "$verify_db"
counts "$verify_db" > "$backup_dir/clone-before.txt"
migrate "$owner_verify" > "$backup_dir/clone-migration.log" 2>&1
counts "$verify_db" > "$backup_dir/clone-after.txt"
cmp "$backup_dir/clone-before.txt" "$backup_dir/clone-after.txt"
printf 'SELECT MigrationId FROM `%s`.__EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;\n' "$verify_db" | root_mysql -N -B > "$backup_dir/clone-schema.txt"
cleanup_verify
trap - EXIT
echo "Rehearsal passed: counts preserved, clone schema $(cat "$backup_dir/clone-schema.txt")."
if [ "$mode" = "--check-only" ]; then echo "Check only: production unchanged. Release staged at $release."; exit 0; fi

tar -czf "$backup_dir/server-config.tar.gz" /etc/alshifa /etc/systemd/system/alshifa-api.service /etc/systemd/system/alshifa-api.service.d /var/lib/alshifa/keys 2>/dev/null
gzip -t "$backup_dir/server-config.tar.gz"

stopped=0
switched=0
rollback() {
  local rc=$?
  set +e
  [ "$stopped" = 1 ] || return "$rc"
  echo "DEPLOY FAILED ($rc): restoring $previous and the database from $backup_dir/final.sql.gz"
  systemctl stop alshifa-api
  if [ -s "$backup_dir/final.sql.gz" ]; then
    # The dump recreates every table it holds; tables added by the new migration are left unused by the old version.
    gunzip -c "$backup_dir/final.sql.gz" | root_mysql "$active_db"
    printf "GRANT UPDATE (AccessFailedCount, LockoutEnd, ConcurrencyStamp) ON \`%s\`.\`AspNetUsers\` TO 'alshifa_mirror_ro'@'%%';\n" "$active_db" | root_mysql
  fi
  rm -f "$dropin"
  ln -sfn "$previous" /opt/alshifa/api.next && mv -Tf /opt/alshifa/api.next /opt/alshifa/api
  ln -sfn "$previous" /opt/alshifa/current.next && mv -Tf /opt/alshifa/current.next /opt/alshifa/current
  systemctl daemon-reload
  systemctl start alshifa-api
  return "$rc"
}
trap rollback EXIT

systemctl stop alshifa-api
stopped=1
# Nothing reaches the database while stopped: the hospital's batches wait in its own queue and are re-sent.
dump "$active_db" | gzip > "$backup_dir/final.sql.gz"
gzip -t "$backup_dir/final.sql.gz"
counts "$active_db" > "$backup_dir/production-before.txt"
migrate "$owner_active" > "$backup_dir/production-migration.log" 2>&1
counts "$active_db" > "$backup_dir/production-after.txt"
cmp "$backup_dir/production-before.txt" "$backup_dir/production-after.txt"
printf "GRANT UPDATE (AccessFailedCount, LockoutEnd, ConcurrencyStamp) ON \`%s\`.\`AspNetUsers\` TO 'alshifa_mirror_ro'@'%%';\n" "$active_db" | root_mysql

# The database download is now built in; the old add-on assembly must no longer be loaded. Its connection setting stays.
printf '[Service]\nEnvironment=ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=\n' > "$dropin"
# Deployed ahead of the hospital: keep accepting the hospital's current version (space-separated migration names) until it upgrades.
i=0
for schema in ${ALSHIFA_ACCEPTED_SOURCE_SCHEMAS:-}; do
  case "$schema" in *[!A-Za-z0-9_]*) echo "Invalid schema name '$schema'." >&2; exit 1;; esac
  printf 'Environment=Mirror__AcceptedSourceSchemas__%s=%s\n' "$i" "$schema" >> "$dropin"
  i=$((i+1))
done
chmod 644 "$dropin"
ln -sfn "$release" /opt/alshifa/api.next && mv -Tf /opt/alshifa/api.next /opt/alshifa/api
ln -sfn "$release" /opt/alshifa/current.next && mv -Tf /opt/alshifa/current.next /opt/alshifa/current
switched=1
systemctl daemon-reload
systemctl start alshifa-api

ok=0
for _ in $(seq 1 60); do
  if curl -fsS http://127.0.0.1:5080/health 2>/dev/null | grep -q Healthy; then ok=1; break; fi
  sleep 1
done
test "$ok" = 1
curl -fsS --resolve shafa.novatechsoft.com:443:127.0.0.1 https://shafa.novatechsoft.com/ | grep -qi '<!doctype html>'
test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/api/sync/mirror/state)" = 401
test "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/api/sync/commands/)" = 401

trap - EXIT
echo "Deployed $release on $active_db (previous: $previous, backups: $backup_dir, switched=$switched)."
