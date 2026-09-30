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
| Daily fiscal expiry | `Rochell__FiscalExpiry__Enabled: "true"` in `compose.yaml` (E-FIS1-04-7) | Configuration reaches the container through environment variables only: the API starts with content root `/app`, so the image's `appsettings.*.json` (in `/app/api`) are not read |
| Deploy | `.github/workflows/deploy-staging.yml` → `deploy/staging/deploy.sh` | Manual, `main` only, refuses a commit without a green `build-test`; migrate → `init-environment TEST` (Patch 1.1) → logins → restart → HTTPS smoke check |
| Backup | `deploy/staging/backup.sh` (cron 01:30) | `pg_dump -Fc` → `openssl cms` (AES-256, to `secrets/backup-cert.pem`) → B2 bucket with a 7-day lifecycle rule (E-B03-8, E-B03-10) |

## One-time setup

### 1. Backblaze B2 (E-B03-10; Alexander creates the account; nothing is sent by chat)

Staging host: `staging.industriasrochell.com.do`. Bucket names in B2 are global, hence the company prefix.

1. Account at backblaze.com (B2 Cloud Storage), with 2FA.
2. Bucket `industriasrochell-worm-staging`: **Private**, **Object Lock: Enable** (no default retention: the store sets
   COMPLIANCE, 7 days, on every object). The bucket page shows the S3 endpoint (`s3.<region>.backblazeb2.com`) and the
   region (`<region>`, e.g. `us-east-005`) → `WORM_ENDPOINT` = `https://s3.<region>.backblazeb2.com`, `WORM_REGION` = `<region>`.
3. Bucket `industriasrochell-backup-staging` in the **same region**: Private, custom lifecycle rule on prefix `staging/`:
   hide 7 days after upload, delete 1 day after hiding (each backup has its own name, so a "prior versions" rule would never
   remove it).
4. Application keys with the B2 CLI (`python3 -m venv ~/b2cli && ~/b2cli/bin/pip install b2`, then `b2 account authorize`
   with the master key), restricted per bucket — the web console only offers read-and-write keys, which include `deleteFiles`:

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

## One-time synthetic setup (`seed.sh`)

After the first deploy, on the VPS: `/opt/rochell-staging/seed.sh alex@rochell.com.do`. It grants the tester PROBADOR, creates
one test identity per slice role (`comprador@staging.invalid`, `aprobador@…`, `almacen@…`, `cxp@…`, `controller@…`,
`politicas@…`, `analista@…`, `fiscal@…`, `tesorero@…`, `auditor@…`, `cierre@…`), and loads the synthetic chart of accounts and
DRAFT account maps in `deploy/staging/seed/` (approved in the UI, "Mapas de cuentas"). Run it once: a second run stops at the
first identity that already exists.

## Test identities (E-B03-14)

