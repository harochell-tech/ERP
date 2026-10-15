#!/usr/bin/env bash
# ENT-1 (E-ENT-1/5): the drivers' page keys, written by deploy-staging when the Environment has EVIDENCE_BUCKET.
# Reads two lines on stdin — the bucket's Application Key ID and the Application Key — and writes them to secrets/deliveries with
# the API's uid (1654), mode 0400 (directory 0500). The QR link key is generated here once and kept: changing it would invalidate
# every printed QR. A throwaway container does the writing, so the deploy user needs no sudo.
set -euo pipefail

IFS= read -r key_id
IFS= read -r key
test -n "${key_id}" && test -n "${key}" || { echo "evidence-keys.sh: the key id and the key are required on stdin" >&2; exit 1; }

printf '%s\n%s\n' "${key_id}" "${key}" | docker run --rm -i --network none -v /opt/rochell-staging/secrets:/s alpine:3.20 sh -euc '
  IFS= read -r id
  IFS= read -r secret
  mkdir -p /s/deliveries
  chmod 700 /s/deliveries
  printf "%s" "$id" > /s/deliveries/evidence-key-id
  printf "%s" "$secret" > /s/deliveries/evidence-key
  if [ ! -s /s/deliveries/link-key ]; then head -c 32 /dev/urandom | base64 > /s/deliveries/link-key; echo "link-key created"; fi
  chown -R 1654:1654 /s/deliveries
  chmod 400 /s/deliveries/*
  chmod 500 /s/deliveries
'
echo "Drivers' page keys in place."
