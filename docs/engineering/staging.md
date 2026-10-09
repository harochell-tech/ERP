# Staging (B-03)

Approved errata E-B03-1…9. Staging runs the same image as production on the Hostinger `sistema` VPS (ADR-026, E-B03-11; never the portal VPS, which holds
real production data). Until the parallel run it held synthetic data only (E-VS1-2, E-VS2-10); since E-PAR-1…6 it is the
parallel-run environment with real data (see "Parallel run" below). B-03 closes when the checklist at the end is done (E-B03-9).

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
| Deploy | `.github/workflows/deploy-staging.yml` → `deploy/staging/deploy.sh` | Manual, `main` only, refuses a commit without a green `build-test`; migrate → `init-environment PRODUCTION` (Patch 1.1, E-PAR-3) → logins → restart → HTTPS smoke check |
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

## Parallel run (E-PAR-1…6)

Staging is the parallel-run environment: Rochell runs next to ADM Cloud with real data and is reconciled at each month's close.
The database is PRODUCTION (`deploy.sh` writes it once; the CLI refuses to change it), so there are no synthetic users and no
"Actuar como", and activating a fiscal rule needs an official PRODUCTION source (P-7). `Rochell__EnvironmentBadge: PARALELO`
(`compose.yaml`) is served at `GET /api/v1/environment` and shown in the top bar. `seed*.sh` are for TEST databases only.