One tester exercises every role through synthetic users (see [identity.md](identity.md#test-identities-e-b03-14-test-databases-only)):

```bash
docker compose run --rm migrate grant-role alex@rochell.com.do PROBADOR 131925332
docker compose run --rm migrate create-synthetic-user comprador@staging.invalid
docker compose run --rm migrate grant-role comprador@staging.invalid COMPRADOR 131925332
# likewise: aprobador (APROBADOR_COMPRAS), almacen (ALMACENISTA), cxp (CUENTAS_POR_PAGAR), tesorero (TESORERO), …
```

Then in the UI header: "Actuar como…" → pick the identity; "Volver a mi usuario" returns.

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

## PF-01 on staging (E-B03-16)

PF-01 creates 10,000 goods receipts, so it never runs against the `rochell` database. Actions → `pf01-staging` → Run workflow
(branch `main`; inputs `receipts` 10000 and `workers` 8 are the PF-01 values, smaller numbers are only a smoke run):

| Step | What happens |
| --- | --- |
| Guards | `main` only, GitHub Environment `staging`, refuses a commit without a green `build-test`; concurrency group `deploy-staging`, so it never overlaps a deploy |
| Harness image | `Dockerfile.loadharness` (the `tests/Rochell.LoadHarness` console app with the test-only migrations) is built on the runner at the same commit and streamed over SSH (`docker save \| gzip \| ssh … docker load`). It is never pushed to ghcr and never becomes the production image; the VPS needs no registry token |
| Database | `deploy/staging/pf01.sh` (uploaded to `/opt/rochell-staging/`) creates `rochell_pf01` in the existing `postgres` container as `rochell_deploy`. Every `psql` call targets the maintenance database `postgres`; the harness only gets a connection string for `rochell_pf01`, and refuses a database that is not empty |
| Logins | The harness creates its own cluster-wide logins `rochell_app_pf01` (∈ rochell_app) and `rochell_sealer_pf01` (∈ rochell_sealer) with random passwords generated for that run (`openssl rand`); it refuses to reuse an existing login. `rochell_app_login` / `rochell_sealer_login` and their passwords are untouched |
| Run | Harness container (`--init`, `--rm`) on `rochell-staging_internal` (no Internet), secrets only as inherited environment variables; 8 workers, sealer running, then the 8 reconciliations |
| Cleanup | `DROP DATABASE rochell_pf01 WITH (FORCE)` and `DROP ROLE` of both logins in a shell `trap` on exit (also on failure or signal), and again from the workflow with `if: always()` (`pf01.sh --cleanup`, which also covers a cancelled run); it then checks that neither exists. The harness image is removed from the VPS |
| Result | `pf01.md` in the job summary; `pf01.json`, `pf01.md`, `harness.log` as the artifact `pf01-staging-report`. The job fails when PF-01 fails (p95 of PostGoodsReceipt ≥ 500 ms, reconciliations ≥ 30 s, or any failed receipt) |

The staging API keeps running during the measurement (same hardware, idle load). Avoid 01:30 (backup cron). Record the result
in `docs/acceptance/vs1.md` (PF-01 table). Locally the same script can be rehearsed with `ROCHELL_STAGING_DIR=<dir>` pointing at
a copy of `compose.yaml` and a dummy `.env`.

## Closing B-03 (E-B03-9)

1. The 00:15 digest of a day with activity is in `industriasrochell-worm-staging` (object with COMPLIANCE retention).
2. `verify-hash-chain` (UI: Auditoría) is valid against WORM.
3. PF-01 repeated on the staging server (workflow `pf01-staging`, temporary database, E-B03-16); result recorded in
   `docs/acceptance/vs1.md`.

## Rehearsal

The stack was rehearsed locally with the built image: migrations, `init-environment TEST`, logins, `create-company`, API behind
Caddy (local certificate for `localhost`; the OIDC redirect came out `https://…/api/v1/auth/callback`), digest service started
against RustFS standing in for B2, and the backup encryption round trip (`openssl cms` encrypt/decrypt, identical dump).

## VS#3 on staging (E-VS3-10-11)

After deploying VS#3 (the deploy uploads it), run `/opt/rochell-staging/seed-vs3.sh` on the VPS: test identities for Vendedor, Crédito, Despacho, Facturación
(two, for the credit note's four eyes) and Cobros, and the sales accounts with DRAFT maps (`seed/accounts-vs3.csv`,
`seed/account-map-vs3.csv`). Then, as the Controller in the UI: approve the maps and the sales posting rules, prepare CREDIT and
REVENUE_ACCOUNTING (approved by the Aprobador de políticas) and register the receipts' bank account (GL 1102). Synthetic data only.

## DGII RNC registry (E-RNC-2, E-RNC-8)

The deploy uploads `rnc-weekly.sh`. It downloads `DGII_RNC.zip` from the DGII and imports it with
`rochell-migrate import-rnc-registry` (the ZIP is mounted read-only into the `migrate` container). Schedule it with
`/etc/cron.d/rochell-rnc` (Mondays 05:00, the line is in the script). The DGII may answer HTTP 403 to automated downloads; the script
then stops and prints the instructions: download the ZIP in a browser, copy it to the VPS and run
`/opt/rochell-staging/rnc-weekly.sh /path/to/DGII_RNC.zip`. The registry is public data, so it is allowed on staging (E-RNC-8).

## MFG-1 on staging (E-MFG1-07-7)

After deploying MFG1-01…07 (the deploy uploads the script), run `/opt/rochell-staging/seed-mfg.sh` on the VPS: test identities for
Supervisor de producción, Gerente de planta and Calidad, and the production accounts with DRAFT maps (`seed/accounts-mfg.csv`,
`seed/account-map-mfg.csv`: WIP 1340, MATERIAL_PRICE_VARIANCE 5106, CONVERSION_ABSORPTION 5150, PRODUCTION_SCRAP 5160,
STANDARD_REVALUATION 5190; MATERIAL_USAGE_VARIANCE keeps 5102). Then, as the Controller in the UI: approve the maps and P-08, P-10,
P-12, P-13 and REVAL, and prepare the PRODUCTION policy (approved by the Aprobador de políticas). Synthetic data only.

## Plant names (UX1-01a, E-UX1-01-4)

Screens show plants as "Name (CODE)". On the VPS: `docker compose run --rm migrate set-plant-name 131925332 <CODE> "Planta Higüey"`
(new plants: `create-plant <rnc> <CODE> <AREA> "Name"`). Only the name of a plant row can change.
