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

## Commands (VS2-02)

Module `Rochell.Treasury` (E-VS2-02-1: may use Finance and MasterData). API: `POST /api/v1/companies/{companyId}/treasury/…` and
`/master-data/…` (E-VS2-02-8).

| Command | Permission (step-up) | What it does |
| --- | --- | --- |
| `treasury/register-bank-account` | `bank_account:manage` (yes) | Normalizes bank code and number, resolves the GL account by code (unmapped control account, not used by another bank account), creates the account ACTIVE (E-VS2-02-2) |
| `treasury/close-bank-account` | `bank_account:manage` (yes) | ACTIVE → CLOSED with a reason and the expected version; refused with PREPARED/RELEASED payments or UNMATCHED lines (E-VS2-02-3) |
| `master-data/request-party-bank-account` | `party_bank_account:request` (yes) | New version in REVIEW for an ACTIVE supplier; one REVIEW at a time (E-VS2-02-4) |
| `master-data/verify-party-bank-account` | `party_bank_account:verify` (yes) | REVIEW → VERIFIED by someone other than the requester, evidence ≥ 20 characters; `payable_from` = now + 72 h; supersedes the previous VERIFIED version in the same transaction |
| `master-data/reject-party-bank-account` | `party_bank_account:verify` (no) | REVIEW → REJECTED with a reason, by someone other than the requester (E-VS2-02-5) |

Requests and verifications of one supplier take an advisory lock per supplier, so "one REVIEW, one VERIFIED" never races.
`PartyBankAccounts.PayabilityAsync` is the "payable" rule (E-VS2-02-6): VERIFIED and now ≥ `payable_from`; payment release
(VS2-03) calls it under its locks. Events carry full account numbers (E-VS2-02-7).

Tests: `tests/Rochell.Treasury.Tests` (PAY-06, PAY-07 payability halves and PAY-10 command half tagged `AcceptanceVs2`).

## Payments (VS2-03)

| Command | Permission (step-up) | What it does |
| --- | --- | --- |
| `treasury/prepare-supplier-payment` | `payment:prepare` (no) | PREPARED payment to one ACTIVE supplier (transfer, DOP), number `PAG-000001…` (E-VS2-03-7), allocations = amount (E-VS2-03-1); VERIFIED supplier account (E-VS2-03-8); each application ≤ open amount now (PAY-03), 2 decimals, value date ≥ latest invoice; no journal, nothing reserved (E-VS2-6) |
| `treasury/update-prepared-payment` | `payment:prepare` (no) | Replaces bank accounts, value date, reference and all applications; version + 1, new allocation set (E-VS2-03-6) |
| `treasury/void-payment` | `payment:void` (no) | PREPARED → VOIDED with a reason |
| `treasury/release-supplier-payment` | `payment:release` (yes) | Releaser ≠ preparer; locks payment → AP documents (id order) → bank account → period × components; re-checks open amounts (PAY-05), payability now (PAY-06/07) and value date ≤ today; posts R-09 at the value date (late entry if BANK-REC or AP-REC is closed, E-VS2-03-3), writes `fin.ap_application`, lowers `open_amount` |

**R-09** (E-VS2-03-2): Dr AP_CONTROL per application (subledger AP = the AP document), Cr BANK for the payment (subledger BANK = the
bank account, posted to its own GL account). The Posting Engine now accepts the BANK subledger and rule versions that require
more than one open component (`also_requires_components`); the E-VS1-9 guard checks all of them.

**Database guarantees** (migration 0026): allocations only for the current version of a PREPARED payment and only to the supplier's
invoices; at COMMIT a PREPARED payment's allocations, and a RELEASED/CLEARED payment's live applications, add up to its amount;
RELEASED/CLEARED need an unreversed AUTO journal of the posting event (K-25); amounts have 2 decimals.

Tests: `PaymentTests` (PAY-01…07, PAY-09, update/void, dates and decimals, late entry, released state machine).

## Reversal (VS2-04)

`treasury/reverse-payment` (`payment:reverse`, step-up): RELEASED or CLEARED → REVERSED with a reason of at least 10 characters,
on today's business date (late entry if BANK-REC or AP-REC is closed). One reversal row per live application, `open_amount`
restored, and the exact reversal (Patch 1 P-4) of the live R-09 journal. A CLEARED payment's DEBIT statement line stays matched
(E-VS2-04-1); the bank's return line is matched to the reversal in VS2-05. Migration 0027: a REVERSED payment has no live
application and no live journal of its posting event. A PREPARED payment is voided, not reversed. After the reversal the invoice
is fully open again, so VS#1 can reverse it (PAY-08).

Tests: `PaymentReversalTests` (PAY-08, the CLEARED path with the fixture, guards, late entry); the CLEARED path through `MatchBankLine` is in `BankStatementTests` (VS2-05).

