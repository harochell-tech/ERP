# Posting Engine (PR-05)

**Setup per company (deployment role):** `import-accounts` (CSV `code,name,is_control`) → `import-account-map`
(CSV `account_role,item_category,account_code,effective_from`, creates DRAFT) → `open-periods <rnc> <year>`.
The Controller approves mappings (`ApproveAccountRoleMap`) and rule versions (`ApprovePostingRuleVersion`), with step-up.

**In a command handler (Patch 1 §5.2):**

1. `var plan = await engine.PrepareAsync(context, request)` — before writing anything: active rule version (by business date),
   posting date (period + close component, late entry), account per line (role map by posting date, category-specific first),
   rounding to 2 decimals, balance (a difference within the POSTING policy tolerance goes to ROUNDING_DIFFERENCE, line R-08). Any gap throws `POSTING_PREREQUISITE_MISSING` / `POSTING_UNBALANCED` → full rollback.
2. Append the domain event and write the documents.
3. `await engine.WriteAsync(context, plan, eventId)` — journal, lines (row hashes) and `gl_period_balance`.

**Reversal:** `engine.ReverseAsync(...)` writes the exact inverse (same accounts, dimensions and rule lines; debit ↔ credit),
once per journal. Lines tied to inventory value entries need the new inverse value entries (PR-07+).

**Database guarantees:** balanced journal with ≥ 2 lines at COMMIT; lines only in the journal's own transaction; control accounts
only with a subledger and matching role; 2 decimals, DOP; journals and lines append-only; configuration immutable once approved
(except closing its range once); no overlapping ACTIVE versions; row-level security by company.