Reset (E-PAR-2, done once, only with the owner's explicit confirmation at the time). Done on 2026-10-01: backup
`rochell-2026-10-01T133402Z.dump.cms` restored on the owner's computer with the same row counts as staging; company
`BLOCK ROCHELL SRL` (the legal name in the DGII registry), plant `MATILLA` (area `MATILLA`, "Planta Matilla") with RECEPCION, CURADO
and TRANSITO, the 2026 periods, the registry of 2026-09-19 and the owner as SUPERADMIN until 2026-12-30.

1. `backup.sh`; check the new object in B2 is CMS-encrypted (it starts with a DER `SEQUENCE`, not the `PGDMP` magic).
2. Restore test (E-PAR-4, criterion E1) on the owner's computer, where the offline key lives: download the object from the B2 web
   console (the key on the VPS can only write to the backup bucket), `openssl cms -decrypt -inform DER -binary -inkey
   backup-key.pem -in <file> -out rochell.dump`, `pg_restore` into an empty PostgreSQL 17, count rows.
3. Review SSH access (`~/.ssh/authorized_keys` fingerprints on the VPS).
4. Note the real people's `iam.user` rows (e-mail, OIDC subject) to recreate them with `create-user`.
5. Stop `api`; drop and recreate the `rochell` database (owner `rochell_deploy`); run the deploy workflow (migrations,
   PRODUCTION, logins).
6. `create-company 131925332 "<legal name>"`, `create-user` for the owner, `grant-role <owner> SUPERADMIN 131925332` (90 days,
   E-ADM-2-2), `create-plant 131925332 MATILLA <AREA> "<name>"` and its locations (RECEPCION, CURADO, TRANSITO), `open-periods`,
   `import-rnc-registry`.
7. Sign in; Configuración › Centro de configuración shows the 19 setup steps pending.

## Outgoing mail (MAIL-03, E-MAIL-01-3 / E-MAIL-01-4)

The compose file runs `pdf` (Gotenberg with Chromium, internal network only) and passes the mail settings to the API. Mail stays
**Off** until `MAIL_MODE` is set as a **variable of the GitHub Environment `staging`** (Settings → Environments → staging →
Variables) — the deploy rewrites `/opt/rochell-staging/.env` from that environment every time, so a value typed on the server is
lost at the next deploy:

```
MAIL_MODE=Redirect
MAIL_FROM=industrias@rochell.com.do
MAIL_REDIRECT_TO=industrias@rochell.com.do
MAIL_ARCHIVE_BCC=industrias@rochell.com.do
```

`MAIL_SMTP_HOST` (default `smtp-relay.gmail.com`) and `MAIL_EHLO_NAME` (default `staging.industriasrochell.com.do`) rarely change.
Then run `deploy-staging`. With **Redirect** every message goes only to `MAIL_REDIRECT_TO`, the intended recipients named in
the subject and the body; **Live** (customers receive mail) is set only on the owner's explicit decision (E-MAIL-8).

One-time, by the Workspace administrator (no password is stored on the server):

1. admin.google.com → Apps → Google Workspace → Gmail → Routing → **SMTP relay service** → Configure.
2. Allowed senders: *Only addresses in my domains*. Authentication: *Only accept mail from the specified IP addresses* →
   `2.25.237.35`. Leave *Require SMTP Authentication* unticked; tick *Require TLS encryption*. Save (it can take up to an hour).
3. `industrias@rochell.com.do` must exist as a mailbox or a group that receives mail.

Check: send a quote from the UI (Ventas › Cotización › Enviar por correo); its history shows «Enviado — Redirigido a …» and the
message arrives at the internal mailbox with the PDF. A failure shows the relay's answer in the history and in
`docker compose logs api`; `core.mail_attempt` keeps every attempt.


## e-CF gateway with Alanube (VS4-02…04, E-VS4-11, E-VS4-04-7)

The gateway is **OFF** until `ECF_MODE` and `ECF_BASE_URL` are set as **variables of the GitHub Environment `staging`**:

```
ECF_MODE=SANDBOX
ECF_BASE_URL=https://sandbox.alanube.co/dom/v1/
```

The token and the webhook secret never go to GitHub, the `.env` or the chat: they are files on the server, read by the API at
start (`secrets/ecf`, mounted read-only at `/run/secrets/ecf`). On the server, once (and again to rotate the token):

```
sudo mkdir -p /opt/rochell-staging/secrets/ecf
sudo nano /opt/rochell-staging/secrets/ecf/alanube-token
openssl rand -hex 32 | sudo tee /opt/rochell-staging/secrets/ecf/webhook-secret
sudo chown -R 1654:1654 /opt/rochell-staging/secrets/ecf
sudo chmod 500 /opt/rochell-staging/secrets/ecf
sudo chmod 400 /opt/rochell-staging/secrets/ecf/*
```

(`nano`: paste the token from Alanube's panel, Ctrl+O, Enter, Ctrl+X.) Then run `deploy-staging`. With SANDBOX the API refuses
to start without the token file.

**Webhook** (optional — without it the queue is still polled every 15 s): in Alanube's panel, notifications → URL
`https://staging.industriasrochell.com.do/api/v1/ecf/webhook`, custom header `X-Rochell-Ecf-Secret` with the value printed by the
`openssl` line above. A call without that header is answered 401 and logged; a valid one only brings the status queries forward.

**Before switching on**: Configuración › Empresa → *Datos del emisor de e-CF* (the address is required), and Fiscal › Rangos e-NCF →
a range per type approved by the Controller (at the cut-over: from the first number the previous provider did not use).

**Contract test** (E-VS4-04-8, sandbox only): write a settings file on the server (no secret in it) and run

```
mkdir -p /opt/rochell-staging/ct && chmod 777 /opt/rochell-staging/ct
img=$(docker inspect -f '{{.Config.Image}}' rochell-staging-api-1)
docker run --rm --network rochell-staging_edge -v /opt/rochell-staging/ct:/ct -v /opt/rochell-staging/secrets/ecf:/run/secrets/ecf:ro \
  --entrypoint dotnet "$img" /app/migrate/rochell-migrate.dll ecf-contract-test /ct/settings.json /ct/report.jsonl
```

(It needs the internet: the `migrate` service sits on the internal network only, so the tool runs on `edge` without the database; the
folder must be writable by the container's user 1654.)

with `settings.json`:

```json
{ "baseUrl": "https://sandbox.alanube.co/dom/v1/", "tokenFile": "/run/secrets/ecf/alanube-token",
  "senderRnc": "131925332", "senderName": "BLOCK ROCHELL SRL", "senderAddress": "…",
  "buyerRnc": "…", "buyerName": "…", "first31": 1, "first34": 1, "sequenceDueDate": "2027-12-31",
  "unitPrice": "100.00", "itbisRate": "0.18", "burst": 50 }
```

`first31` / `first34` are the first unused numbers of the **sandbox** ranges (the test uses 5 + `burst` numbers of 31 and one of
34). The report has one JSON line per request with Alanube's raw answer, never the token; its findings go to
`docs/acceptance/vs4-contract-test.md`.

## Machines' portal (MFG-2, E-MFG2-1)

Core reads `https://industriasrochell.com.do/data/exportar.php` every 15 minutes once `PORTAL_URL=https://industriasrochell.com.do/`
is a variable of the GitHub Environment `staging`. Core's key (`core_token` in the portal's `config/portal-config.php`) goes in a
file on the server, never in the `.env`:

```
sudo mkdir -p /opt/rochell-staging/secrets/portal
sudo nano /opt/rochell-staging/secrets/portal/core-token
sudo chown -R 1654:1654 /opt/rochell-staging/secrets/portal
sudo chmod 500 /opt/rochell-staging/secrets/portal
sudo chmod 400 /opt/rochell-staging/secrets/portal/core-token
```

Then `deploy-staging`. Producción › Portal shows the last good read, the last failure and what could not be imported.

## Drivers' page (ENT-1, E-ENT-1/5, E-ENT1-01-2)

Off until both pieces exist; the API refuses to start with the link key but no evidence bucket.

1. In Backblaze (B2 Cloud Storage), a **private** bucket without Object Lock, e.g. `rochell-staging-evidencias`, and an Application
   Key limited to that bucket (read and write). Nothing is sent by chat.
2. On the server:

```
sudo mkdir -p /opt/rochell-staging/secrets/deliveries
openssl rand -base64 32 | sudo tee /opt/rochell-staging/secrets/deliveries/link-key >/dev/null
sudo nano /opt/rochell-staging/secrets/deliveries/evidence-key-id
sudo nano /opt/rochell-staging/secrets/deliveries/evidence-key
sudo chown -R 1654:1654 /opt/rochell-staging/secrets/deliveries
sudo chmod 500 /opt/rochell-staging/secrets/deliveries
sudo chmod 400 /opt/rochell-staging/secrets/deliveries/*
```

3. The variable `EVIDENCE_BUCKET` of the GitHub Environment `staging`, then `deploy-staging`.

Changing `link-key` invalidates every printed QR (Dispatch reopens the links of the deliveries in transit). The page lives at
`/entrega/?c=…&d=…&g=…&k=…`; its API under `/api/v1/public/deliveries`.
