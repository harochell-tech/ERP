# Staging (B-03)

Approved errata E-B03-1…9. Staging runs the same image as production on the Hostinger `sistema` VPS (ADR-026, E-B03-11; never the portal VPS, which holds
real production data), with synthetic data only
(E-VS1-2, E-VS2-10). B-03 closes when the checklist at the end is done (E-B03-9).

## Pieces

| Piece | Where | Notes |
| --- | --- | --- |
| Image | `Dockerfile` → `ghcr.io/harochell-tech/rochell-core:<sha>` | API + web static export (`/app/web`) + deployment CLI (`/app/migrate/rochell-migrate.dll`); non-root (uid 1654); no secrets |
| Stack | `deploy/staging/compose.yaml` in `/opt/rochell-staging` | `postgres` (17.6, internal network only), `api`, `caddy` (only 443/tcp+udp published), `migrate` (tools profile) |
| TLS | `deploy/staging/Caddyfile` | Let's Encrypt via TLS-ALPN on 443 (port 80 stays closed, E-B03-2); HSTS |
| Proxy trust | `Rochell:ReverseProxy:TrustedNetworks` = `172.30.0.0/24` (the `edge` network) | The API honours `X-Forwarded-For/Proto` only from Caddy, so the OIDC redirect and cookies are `https` |
| Logins | `deploy/staging/init-roles.sql` | `rochell_app_login` ∈ rochell_app, `rochell_sealer_login` ∈ rochell_sealer; passwords reset on each deploy |
| WORM | Backblaze B2 Object Lock (S3 API), COMPLIANCE, 7 days (E-B03-3, E-B03-10) | `Rochell:Audit:S3:*` incl. `ServiceUrl`; see [hash-chain.md](hash-chain.md) |
| Digest keys | `/opt/rochell-staging/secrets/digest-{signing,public}.pem` (`bootstrap.sh`) | Private key generated on the server, `0400`, uid 1654; `Rochell:Digest:SigningKeyPemFile`, `Rochell:Audit:DigestPublicKeyPemFile` (E-B03-6) |
| Deploy | `.github/workflows/deploy-staging.yml` → `deploy/staging/deploy.sh` | Manual, `main` only, refuses a commit without a green `build-test`; migrate → `init-environment TEST` (Patch 1.1) → logins → restart → HTTPS smoke check |
| Backup | `deploy/staging/backup.sh` (cron 01:30) | `pg_dump -Fc` → `openssl cms` (AES-256, to `secrets/backup-cert.pem`) → B2 bucket with a 7-day lifecycle rule (E-B03-8, E-B03-10) |

## One-time setup

### 1. Backblaze B2 (E-B03-10; Alexander creates the account; nothing is sent by chat)

Staging host: `staging.industriasrochell.com.do`. Bucket names in B2 are global, hence the company prefix.

1. Account at backblaze.com (B2 Cloud Storage), with 2FA.
2. Bucket `industriasrochell-worm-staging`: **Private**, **Object Lock: Enable** (no default retention: the store sets
   COMPLIANCE, 7 days, on every object). The bucket page shows the S3 endpoint (`s3.<region>.backblazeb2.com`) and the
   region (`<region>`, e.g. `us-east-005`) → `WORM_ENDPOINT` = `https://s3.<region>.backblazeb2.com`, `WORM_REGION` = `<region>`.
3. Bucket `industriasrochell-backup-staging` in the **same region**: Private, lifecycle rule "Keep prior versions for 7 days"
   and delete hidden files after 7 days.
4. Application keys with the B2 CLI (`pip install b2`, `b2 account authorize` with the master key), restricted per bucket:

   ```bash
   b2 key create --bucket industriasrochell-worm-staging rochell-staging-sealer \
     listBuckets,readBuckets,readBucketRetentions,listFiles,readFiles,writeFiles,readFileRetentions,writeFileRetentions
   b2 key create --bucket industriasrochell-backup-staging rochell-staging-backup listBuckets,writeFiles
   ```

   Each prints a keyID and an applicationKey → `WORM_ACCESS_KEY_ID` / `WORM_SECRET_ACCESS_KEY` and
   `BACKUP_ACCESS_KEY_ID` / `BACKUP_SECRET_ACCESS_KEY`. Neither key can delete files; COMPLIANCE retention stops even the
   master key until it expires.

### 2. Google Workspace OIDC client (A-03)

Google Cloud Console of the Workspace organization → APIs & Services → OAuth consent screen: type **Internal**. Credentials →
Create credentials → OAuth client ID → **Web application**; authorized redirect URI
`https://<STAGING_HOST>/api/v1/auth/callback`. Client ID → `OIDC_CLIENT_ID` (variable), client secret → `OIDC_CLIENT_SECRET`.

### 3. DNS and VPS

**Before anything else (E-B03-12):** the `sistema` VPS runs `sistema-contable` (test data only), whose Caddy holds 80/443.
Back it up and remove it, as root on that VPS:

