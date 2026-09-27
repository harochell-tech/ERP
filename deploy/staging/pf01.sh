#!/usr/bin/env bash
# PF-01 on staging (E-B03-16), run on the VPS by the pf01-staging workflow. Measures the staging hardware without touching the
# staging data: a separate, temporary database rochell_pf01 on the same PostgreSQL container, the load harness image on the
# compose internal network, then the database and the harness's own logins are dropped, whatever happened.
#
# Usage: pf01.sh <harness-image> [receipts] [workers]   report in ./pf01-report (pf01.json, pf01.md, harness.log)
#        pf01.sh --cleanup                              only the cleanup (the workflow calls it again with if: always())
#
# Never connects to the rochell database: every psql call targets the maintenance database "postgres", and the harness gets a
# connection string for rochell_pf01 only (it also refuses a database that is not empty).
set -euo pipefail
cd "${ROCHELL_STAGING_DIR:-/opt/rochell-staging}"

readonly db=rochell_pf01
readonly app_login=rochell_app_pf01
readonly sealer_login=rochell_sealer_pf01
readonly container=rochell-pf01
readonly network=rochell-staging_internal
readonly report_dir="${PWD}/pf01-report"

# .env holds DEPLOY_DB_PASSWORD and the variables compose.yaml requires (written by deploy-staging).
set -a; . ./.env; set +a

psql_admin() {
  docker compose exec -T postgres psql -U rochell_deploy -d postgres -v ON_ERROR_STOP=1 -qtA "$@"
}

drop_all() {
  docker rm -f "${container}" >/dev/null 2>&1 || true
  psql_admin -c "SET client_min_messages = warning" \
             -c "DROP DATABASE IF EXISTS ${db} WITH (FORCE)" \
             -c "DROP ROLE IF EXISTS ${app_login}" \
             -c "DROP ROLE IF EXISTS ${sealer_login}"
  local left
  left="$(psql_admin -c "SELECT (SELECT count(*) FROM pg_database WHERE datname = '${db}')
                              + (SELECT count(*) FROM pg_roles WHERE rolname IN ('${app_login}', '${sealer_login}'))")"
  if [[ "${left}" != "0" ]]; then
    echo "Cleanup incomplete: ${db} or its logins still exist" >&2
    return 1
  fi
  echo "Cleanup done: ${db}, ${app_login} and ${sealer_login} do not exist."
}

if [[ "${1:-}" == "--cleanup" ]]; then
  drop_all
  exit 0
fi

image="${1:?harness image required}"
receipts="${2:-10000}"
workers="${3:-8}"
if ! [[ "${receipts}" =~ ^[1-9][0-9]{0,5}$ && "${workers}" =~ ^[1-9][0-9]?$ ]]; then
  echo "receipts must be 1..999999 and workers 1..99" >&2
  exit 2
fi

cleanup() {
  local status=$?
  trap - EXIT
  drop_all || status=1
  docker image rm "${image}" >/dev/null 2>&1 || true
  exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT TERM HUP

docker network inspect "${network}" >/dev/null
test "$(psql_admin -c 'SELECT 1')" = "1"

# A leftover from an interrupted run is ours by name; start from nothing.
drop_all
psql_admin -c "CREATE DATABASE ${db}"

# The harness creates these two logins (members of rochell_app / rochell_sealer) in the new database; they are cluster-wide, so
# they get throw-away random passwords and are dropped with the database. rochell_app_login / rochell_sealer_login are untouched.
ROCHELL_PF01_ADMIN_CONNECTION="Host=postgres;Database=${db};Username=rochell_deploy;Password=${DEPLOY_DB_PASSWORD}"
ROCHELL_PF01_APP_LOGIN="${app_login}"
ROCHELL_PF01_APP_PASSWORD="$(openssl rand -hex 24)"
ROCHELL_PF01_SEALER_LOGIN="${sealer_login}"
ROCHELL_PF01_SEALER_PASSWORD="$(openssl rand -hex 24)"
export ROCHELL_PF01_ADMIN_CONNECTION ROCHELL_PF01_APP_LOGIN ROCHELL_PF01_APP_PASSWORD ROCHELL_PF01_SEALER_LOGIN ROCHELL_PF01_SEALER_PASSWORD

rm -rf "${report_dir}"
mkdir -p "${report_dir}"

# Secrets reach the container as inherited environment variables (-e NAME), never on the command line.
set +e
docker run --name "${container}" --rm --init --network "${network}" \
  --user "$(id -u):$(id -g)" -e HOME=/tmp \
  -e ROCHELL_PF01_ADMIN_CONNECTION -e ROCHELL_PF01_APP_LOGIN -e ROCHELL_PF01_APP_PASSWORD \
  -e ROCHELL_PF01_SEALER_LOGIN -e ROCHELL_PF01_SEALER_PASSWORD \
  -v "${report_dir}:/report" \
  "${image}" --receipts "${receipts}" --workers "${workers}" --report /report 2>&1 | tee "${report_dir}/harness.log"
harness_status=${PIPESTATUS[0]}
set -e

echo "Harness exit code: ${harness_status}"
exit "${harness_status}"
