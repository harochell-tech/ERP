# Banks and supplier payments — schema (VS2-01)

VS2-01 builds the schema of Vertical Slice #2 (`docs/architecture/vs2/frozen-baseline-vs2.md` §2, §7) in migration
`0023__payments_and_banks.sql`. It has no commands: the fixture `TestTreasurySql` (tests only) writes rows as the application role
until VS2-02…05 bring the real commands. Approved errata: E-VS2-1…10, E-VS2-01-1…14.

## Tables

| Table | What it holds | Written by (later PRs) |
| --- | --- | --- |
| `fin.bank_account` | The company's own accounts (DOP). ACTIVE → CLOSED. One **control** GL account each, not shared and never in the role map (E-VS2-01-1) | Controller, `bank_account:manage` (VS2-02) |
| `md.party_bank_account` | Supplier accounts, versioned. REVIEW → VERIFIED / REJECTED; VERIFIED → SUPERSEDED | Tesorero requests, Controller verifies or rejects (VS2-02) |
| `fin.payment` | Disbursements (`fin.payment_status`). Transfer only, to an account of the same supplier (E-VS2-01-14) | Tesorero prepares/voids, Controller releases/reverses (VS2-03/04) |
| `fin.ap_application` | Payment → AP document amounts. Append-only; unapplying is a reversal row that mirrors one application | Release and reversal (VS2-03/04) |
| `fin.bank_statement`, `fin.bank_statement_line` | Imported statements (append-only) and their lines. UNMATCHED → MATCHED / CHARGE_RECOGNIZED; MATCHED → UNMATCHED | Import, match, charges (VS2-05) |

Formats (E-VS2-01-3): `bank_code` is stored upper case, 2–20 characters; `account_number` is digits only, 5–30 (commands strip
spaces and hyphens before inserting).

## Database guarantees

- **Supplier accounts.** Verifier ≠ requester (PAY-10, CHECK); evidence of at least 20 characters and `payable_from = verified_at +
  72 h` (E-VS2-01-4, E-VS2-8). Verification data stays on SUPERSEDED rows and cannot be rewritten (E-VS2-01-8). At most one REVIEW
  and one VERIFIED version per supplier; a version becomes SUPERSEDED only when a newer one is verified in the same transaction
  (E-VS2-01-9). A rejection records who, when and a reason; rejecter ≠ requester (E-VS2-01-10). Account data is immutable.
- **Payments.** Preparer ≠ releaser (PAY-04, CHECK). RELEASED / CLEARED / REVERSED carry `released_by` and `posting_event_id`.
  Transitions: PREPARED → RELEASED | VOIDED; RELEASED → CLEARED | REVERSED; CLEARED → REVERSED | RELEASED (unmatch, E-VS2-01-11).
  Only a PREPARED payment can be edited. The UNIQUE on (bank account, direction, reference, amount, value date) is the baseline's.
- **Applications.** The AP document belongs to the payment's supplier; a reversal has the same payment, document and amount as the
  original and exists at most once.
- **Statement lines (IDM-04, E-VS2-01-12).** `UNIQUE NULLS NOT DISTINCT (company, bank account, direction, reference, amount,
  value date, occurrence)`, where `occurrence` numbers identical lines within one file: an overlapping import adds nothing, two
  identical fees on one day are both kept. The value date lies within the statement's period; only a DEBIT line of the payment's
  bank account can be MATCHED.
- **Ledger.** Account roles `BANK` (control) and `BANK_CHARGES`. `fin.gl_entry` accepts subledger `BANK`; role BANK ⇔ subledger
  BANK, and a BANK line references a bank account and posts to that account's GL account (E-VS2-01-2, E-VS2-01-13). The BA
  dimension lives only on BANK lines; no new `gl_entry` column.
- **ADR-027.** Every status set on insert (bank account, supplier account, payment) or changed (all four) needs its
  `core.state_history` row in the same transaction (deferred check, `fin.require_state_history`). Statement lines are created by
  the import event and need history for status changes only.
- **Close component BANK-REC** (E-VS2-01-6) is accepted by every component CHECK, opened for existing periods by the migration and
  for new ones by `rochell-migrate open-periods`. Closing it arrives with BANK-GL and PAY-APPL in VS2-06
  (`Components.IsKnown` still rejects it).

## Permissions (VS#2 §7, E-VS2-01-5)

Role **TESORERO**. 13 new permissions (57 in total) and 8 SoD pairs (24 in total):

| Role | Permissions |
| --- | --- |
| TESORERO | party_bank_account:request, payment:prepare, payment:void, bank_statement:import, bank_line:match, payment:read, bank:read |
| CONTROLLER | bank_account:manage, party_bank_account:verify, payment:release, payment:reverse, bank_line:unmatch, bank_charge:recognize, payment:read, bank:read |
| CUENTAS_POR_PAGAR, AUDITOR | payment:read, bank:read |

SoD: bank_account:manage / payment:prepare; party_bank_account:request / verify; payment:prepare, payment:void and payment:reverse
against payment:release or prepare as in §7; payment:release / supplier:create, supplier_invoice:register, supplier_invoice:post.

Schema tests: `tests/Rochell.Finance.Tests/BankSchemaTests.cs` (tagged `AcceptanceVs2` for PAY-04 / PAY-10 until VS2-09 extends the
traceability test to VS#2).
