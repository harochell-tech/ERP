# Staging (B-03)

Approved errata E-B03-1…9. Staging runs the same image as production on the Hostinger VPS (ADR-026), with synthetic data only
(E-VS1-2, E-VS2-10). B-03 closes when the checklist at the end is done (E-B03-9).

## Pieces

| Piece | Where | Notes |
| --- | --- | --- |
| Image | `Dockerfile` → `ghcr.io/harochell-tech/rochell-core:<sha>` | API + web static export (`/app/web`) + deployment CLI (`/app/migrate/rochell-migrate.dll`); non-root (uid 1654); no secrets |
| Stack | `deploy/staging/compose.yaml` in `/opt/rochell-staging` | `postgres` (17.6, internal network only), `api`, `caddy` (only 443/tcp+udp published), `migrate` (tools profile) |
| TLS | `deploy/staging/Caddyfile` | Let's Encrypt via TLS-ALPN on 443 (port 80 stays closed, E-B03-2); HSTS |
| Proxy trust | `Rochell:ReverseProxy:TrustedNetworks` = `172.30.0.0/24` (the `edge` network) | The API honours `X-Forwarded-For/Proto` only from Caddy, so the OIDC redirect and cookies are `https` |
| Logins | `deploy/staging/init-roles.sql` | `rochell_app_login` ∈ rochell_app, `rochell_sealer_login` ∈ rochell_sealer; passwords reset on each deploy |
| WORM | AWS S3 Object Lock, COMPLIANCE, 7 days (E-B03-3) | `Rochell:Audit:S3:*`; see [hash-chain.md](hash-chain.md) |
| Digest keys | `/opt/rochell-staging/secrets/digest-{signing,public}.pem` (`bootstrap.sh`) | Private key generated on the server, `0400`, uid 1654; `Rochell:Digest:SigningKeyPemFile`, `Rochell:Audit:DigestPublicKeyPemFile` (E-B03-6) |
| Deploy | `.github/workflows/deploy-staging.yml` → `deploy/staging/deploy.sh` | Manual, `main` only, refuses a commit without a green `build-test`; migrate → `init-environment TEST` (Patch 1.1) → logins → restart → HTTPS smoke check |
| Backup | `deploy/staging/backup.sh` (cron 01:30) | `pg_dump -Fc` → `openssl cms` (AES-256, to `secrets/backup-cert.pem`) → S3 bucket with a 7-day expiry (E-B03-8) |

## One-time setup

### 1. AWS (Alexander creates the account; nothing is sent by chat)

1. Bucket `rochell-worm-staging` in `us-east-1`, **Object Lock enabled at creation** (versioning turns on with it). No default
   retention is needed: the store sets COMPLIANCE, 7 days, on every object.
2. Bucket `rochell-backup-staging`, Block Public Access on, lifecycle rule: expire current and noncurrent versions after 7 days.
3. IAM user `rochell-staging-sealer`, access key → `WORM_ACCESS_KEY_ID` / `WORM_SECRET_ACCESS_KEY`:

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       { "Effect": "Allow", "Action": ["s3:GetBucketObjectLockConfiguration", "s3:ListBucketVersions"],
         "Resource": "arn:aws:s3:::rochell-worm-staging" },
       { "Effect": "Allow", "Action": ["s3:PutObject", "s3:PutObjectRetention", "s3:GetObject", "s3:GetObjectVersion", "s3:GetObjectRetention"],
         "Resource": "arn:aws:s3:::rochell-worm-staging/*" }
     ]
   }
   ```

4. IAM user `rochell-staging-backup` → `BACKUP_ACCESS_KEY_ID` / `BACKUP_SECRET_ACCESS_KEY`, with only `s3:PutObject` on
   `arn:aws:s3:::rochell-backup-staging/staging/*`.

### 2. Google Workspace OIDC client (A-03)

Google Cloud Console of the Workspace organization → APIs & Services → OAuth consent screen: type **Internal**. Credentials →
Create credentials → OAuth client ID → **Web application**; authorized redirect URI
`https://<STAGING_HOST>/api/v1/auth/callback`. Client ID → `OIDC_CLIENT_ID` (variable), client secret → `OIDC_CLIENT_SECRET`.

### 3. DNS and VPS

1. DNS: `A` record `<STAGING_HOST>` → the VPS's IP.
2. VPS (Ubuntu 22.04/24.04, ≥ 2 vCPU, 4 GB): a `deploy` user with sudo and the workflow's public SSH key; Docker Engine and the
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
Variables: `STAGING_HOST`, `WORKSPACE_DOMAIN`, `OIDC_CLIENT_ID`, `WORM_BUCKET`, `WORM_REGION`, `BACKUP_BUCKET`.
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

1. The 00:15 digest of a day with activity is in `rochell-worm-staging` (object with COMPLIANCE retention).
2. `verify-hash-chain` (UI: Auditoría) is valid against WORM.
3. PF-01 repeated against the staging database; result recorded in `docs/acceptance/vs1.md`.

## Rehearsal

The stack was rehearsed locally with the built image: migrations, `init-environment TEST`, logins, `create-company`, API behind
Caddy (local certificate for `localhost`; the OIDC redirect came out `https://…/api/v1/auth/callback`), digest service started
against RustFS standing in for S3, and the backup encryption round trip (`openssl cms` encrypt/decrypt, identical dump).
