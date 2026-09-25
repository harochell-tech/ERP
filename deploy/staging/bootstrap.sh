#!/usr/bin/env bash
# One-time setup of the staging VPS (B-03), run as the deploy user with sudo rights. Idempotent.
# Creates /opt/rochell-staging and the digest key pair (E-B03-6): the private key never leaves this server.
set -euo pipefail
ROOT=/opt/rochell-staging

command -v docker >/dev/null || { echo "Install Docker Engine and the compose plugin first (docs/engineering/staging.md)." >&2; exit 1; }
sudo mkdir -p "$ROOT/secrets"
sudo chown -R "$(id -u):$(id -g)" "$ROOT"
chmod 700 "$ROOT/secrets"

if [ ! -f "$ROOT/secrets/digest-signing.pem" ]; then
  openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$ROOT/secrets/digest-signing.pem"
  openssl pkey -in "$ROOT/secrets/digest-signing.pem" -pubout -out "$ROOT/secrets/digest-public.pem"
  # Readable only by the container user (app, uid 1654) of the .NET image.
  sudo chown 1654:1654 "$ROOT/secrets/digest-signing.pem" "$ROOT/secrets/digest-public.pem"
  sudo chmod 400 "$ROOT/secrets/digest-signing.pem"
  sudo chmod 444 "$ROOT/secrets/digest-public.pem"
  echo "Digest key pair created. Public key (share it with the auditor):"
  cat "$ROOT/secrets/digest-public.pem"
else
  echo "Digest key pair already present; left untouched."
fi
