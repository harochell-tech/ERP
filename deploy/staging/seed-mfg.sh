#!/usr/bin/env bash
# MFG-1 synthetic setup of an existing staging (E-MFG1-07-7), run on the VPS after deploying MFG1-01…07: one test identity per
# production role and the production accounts with DRAFT maps (MATERIAL_USAGE_VARIANCE already maps to 5102), approved later in
# the UI ("Mapas de cuentas"). Identities that already exist are left as they are. Usage: seed-mfg.sh
set -euo pipefail
cd /opt/rochell-staging
rnc=131925332
m() { docker compose run --rm -T migrate "$@"; }

for pair in supervisor:SUPERVISOR_PRODUCCION gerente-planta:GERENTE_PLANTA calidad:CALIDAD; do
  email="${pair%%:*}@staging.invalid"
  m create-synthetic-user "$email" || echo "$email already exists"
  m grant-role "$email" "${pair##*:}" "$rnc" || echo "$email already holds ${pair##*:}"
done
m import-accounts "$rnc" /dev/stdin < seed/accounts-mfg.csv
m import-account-map "$rnc" /dev/stdin < seed/account-map-mfg.csv
echo "MFG-1 seeded. In the UI as controller: approve the new maps and P-08, P-10, P-12, P-13, REVAL; prepare PRODUCTION (usage_tolerance_pct)."
