#!/usr/bin/env bash
# Publishes only the isolated office API. The production virtual host is never edited.
set -euo pipefail
host=test-shafa.novatechsoft.com
site=/etc/nginx/sites-available/$host
enabled=/etc/nginx/sites-enabled/$host
webroot=/var/www/alshifa-office-acme
cert=/etc/letsencrypt/live/$host
curl -fsS http://127.0.0.1:5081/health | grep -q Healthy
getent ahostsv4 "$host" | awk '{print $1}' | grep -qx 157.173.109.255
if [ -e "$site" ]; then
  grep -q '^# AlShifa isolated office acceptance$' "$site" || { echo 'Existing virtual host is not owned by this setup.' >&2; exit 1; }
else
  test ! -e "$enabled"
  install -d -m 755 "$webroot"
  cat > "$site" <<EOF
# AlShifa isolated office acceptance
server {
    listen 80;
    server_name $host;
    location /.well-known/acme-challenge/ { root $webroot; }
    location / { return 404; }
}
EOF
  ln -s "$site" "$enabled"
  nginx -t 2>/dev/null
  systemctl reload nginx
fi
certbot certonly --non-interactive --agree-tos --email yaserfaqiri9@gmail.com --webroot -w "$webroot" --cert-name "$host" -d "$host" --keep-until-expiring > /etc/alshifa-office/certificate.log 2>&1
test -s "$cert/fullchain.pem"
cp "$site" "$site.before-https"
cat > "$site" <<'EOF'
# AlShifa isolated office acceptance
server {
    listen 80;
    server_name test-shafa.novatechsoft.com;
    location /.well-known/acme-challenge/ { root /var/www/alshifa-office-acme; }
    location / { return 301 https://$host$request_uri; }
}
server {
    listen 443 ssl;
    server_name test-shafa.novatechsoft.com;
    ssl_certificate /etc/letsencrypt/live/test-shafa.novatechsoft.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/test-shafa.novatechsoft.com/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    add_header X-Content-Type-Options nosniff always;
    add_header X-Frame-Options DENY always;
    client_max_body_size 64m;
    location / {
        proxy_pass http://127.0.0.1:5081;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 300s;
    }
}
EOF
if ! nginx -t 2>/dev/null; then cp "$site.before-https" "$site"; exit 1; fi
systemctl reload nginx
curl -fsS "https://$host/login" | grep -qi '<!doctype html>'
test "$(curl -s -o /dev/null -w '%{http_code}' "https://$host/api/sync/mirror/state")" = 401
echo 'Isolated office HTTPS ready with a publicly trusted certificate.'