## Statements, matching and charges (VS2-05)

Migration `0028__bank_statements.sql`; approved errata E-VS2-05-1…9.

| Command | Permission (step-up) | What it does |
| --- | --- | --- |
| `treasury/import-bank-statement` | `bank_statement:import` (no) | Reads a CSV (base64, ≤ 5 MB) of an ACTIVE bank account with the latest format of its bank code; keeps the file; creates the statement and its new lines UNMATCHED (see below) |
| `treasury/match-bank-line` | `bank_line:match` (no) | A person confirms that an UNMATCHED **DEBIT** line is a RELEASED payment of the same account, same amount, line dated within [value date, value date + 10 days]. Line MATCHED, payment CLEARED; events BankLineMatched + PaymentCleared |
| `treasury/unmatch-bank-line` | `bank_line:unmatch` (yes) | MATCHED → UNMATCHED with a reason; the payment goes back from CLEARED to RELEASED (E-VS2-01-11). Refused when the payment is REVERSED: that line stays matched (E-VS2-04-1). Events BankLineUnmatched + PaymentUncleared |
| `treasury/recognize-bank-charge` | `bank_charge:recognize` (no) | An UNMATCHED DEBIT line becomes CHARGE_RECOGNIZED and R-10 is posted for its full amount at the line's value date (late entry if BANK-REC is closed). No un-recognize in VS#2 |

Query `GET treasury/bank-statements/{id}/match-suggestions` (`bank:read`): for each UNMATCHED DEBIT line, the RELEASED payments of
the account with the exact amount whose value date puts the line in the 10-day window and that the line names (basis `PAYMENT_NO`:
the reference or description contains `PAG-…`; `REFERENCE`: the reference equals the payment's). When none does and exactly one
payment fits by amount and date, it is suggested with basis `AMOUNT_ONLY`. Suggestions are never applied automatically.

**Formats** (E-VS2-05-1). `fin.bank_statement_format` is global and versioned per bank code, seeded by migrations, read-only for
the application. The definition (parsed by `StatementFormat`) gives the encoding (UTF-8, ISO-8859-1, windows-1252), delimiter, rows
to skip at the start and at the end, the date format, decimal and thousands separators, and the columns: value date, optional
reference, description, and the amount as two columns (debit / credit), one signed column (negative = debit), or one column plus a
direction indicator with its debit and credit values. Optional `cells` give the period and the balances from the file's header;
what the format does not have comes from the command, and a value given both ways must agree (E-VS2-05-3). Fields may be quoted
(RFC 4180). **The real banks' formats are pending the owner's samples**; tests use `TEST_BANK` and `TEST_SIGNED` from
`tests/migrations/0005__test_bank_formats.sql`.

**Import rules.** The same file (SHA-256) for the account again is `STATEMENT_ALREADY_IMPORTED` with the existing statement. Any
unreadable row, a zero amount, more than 2 decimals, an empty description or a date outside the period rejects the whole file, with
the row numbers (E-VS2-05-9); a blank reference is NULL. Opening + credits − debits must equal closing over all of the file's lines.
Identical lines of one file are numbered by `occurrence`; a line already imported from another file of the account (IDM-04,
E-VS2-01-12) is not inserted again but reported in the result and the event as a duplicate with the existing line and statement
(E-VS2-05-4, BNK-01). A new line dated in a period whose BANK-REC is CLOSED refuses the import (E-VS2-05-5); each period is locked in
shared mode on BANK-REC, as postings do. Lock order: bank account → period × BANK-REC.

**Lock order for lines** (E-VS2-05-8): payment → statement line → bank account (shared). A charge: line → bank account → period ×
BANK-REC (engine).

**R-10** (E-VS2-05-7, DRAFT until the Controller approves it): Dr BANK_CHARGES, Cr BANK (subledger BANK = the bank account, posted to
its own GL account), both `charge_amount`, business date = the line's value date. The 0.15 % DGII tax is charged to BANK_CHARGES in
VS#2. CREDIT lines stay UNMATCHED as in-transit items.

**Database guarantees** (migration 0028): `fin.bank_statement_file` keeps the bytes of every statement, append-only, with the
statement's SHA-256 (checked on insert, and every statement has its file at COMMIT); a payment is matched to one line at most
(partial unique index); a charge is recognized on a DEBIT line only; at COMMIT a CLEARED payment has exactly one matched line, a
RELEASED one none, a MATCHED line's payment is CLEARED (or REVERSED after being cleared), and a CHARGE_RECOGNIZED line has the
unreversed AUTO journal of its BankChargeRecognized event (K-25).

Tests: `BankStatementTests` (BNK-01…03, occurrence and kept file, rejections, missing format and closed period, the signed
windows-1252 format with header cells, match guards, unmatch and the CLEARED → REVERSED path end to end, E-VS2-04-6).
