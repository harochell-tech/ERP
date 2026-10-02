# Configuration load (CFG)

Approved errata E-CFG-1…6. The owner asked the assistant to load the fiscal rules itself. It does so without a user's session and
without writing to the database by hand: a service identity runs the application's own commands from the deployment CLI.

## CFG-01 — fiscal sources and rules (migration 0072)

- **Identity.** `iam.user` `…d003` «Carga de configuración» (SERVICE) with the role `CARGA_CONFIGURACION`:
  `fiscal_rule_source:register` and `fiscal_rule:configure` — it registers sources, configures versions, links sources and runs
  tests; it holds no `fiscal_rule:activate`. Service roles are held only by their own service identity, which holds nothing else
  (`iam.role_assignment_service_guard`). Assigned in every company (migration; `create-company` for new ones). Hidden from the role
  list of the screens, like PROCESO_DIARIO.
- **Pack.** `deploy/fiscal/rules-2026-10.json`: sources (title, version, dates, URL, file name) and rules (code, kind, definition,
  start date, sources, regression cases) from `docs/fiscal/a02-sources-dossier.md`. The official documents are in
  `docs/fiscal/fuentes/` (downloaded by the owner in a browser); a source's SHA-256 is computed from its file.
- **Loader.** `Rochell.Tax.Packs.FiscalRulePackLoader` runs `RegisterFiscalSource`, `ConfigureFiscalRuleVersion`,
  `LinkFiscalSource` and `RunFiscalRuleTests` through the command pipeline on the service session. Safe to repeat: a source with
  the same SHA-256 and environment is reused; a version already prepared or active with the same definition (compared as JSON, not
  as text) and start date is reused and completed; a rule configured differently is reported `DIFFERENT` and left alone; a source
  whose file is missing is `MISSING_FILE` and its rules `SKIPPED`. Sources are PRODUCTION in a PRODUCTION database, TEST otherwise.
- **CLI.** `rochell-migrate load-fiscal-rules <company-rnc> <pack.json> <documents-folder>` prints one line per source and rule
  and ends with exit code 3 when something was missing, skipped or different. It never activates: each READY rule is activated by
  a person in Configuración › Reglas fiscales.

On staging:

```bash
scp deploy/fiscal/rules-2026-10.json docs/fiscal/fuentes/*.pdf rochell-staging:/opt/rochell-staging/fiscal/
ssh rochell-staging 'cd /opt/rochell-staging && docker compose run --rm -v ./fiscal:/fiscal:ro migrate load-fiscal-rules <rnc> /fiscal/rules-2026-10.json /fiscal'
```

Pack 2026-10: Código Tributario Título III → `ITBIS_COMPRAS` (18 %, recoverable) and `ITBIS_VENTAS` (18 %, output); Norma General
07-2018 → `CLASIF_606` (raw materials as type 09). Not in the pack: purchase withholdings (the system does not yet tell services
from goods, G-1), the consumer identification amount (accountant, X-1) and the freight exemption (SRV-1).

Tests: `Rochell.Tax.Tests.FiscalRulePackTests` — the repository's pack and documents load to READY with their real fingerprints,
twice without duplicates; a person's matching version is completed and a different one left alone; a missing document; the service
identity cannot activate; the role belongs only to its identity.
