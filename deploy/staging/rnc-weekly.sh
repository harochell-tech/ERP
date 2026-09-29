#!/usr/bin/env bash
# E-RNC-2 / E-RNC-8: weekly import of the DGII registry ("Listado de todos los RNC", public data) into md.rnc_registry.
# Usage: rnc-weekly.sh [DGII_RNC.zip]   — without an argument it downloads the file; with one it imports that file.
# The DGII may refuse automated downloads (HTTP 403). The script does not disguise itself as a browser: download the ZIP by hand
# from the DGII page and run the script with its path. Schedule: /etc/cron.d/rochell-rnc
#   0 5 * * 1 deploy /opt/rochell-staging/rnc-weekly.sh >> /var/log/rochell-rnc.log 2>&1
set -euo pipefail
cd /opt/rochell-staging
url=https://dgii.gov.do/app/WebApps/Consultas/RNC/DGII_RNC.zip
mkdir -p rnc

if [ $# -ge 1 ]; then
  cp "$1" rnc/DGII_RNC.zip
else
  status="$(curl -sS -L -o rnc/DGII_RNC.zip.part -w '%{http_code}' "$url" || true)"
  if [ "$status" != "200" ]; then
    rm -f rnc/DGII_RNC.zip.part
    echo "The DGII answered HTTP $status. Download DGII_RNC.zip in a browser from" >&2
    echo "  https://dgii.gov.do/herramientas/consultas/Paginas/RNC.aspx  (\"Listado de RNC\")" >&2
    echo "copy it to the VPS and run: /opt/rochell-staging/rnc-weekly.sh /path/to/DGII_RNC.zip" >&2
    exit 1
  fi
  mv rnc/DGII_RNC.zip.part rnc/DGII_RNC.zip
fi

chmod 644 rnc/DGII_RNC.zip
docker compose run --rm -T -v "$PWD/rnc:/rnc:ro" migrate import-rnc-registry /rnc/DGII_RNC.zip
