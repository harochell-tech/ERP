#!/usr/bin/env bash
# Runs on the staging VPS from the deploy-staging workflow (E-B03-5): pull the image, migrate, logins, restart, smoke check.
# Usage: deploy.sh <image>   (reads /opt/rochell-staging/.env)
set -euo pipefail
cd /opt/rochell-staging
image="${1:?image required}"
sed -i "s|^ROCHELL_IMAGE=.*|ROCHELL_IMAGE=${image}|" .env
set -a; . ./.env; set +a

docker compose pull api
docker compose up -d postgres
docker compose run --rm migrate migrate
# Patch 1.1: a staging database is TEST (only TEST or PRODUCTION exist); written once, never changed.
docker compose run --rm migrate init-environment TEST
docker compose exec -T postgres psql -U rochell_deploy -d rochell -q \
  -v app_password="$APP_DB_PASSWORD" -v sealer_password="$SEALER_DB_PASSWORD" < init-roles.sql
docker compose up -d --remove-orphans api caddy

for attempt in $(seq 1 30); do
  if curl -fsS -o /dev/null "https://${STAGING_HOST}/"; then
    echo "Staging is up: https://${STAGING_HOST}/ (${image})"
    exit 0
  fi
  sleep 5
done
echo "Staging did not answer on https://${STAGING_HOST}/" >&2
docker compose logs --tail 100 api caddy >&2
exit 1
