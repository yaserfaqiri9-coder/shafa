#!/usr/bin/env bash
# Offers a signed hospital release to the hospital's automatic updater (deploy/windows/auto-update.ps1).
# Run as root on the online server, AFTER deploy-online-release.sh installed the same build here:
#   publish-hospital-update.sh /root/alshifa-update-<version>     # folder with manifest.json, manifest.json.sig, alshifa-<version>.zip
# The release is refused unless its database version equals the online copy's, so the hospital never upgrades
# to a version whose sync the online copy would reject. The hospital checks the signature itself; this server
# holds no signing key. Only the two newest packages are kept (the disk is small).
set -euo pipefail
umask 022
exec 9>/run/lock/alshifa-deploy.lock
flock -n 9 || { echo 'Another AlShifa deployment is running.' >&2; exit 1; }

staging=${1:?folder with the signed update}
site_dir=/var/www/alshifa-updates
site=/etc/nginx/sites-enabled/shafa.novatechsoft.com
container=alshifa-mysql

read -r version schema sha size < <(python3 - "$staging/manifest.json" <<'PY'
import json, re, sys
m = json.load(open(sys.argv[1], encoding="utf-8-sig"))
assert re.fullmatch(r"\d+\.\d+\.\d+\.\d+", m["version"]), "bad version"
assert re.fullmatch(r"[A-Za-z0-9_]+", m.get("schema") or ""), "manifest has no database version"
assert re.fullmatch(r"[A-Fa-f0-9]{64}", m["sha256"]), "bad hash"
print(m["version"], m["schema"], m["sha256"].lower(), int(m["sizeBytes"]))
PY
)
package="$staging/alshifa-$version.zip"
test -s "$staging/manifest.json.sig"
test "$(stat -c %s "$package")" = "$size"
test "$(sha256sum "$package" | awk '{print $1}')" = "$sha"

# The database the running online service really uses (same rule as deploy-online-release.sh).
active_db=""
for file in $(systemctl show alshifa-api -p EnvironmentFiles --value | tr ' ' '\n' | grep '^/' ); do
  line=$(grep -E '^ConnectionStrings__Default=' "$file" 2>/dev/null | tail -1 || true)
  [ -n "$line" ] && active_db=$(printf '%s' "$line" | sed -E 's/.*Database=([^;"]+).*/\1/')
done
case "$active_db" in alshifa_mirror|alshifa_rb_[0-9_a-f]*) ;; *) echo "Unexpected active database '$active_db'." >&2; exit 1;; esac
online_schema=$(printf 'SELECT MigrationId FROM `%s`.__EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;\n' "$active_db" |
  docker exec -i "$container" sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot -N -B')
if [ "$online_schema" != "$schema" ]; then
  echo "Refused: release database version $schema, online copy $online_schema. Deploy this build online first." >&2
  exit 1
fi

mkdir -p "$site_dir"
install -m 644 "$package" "$site_dir/alshifa-$version.zip.tmp"
mv -f "$site_dir/alshifa-$version.zip.tmp" "$site_dir/alshifa-$version.zip"
install -m 644 "$staging/manifest.json.sig" "$site_dir/manifest.json.sig.tmp"
install -m 644 "$staging/manifest.json" "$site_dir/manifest.json.tmp"
# A hospital that reads the manifest between these two renames sees a signature mismatch and simply retries next hour.
mv -f "$site_dir/manifest.json.sig.tmp" "$site_dir/manifest.json.sig"
mv -f "$site_dir/manifest.json.tmp" "$site_dir/manifest.json"
ls -1t "$site_dir"/alshifa-*.zip | tail -n +3 | xargs -r rm -f

if ! grep -q 'location /updates/' "$site"; then
  cp -a "$site" "/root/alshifa-nginx-site.$(date -u +%Y%m%dT%H%M%SZ).bak"
  python3 - "$site" <<'PY'
import sys
path = sys.argv[1]
text = open(path).read()
block = """    # Signed hospital updates (static files; the hospital verifies the signature).
    location /updates/ {
        alias /var/www/alshifa-updates/;
        autoindex off;
        add_header Cache-Control "no-cache";
    }

"""
anchor = "    location / {\n        include /etc/nginx/alshifa-proxy.conf;"
index = text.rfind(anchor)
if index < 0:
    sys.exit("Nginx site layout not recognised; add the /updates/ location by hand.")
open(path, "w").write(text[:index] + block + text[index:])
PY
  nginx -t
  systemctl reload nginx
fi

# nginx reloads in the background: give the new workers a few seconds to serve /updates/.
served=0
for _ in $(seq 1 15); do
  if [ "$(curl -fsS --resolve shafa.novatechsoft.com:443:127.0.0.1 https://shafa.novatechsoft.com/updates/manifest.json 2>/dev/null | sha256sum | awk '{print $1}')" = "$(sha256sum "$site_dir/manifest.json" | awk '{print $1}')" ]; then served=1; break; fi
  sleep 1
done
test "$served" = 1
echo "Published hospital update $version (database $schema). Hospitals install it between 03:00 and 05:00."
