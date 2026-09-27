#!/usr/bin/env bash
# One-time synthetic setup of staging (E-B03-7, E-B03-13, E-B03-14, E-B03-15-2), run on the VPS after the first deploy.
# Gives the tester the PROBADOR role, creates one test identity per slice role, and loads a synthetic chart of accounts and
# DRAFT account maps (approved later in the UI, "Mapas de cuentas"). Usage: seed.sh <tester-email>
set -euo pipefail
cd /opt/rochell-staging
tester="${1:?tester e-mail required (e.g. alex@rochell.com.do)}"
rnc=131925332
m() { docker compose run --rm -T migrate "$@"; }

m grant-role "$tester" PROBADOR "$rnc"
for pair in comprador:COMPRADOR aprobador:APROBADOR_COMPRAS almacen:ALMACENISTA cxp:CUENTAS_POR_PAGAR controller:CONTROLLER \
            politicas:APROBADOR_POLITICAS analista:ANALISTA_FISCAL fiscal:ESPECIALISTA_FISCAL tesorero:TESORERO \
            auditor:AUDITOR cierre:SEGUNDO_APROBADOR_CIERRE seguridad:ADMIN_SEGURIDAD seguridad2:SEGUNDO_APROBADOR_SEGURIDAD \
            director:DIRECTOR; do
  email="${pair%%:*}@staging.invalid"
  m create-synthetic-user "$email"
  m grant-role "$email" "${pair##*:}" "$rnc"
done
m import-accounts "$rnc" /dev/stdin < seed/accounts.csv
m import-account-map "$rnc" /dev/stdin < seed/account-map.csv
echo "Staging seeded. In the UI: \"Actuar como…\" to switch identities; approve maps and posting rules as controller."
