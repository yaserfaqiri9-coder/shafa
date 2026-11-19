#!/usr/bin/env bash
# An isolated destination for office acceptance. Never reads or replaces production business data.
set -euo pipefail
umask 077
release=${1:?verified release directory}
db=alshifa_office_test
root=/etc/alshifa-office
state=/var/lib/alshifa-office
service=alshifa-office-api
container=alshifa-mysql
test -x "$release/AlShifa.Api"
case "$release" in /opt/alshifa/releases/*) ;; *) echo 'Unexpected release directory.' >&2; exit 1;; esac
if [ -f "$root/mirror.env" ]; then
  systemctl is-active "$service"
  echo 'Office destination already exists; existing data and keys preserved.'
  exit 0
fi
test ! -e "/etc/systemd/system/$service.service"
test ! -e "$root"
test ! -e "$state"
test -z "$(ss -ltnH '( sport = :5081 )')"
mysql_root() { docker exec -i "$container" sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot'; }
test -z "$(printf "SHOW DATABASES LIKE '%s';\n" "$db" | mysql_root | tail -n +2)"
test -z "$(printf "SELECT User FROM mysql.user WHERE User IN ('alshifa_office_owner','alshifa_office_sync','alshifa_office_ro');\n" | mysql_root | tail -n +2)"
id alshifa-office >/dev/null 2>&1 || useradd --system --no-create-home --home-dir "$state" --shell /usr/sbin/nologin alshifa-office
install -d -m 700 "$root"
install -d -o alshifa-office -g alshifa-office -m 700 "$state" "$state/storage" "$state/keys"
owner_pass=$(openssl rand -hex 24)
sync_pass=$(openssl rand -hex 24)
ro_pass=$(openssl rand -hex 24)
sync_key=$(openssl rand -hex 32)
mysql_root <<SQL
CREATE DATABASE \`$db\` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER 'alshifa_office_owner'@'%' IDENTIFIED BY '$owner_pass';
GRANT ALL PRIVILEGES ON \`$db\`.* TO 'alshifa_office_owner'@'%';
CREATE USER 'alshifa_office_sync'@'%' IDENTIFIED BY '$sync_pass';
GRANT SELECT, INSERT, UPDATE, DELETE ON \`$db\`.* TO 'alshifa_office_sync'@'%';
CREATE USER 'alshifa_office_ro'@'%' IDENTIFIED BY '$ro_pass';
GRANT SELECT ON \`$db\`.* TO 'alshifa_office_ro'@'%';
SQL
connection() { printf 'Server=127.0.0.1;Port=3316;Database=%s;User=%s;Password=%s;' "$db" "$1" "$2"; }
owner_connection=$(connection alshifa_office_owner "$owner_pass")
cat > "$root/mirror.env" <<EOF
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5081
Mirror__Enabled=true
Mirror__SyncKey=$sync_key
Mirror__SyncConnection="$(connection alshifa_office_sync "$sync_pass")"
ConnectionStrings__Default="$(connection alshifa_office_ro "$ro_pass")"
Storage__Path=$state/storage
DataProtection__KeysPath=$state/keys
EOF
printf 'ALSHIFA_MIRROR_OWNER="%s"\n' "$owner_connection" > "$root/mirror-owner.env"
printf '%s\n' "$sync_key" > "$root/sync-key.txt"
(
  cd "$release"
  sudo -u alshifa-office env ASPNETCORE_ENVIRONMENT=Production Mirror__Enabled=true \
    ConnectionStrings__Default="$owner_connection" Storage__Path="$state/storage" DataProtection__KeysPath="$state/keys" \
    ./AlShifa.Api --migrate --backup-taken
) > "$root/initialize.log" 2>&1
printf "GRANT UPDATE (AccessFailedCount, LockoutEnd, ConcurrencyStamp) ON \`%s\`.\`AspNetUsers\` TO 'alshifa_office_ro'@'%%';\n" "$db" | mysql_root
cat > "/etc/systemd/system/$service.service" <<EOF
[Unit]
Description=AlShifa isolated office acceptance API
After=network-online.target docker.service
Wants=network-online.target
[Service]
User=alshifa-office
Group=alshifa-office
WorkingDirectory=$release
ExecStart=$release/AlShifa.Api
EnvironmentFile=$root/mirror.env
Restart=on-failure
RestartSec=5
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$state
[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now "$service" >/dev/null
ready=0
for _ in $(seq 1 30); do
  if curl -fsS http://127.0.0.1:5081/health 2>/dev/null | grep -q Healthy; then ready=1; break; fi
  sleep 1
done
test "$ready" = 1
echo 'Isolated office API ready on loopback 5081; empty separate database, independent users, files and keys.'
echo 'Production service, DNS, business data and sync key were not modified.'