```bash
dir=$(docker inspect sistema-contable-app-1 --format '{{ index .Config.Labels "com.docker.compose.project.working_dir" }}')
mkdir -p /root/respaldo-sistema-contable
docker exec sistema-contable-db-1 sh -c 'pg_dumpall -U "$POSTGRES_USER"' | gzip > /root/respaldo-sistema-contable/db.sql.gz
tar czf /root/respaldo-sistema-contable/proyecto.tgz -C "$dir" .
ls -lh /root/respaldo-sistema-contable && zcat /root/respaldo-sistema-contable/db.sql.gz | head -5   # both non-empty
cd "$dir" && docker compose down -v --rmi local    # irreversible: stops and deletes containers, volumes and local images
ss -tlnp | grep -E ':(443|80) ' || echo "80 and 443 are free"
```


1. DNS: `A` record `<STAGING_HOST>` → the VPS's IP.
2. VPS (`sistema`: Ubuntu 26.04 LTS, 2 vCPU, 7.7 GB; Docker already installed): a `deploy` user with sudo and the workflow's public SSH key; Docker Engine and the
   compose plugin (`docs.docker.com/engine/install/ubuntu`), `deploy` in the `docker` group; firewall
   `ufw default deny incoming && ufw allow 22/tcp && ufw allow 443 && ufw enable`.
3. Copy `deploy/staging/bootstrap.sh` to the server and run it once: it creates `/opt/rochell-staging` and the digest key pair
   and prints the public key.
4. Backup certificate: on a machine that is **not** the server, `openssl req -x509 -newkey rsa:3072 -nodes -keyout
   backup-key.pem -out backup-cert.pem -days 825 -subj "/CN=rochell-staging-backup"`; copy only `backup-cert.pem` to
   `/opt/rochell-staging/secrets/`; keep `backup-key.pem` offline. Add the cron line in `backup.sh`.

### 4. GitHub Environment `staging` (Settings → Environments)

Secrets: `STAGING_SSH_KEY` (private key of the workflow), `STAGING_SSH_KNOWN_HOSTS` (`ssh-keyscan <host>`), `STAGING_SSH_USER`,
`STAGING_SSH_HOST`, `DEPLOY_DB_PASSWORD`, `APP_DB_PASSWORD`, `SEALER_DB_PASSWORD` (`openssl rand -hex 32` each: `.env` is
read by the shell, so no spaces, quotes or `$`), `OIDC_CLIENT_SECRET`,
`WORM_ACCESS_KEY_ID`, `WORM_SECRET_ACCESS_KEY`, `BACKUP_ACCESS_KEY_ID`, `BACKUP_SECRET_ACCESS_KEY`.
Variables: `STAGING_HOST` (`staging.industriasrochell.com.do`), `WORKSPACE_DOMAIN`, `OIDC_CLIENT_ID`, `WORM_BUCKET`, `WORM_REGION`,
`WORM_ENDPOINT`, `BACKUP_BUCKET` (same region and endpoint as the WORM bucket).
Deployment branch rule: `main` only; optionally a required reviewer (Alexander).

## Deploying

Actions → deploy-staging → Run workflow (branch `main`). The job checks CI, builds and pushes the image, uploads the compose files
and `.env` (mode 600), and runs `deploy.sh`. The VPS pulls with the job's short-lived token and logs out afterwards.

## Synthetic data (first deploy, E-B03-7)

On the server, in `/opt/rochell-staging` (`set -a; . ./.env; set +a` first):

```bash
docker compose run --rm migrate create-company 101999999 "Empresa de Ensayo, S.R.L."   # fictitious RNC
docker compose run --rm migrate create-user ana@<domain> <google-subject> <new-uuid>
docker compose run --rm migrate grant-role ana@<domain> CONTROLLER 101999999
docker compose run --rm migrate create-plant 101999999 PLANTA1 AREA1
docker compose run --rm migrate create-location 101999999 PLANTA1 RECEPCION
docker compose run --rm migrate import-accounts 101999999 /path/accounts.csv    # mount the CSV or pipe it in
docker compose run --rm migrate open-periods 101999999 2026
```

Posting rules, account maps, policies and fiscal rules (TEST sources) are then approved through the UI by the synthetic users,
as in the VS#1 walkthrough. No real supplier, invoice, account or bank data (E-VS1-2, E-VS2-10).

## Closing B-03 (E-B03-9)

1. The 00:15 digest of a day with activity is in `industriasrochell-worm-staging` (object with COMPLIANCE retention).
2. `verify-hash-chain` (UI: Auditoría) is valid against WORM.
3. PF-01 repeated against the staging database; result recorded in `docs/acceptance/vs1.md`.

## Rehearsal

The stack was rehearsed locally with the built image: migrations, `init-environment TEST`, logins, `create-company`, API behind
Caddy (local certificate for `localhost`; the OIDC redirect came out `https://…/api/v1/auth/callback`), digest service started
against RustFS standing in for B2, and the backup encryption round trip (`openssl cms` encrypt/decrypt, identical dump).
