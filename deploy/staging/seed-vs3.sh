#!/usr/bin/env bash
# VS#3 synthetic setup of an existing staging (E-VS3-10-11), run on the VPS after deploying VS3: one test identity per sales role
# (a second Facturación identity issues credit notes, four eyes) and the sales chart of accounts with DRAFT account maps, approved
# later in the UI ("Mapas de cuentas"). Identities that already exist are left as they are. Usage: seed-vs3.sh
set -euo pipefail
cd /opt/rochell-staging
rnc=131925332
m() { docker compose run --rm -T migrate "$@"; }

for pair in vendedor:VENDEDOR credito:CREDITO despacho:DESPACHO facturacion:FACTURACION facturacion2:FACTURACION cobros:COBROS; do
  email="${pair%%:*}@staging.invalid"
  m create-synthetic-user "$email" || echo "$email already exists"
  m grant-role "$email" "${pair##*:}" "$rnc" || echo "$email already holds ${pair##*:}"
done
m import-accounts "$rnc" /dev/stdin < seed/accounts-vs3.csv
m import-account-map "$rnc" /dev/stdin < seed/account-map-vs3.csv
echo "VS#3 seeded. In the UI as controller: approve the new maps and the sales posting rules; set CREDIT and REVENUE_ACCOUNTING."
