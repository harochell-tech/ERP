#!/usr/bin/env bash
# E-B03-8: daily encrypted dump of the staging database to the second provider (bucket with a 7-day expiry rule).
# Encrypted to backup-cert.pem (X.509 certificate; its private key is kept offline). Schedule: /etc/cron.d/rochell-backup
#   30 1 * * * deploy /opt/rochell-staging/backup.sh >> /var/log/rochell-backup.log 2>&1
set -euo pipefail
cd /opt/rochell-staging
set -a; . ./.env; set +a
stamp="$(date -u +%Y-%m-%dT%H%M%SZ)"

docker compose exec -T postgres pg_dump -U rochell_deploy -d rochell -Fc \
  | openssl cms -encrypt -binary -aes256 -outform DER secrets/backup-cert.pem \
  | docker run --rm -i -e AWS_ACCESS_KEY_ID="$BACKUP_ACCESS_KEY_ID" -e AWS_SECRET_ACCESS_KEY="$BACKUP_SECRET_ACCESS_KEY" \
      -e AWS_DEFAULT_REGION="$WORM_REGION" amazon/aws-cli s3 cp - "s3://${BACKUP_BUCKET}/staging/rochell-${stamp}.dump.cms"
echo "Backup rochell-${stamp}.dump.cms uploaded."
