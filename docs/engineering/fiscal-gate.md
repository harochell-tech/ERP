# Fiscal gate and purchase Tax Engine (PR-12)

No rate, exemption or withholding lives in code. Each is a `tax.fiscal_rule_version` taken through the gate:

1. **Analista fiscal** — `RegisterFiscalSource` (official source, reference, SHA-256 of the document consulted),
   `ConfigureFiscalRuleVersion` (declarative definition, starts BLOCKED_PENDING_SOURCE), `LinkFiscalSource`,
   `RunFiscalRuleTests` (regression cases supplied by Fiscal; recorded with the environment and a result hash).
   With a source and a passing latest run the version is READY.
2. **Especialista fiscal** (≠ configurer, SoD, step-up) — `ActivateFiscalRuleVersion`. A successor closes its predecessor at its
   start date (or retires it).

The database enforces the gate: READY/ACTIVE without a source or a passing run of this environment is rejected, the activator can
never be the configurer, definitions are immutable, and two ACTIVE versions of a rule never overlap.

`TaxEngine.DetermineAsync` (used by the supplier invoice, PR-13) refuses to work while the gate is closed: no ACTIVE purchase ITBIS
rule for the date, or any rule pending activation already in force (SI-07). Each determination records its inputs, the versions
used and every tax line (`amount = round(base × rate, 2)`).

**Production (A-02):** until Fiscal registers official DGII sources and activates the rules, every purchase-invoice function stays
blocked by design. Test suites use sources marked TEST, which never appear in production migrations.
