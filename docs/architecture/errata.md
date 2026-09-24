# Approved implementation errata

Errata approved by Alexander Rochell while implementing Vertical Slice #1. They complement the frozen baseline
(precedence: Frozen Baseline Patch 1.1 → Patch 1 → Errata & Frozen Baseline → v2.1 → v2) without changing its intent.

| Id | PR | Decision |
| --- | --- | --- |
| E-PR02-1 | PR-02 | `core.command_log.session_id` had no FK in PR-02 because `iam` did not exist; PR-03 adds `command_log_session_fk → iam.session`. |
| E-PR02-2 | PR-02 | Row hash of `core.domain_event` covers all columns except `row_hash`, in declared order. |
| E-PR02-3 | PR-02 | Canonical JSON (RFC 8785) accepts only integers with \|n\| ≤ 2^53−1; decimal values travel as strings (ADR-015). |
| E-PR03-1 | PR-03 | `iam.role_assignment.plant_id` and `iam.role_assignment_request.plant_id` have no FK until PR-04 creates `md.plant`; PR-04 adds composite FKs. |
| E-PR03-2 | PR-03 | Step-up ("reautenticación reciente") maximum age is host configuration; initial value 5 minutes. |
| E-PR03-3 | PR-03 | PR-03 implements login from already-validated OIDC claims (IdP simulated in tests); HTTP OIDC middleware wiring moves to PR-18 with the first endpoints. |
| E-PR03-4 | PR-03 | (a) `fiscal_rule:configure` ↔ `fiscal_rule:activate` is document-level only (existing CHECK). (b) The AUDITOR role cannot be combined with WRITE/SECURITY permissions. (c) Holders of `role:assign`/`role:revoke` cannot hold WRITE permissions. Permissions are classified READ / WRITE / SECURITY. |
| E-PR03-5 | PR-03 | In VS#1 users are provisioned by the deployment role (`rochell-migrate create-user`); bootstrap grants use `rochell-migrate grant-role` with a seeded SERVICE identity as `granted_by`. `user:register` is deferred. |
| E-PR03-6 | PR-03 | Office sessions: absolute lifetime 12 h, inactivity 30 min, both configurable. Requires `iam.session.last_activity_at` (refreshed at most once a minute). |
| E-PR03-7 | PR-03 | SoD exceptions are out of VS#1: `role_assignment.sod_exception_id` must be NULL. |
| E-PR03-8 | PR-03 | Login requires `email_verified = true`, `hd` = configured domain and an OIDC subject linked to an ACTIVE HUMAN user with employee. MFA is enforced by Google Workspace, not verified in the token. |
| E-PR04-1 | PR-04 | `DefineUomConversion` requires `item:activate` (Controller): a conversion changes inventory quantities. |
| E-PR04-2 | PR-04 | Plants (with their valuation area) and locations are created by the deployment role: `rochell-migrate create-plant` / `create-location`. |
| E-PR04-3 | PR-04 | VS#1 master lifecycle is DRAFT → ACTIVE; the Controller's activation is the approval. The enum keeps REVIEW/APPROVED/OBSOLETE. |
| E-PR04-4 | PR-04 | `UpdateSupplier` only in DRAFT; changes to active suppliers (with re-approval) come after VS#1. |
| E-PR04-5 | PR-04 | RNC validated by format only (9 digits RNC / 11 digits cédula, separators removed) through `IRncRegistry`; `rnc_validated_at` stays NULL until the DGII lookup exists. |
| E-PR04-6 | PR-04 | The schema allows FOREIGN parties; the VS#1 command creates LOCAL suppliers only. |
| E-PR04-7 | PR-04 | `item_category` is a closed list: CEMENTO, AGREGADO, ADITIVO, OTRA_MATERIA_PRIMA. |
| E-PR04-8 | PR-04 | Conversions always point to the item's base UOM; a new one closes the open one and cannot start before today. |
| E-PR04-9 | PR-04 | UOM seed: kg, t (mass), m3, l (volume), un (count). |
| E-PR05-1 | PR-05 | The chart of accounts is loaded by the deployment role (`rochell-migrate import-accounts`, CSV approved by the Controller); no account creation from the application in VS#1. |
| E-PR05-2 | PR-05 | Posting rule definition is declarative JSON (lines: code, side, account role, amount name, dimensions, subledger); code only computes the named amounts; versions go DRAFT → ACTIVE on Controller approval. |
| E-PR05-3 | PR-05 | Monthly calendar periods created with `rochell-migrate open-periods <rnc> <year>`, both components OPEN; posting to a date without period is rejected. |
| E-PR05-4 | PR-05 | Each rule version declares its close component (INV-MOV or AP-REC); if closed for the business date's month the journal goes to the first day of the next open period with `late_entry = true`; none open → rejected. |
| E-PR05-5 | PR-05 | `fin.account_role` seeded with the slice roles; control roles (AP_CONTROL, RAW_MATERIAL) map only to control accounts and vice versa. |
| E-PR05-6 | PR-05 | DOP only in VS#1 (CHECK). |
| E-PR05-7 | PR-05 | GL lines rounded half-up to 2 decimals (CHECK); rounding differences go to ROUNDING_DIFFERENCE within the policy tolerance. |
| E-PR05-8 | PR-05 | Row hash of `gl_journal`/`gl_entry` covers all columns except `row_hash`; `gl_entry.inv_value_entry_id` gets its FK in PR-07. |
| E-PR06-1 | PR-06 | Three policies: PURCHASING (receipt and match tolerances), INVENTORY (adjustment materiality, GRNI aging, price-variance allocation = STOCK_COVERAGE), POSTING (rounding tolerance, late-entry hours). |
| E-PR06-2 | PR-06 | Policy versions go DRAFT → ACTIVE on approval; the effective range decides when they apply. |
| E-PR06-3 | PR-06 | `acc.policy_parameter_definition` (type, min, max, allowed values) validates versions; every parameter of the policy is required. |
| E-PR06-4 | PR-06 | Role APROBADOR_POLITICAS seeded with only `accounting_policy:approve`. |
| E-PR06-5 | PR-06 | Parameter values are JSON strings (decimals as `"0.02"`). |
| E-PR06-6 | PR-06 | Architecture test: no decimal literals in `src/` other than 0m, 1m, 100m, except column type limits marked `// type-limit`. |
| E-PR06-7 | PR-06 | A missing ACTIVE policy is a missing posting prerequisite (full rollback); initial values are set by the Controller (A-01). |
| E-PR07-1 | PR-07 | Inventory value entries have 2 decimals (half-up), equal to their GL line; the moving average is not rounded; issuing the last unit takes the remaining value. |
| E-PR07-2 | PR-07 | Quantities always in the item's base UOM, `numeric(18,6)`; conversion happens in the document. |
| E-PR07-3 | PR-07 | Lot mandatory for raw-material movements; one lot per receipt line (PR-09), supplier lot number optional. |
| E-PR07-4 | PR-07 | Every GL line with subledger INV carries plant, item and its value entry (CHECK); the GL ↔ value-entry link is 1:1 both ways. |
| E-PR07-5 | PR-07 | Movement type ISSUE exists in the schema but in VS#1 only the R-T1 test fixture uses it (TST-01, architecture test). |
| E-PR07-6 | PR-07 | Row hash of inventory ledgers covers all columns except `row_hash`. |
| E-PR07-7 | PR-07 | Stock balance by location × item × lot; valuation balance by valuation area (plant) × item. |
| E-PR07-8 | PR-07 | At COMMIT, for touched positions: valuation = Σ value entries = Σ GL (RAW_MATERIAL, plants of the area, item); quantities agree across ledger, stock and valuation. |
| E-PR08-1 | PR-08 | Module dependencies are an explicit, acyclic allow-list verified by ArchitectureTests: Procurement → Finance, Inventory, MasterData, Tax; every module → Platform. No separate contracts projects. |
| E-PR08-2 | PR-08 | PURCHASING policy gains `po_approval_limit` (purchasing approver; the Controller has no limit) and `po_approval_step_up_threshold` (re-authentication when the order total exceeds it). |
| E-PR08-3 | PR-08 | `po_no` is readable but not sequential: `OC-<year>-<8 hex of the UUIDv7>` (ADR-029). |
| E-PR08-4 | PR-08 | Order quantities and prices are in the line UOM, which must be the base UOM or have a conversion valid on the order date; the goods receipt converts to base with the conversion valid at receipt. |
| E-PR08-5 | PR-08 | `UpdatePurchaseOrderDraft` replaces the lines of a DRAFT order (permission `purchase_order:create`). |
| E-PR08-6 | PR-08 | `ClosePurchaseOrder` is implemented in PR-13 with the supplier invoice. |
| E-PR09-1 | PR-09 | R-01's GRNI line carries plant and supplier (party) dimensions, for GRNI aging by supplier. |
| E-PR09-2 | PR-09 | R-01 v1 is seeded DRAFT with `effective_from = 2026-01-01`; the Controller approves it in the application. |
| E-PR09-3 | PR-09 | Goods receipt number `RM-yyyy-XXXXXXXX`, readable and not sequential. |
| E-PR09-4 | PR-09 | Line value = quantity (PO line UOM) × PO price, 2 decimals half-up; inventory receives the quantity converted to the base UOM with the conversion effective on the receipt date, 6 decimals. |
| E-PR09-5 | PR-09 | Weigh ticket optional in VS#1; when present it cannot repeat on another non-reversed receipt. |
| E-PR09-6 | PR-09 | `late_entry_hours` does not affect posting in VS#1 (late entry = closed component, E-PR05-4); it is reserved for reporting and reconciliations. |
| E-PR10-1 | PR-10 | R-02B `avg₀` = the area's average (value ÷ quantity) immediately before the reversal; the remaining stock keeps its unit cost. |
| E-PR10-2 | PR-10 | PURCHASE_PRICE_VARIANCE lines carry plant and item dimensions. |
| E-PR10-3 | PR-10 | `inv.movement_type` gains VALUATION_REALLOCATION (value only); the exact reversal uses RECEIPT_REVERSAL with `reverses_*_entry_id`. |
| E-PR10-4 | PR-10 | R-02B seeded DRAFT from 2026-01-01 with both side variants; R-02 (A) is the exact reversal of the R-01 journal (no new rule). |
| E-PR11-1 | PR-11 | `evidence_object_key` is a mandatory text reference (corrected ticket, photo, record) until WORM object storage exists (B-03). |
| E-PR11-2 | PR-11 | Δq > 0 always goes to the receipt's original lot and location, even with zero stock; no correction lots. |
| E-PR11-3 | PR-11 | Δq < 0 takes the receipt's lot first, then the item's other lots in the area from oldest to newest (UUIDv7 order), then by location. |
| E-PR11-4 | PR-11 | Under materiality (|Δq| × P ≤ `inventory_adjustment_materiality`) a correction is DRAFT and the Controller approves without step-up; above it starts PENDING_APPROVAL and approval needs step-up. Reject uses `receipt_correction:approve`. |
| E-PR11-5 | PR-11 | R-03A and R-03B seeded DRAFT from 2026-01-01; R-03B has both PPV side variants; MATERIAL_USAGE_VARIANCE carries plant and item. |
| E-PR12-1 | PR-12 | Fiscal sources, rules, versions, test runs and determinations carry `company_id` with RLS. |
| E-PR12-2 | PR-12 | A source stores a text reference and the SHA-256 of the consulted document (supplied by the analyst) until WORM storage exists (B-03). |
| E-PR12-3 | PR-12 | Declarative definitions: PURCHASE_ITBIS {tax_code, rate, effect RECOVERABLE_INPUT or NON_RECOVERABLE_INPUT, exempt_item_categories?}; PURCHASE_WITHHOLDING {tax_code, rate, base NET or ITBIS, party_types}. Rates are strings, 0 < rate ≤ 1, ≤ 6 decimals. |
| E-PR12-4 | PR-12 | Versions start BLOCKED_PENDING_SOURCE; source + latest passing run → READY; activation → ACTIVE; a successor closes its open predecessor at its start date, or retires it when it starts on or before it. Definitions are immutable. |
| E-PR12-5 | PR-12 | Test runs record their environment; READY and ACTIVE need the latest run of the deployment's own environment to pass. |
| E-PR12-6 | PR-12 | Supplier taxpayer type from its identifier: 9-digit RNC = COMPANY, 11-digit cédula = INDIVIDUAL, none = FOREIGN. |
| E-PR12-7 | PR-12 | Tax base and amount: 2 decimals half-up per line (`amount = round(base × rate, 2)`, enforced by CHECK). |
| E-PR13-0 | PR-13 | Split into PR-13a (schema, register, match, exception approval, void) and PR-13b (posting R-04/R-05, AP, reversal R-07, CC-03). |
| E-PR13-1 | PR-13 | Invoiced quantity above received-not-invoiced is a match exception that is never approvable; it is resolved by a receipt correction and a re-match. Under-invoicing is normal; `match_qty_tolerance_pct` is unused in VS#1. |
| E-PR13-2 | PR-13 | A line's price is within tolerance when \|P′ − P\| ≤ P × match_price_tolerance_pct OR \|Q × (P′ − P)\| ≤ match_amount_tolerance_abs. |
| E-PR13-3 | PR-13 | Non-recoverable ITBIS: posting records POSTING_BLOCKED with the determination, without journal, AP or qty_invoiced; other missing prerequisites roll back (P-1); a closed fiscal gate rejects (SI-07). |
| E-PR13-4 | PR-13 | Supplier fiscal number: format only, B + 10 digits (NCF) or E + 12 digits (e-NCF); unique per supplier among non-voided invoices. |
| E-PR13-5 | PR-13 | doc_date not in the future; due_date ≥ doc_date and mandatory; tax determination and posting use doc_date (late entry when the component is closed). |
| E-PR13-6 | PR-13 | R-04 closes with AP-REC; R-05 with INV-MOV (two journals of the same event). |
| E-PR13-7 | PR-13 | R-05 coverage s = min(area quantity, Q_base) ÷ Q_base, Q_base with the receipt's own factor; s·D to 2 decimals, PPV = D − s·D; new value-only movement PRICE_ADJUSTMENT. |
| E-PR13b-1 | PR-13b | Voiding a POSTING_BLOCKED invoice returns its accounting status to NOT_POSTED (nothing was ever posted); the event keeps the previous status. |
| E-PR13b-2 | PR-13b | R-04 dimensions: GRNI plant + supplier; AP_CONTROL supplier + AP subledger (the AP document); WITHHOLDING_PAYABLE supplier; ITBIS_RECOVERABLE plant. |
| E-PR13b-3 | PR-13b | A supplier invoice reversal is posted on the reversal date; the part of the price difference that cannot return to inventory goes to PPV through R-07B (seeded DRAFT). |
| E-PR14-1 | PR-14 | Patch 1 P-1 prevails over AT-05: a goods receipt is never POSTING_BLOCKED (a missing prerequisite rolls back, PR-09); the repost part of AT-05 is replaced by AT-06. Only a supplier invoice can be POSTING_BLOCKED (SI-08); it is retried with PostSupplierInvoice. |
| E-PR14-2 | PR-14 | RepostEvent reverses generation n exactly and posts n + 1 of the same event and rule with the rule and mappings in force, same lines, dimensions and amounts, at the event's business date (late entry if the component is closed); reason and step-up. |
| E-PR14-3 | PR-14 | New value-only movement REPOST (any sign): the reversal points to the original value entry, generation n + 1 gets a new entry of the same amount; valuation unchanged, P-1 kept. |
| E-PR14-4 | PR-14 | R-06 only removes orphan value (quantity 0, value ≠ 0) down to zero; other VAL-RESIDUAL findings are reported (PR-16), not adjusted by R-06 in VS#1. |
| E-PR14-5 | PR-14 | INVENTORY policy parameter `valuation_residual_account_role` (PURCHASE_PRICE_VARIANCE or INVENTORY_ADJUSTMENT) chooses R-06's counter-account; new value-only movement RESIDUAL_ADJUSTMENT; R-06 seeded DRAFT. |
| E-PR15-1 | PR-15 | `audit.integrity_state` (Patch 1 P-2) is created in PR-15; PENDING_SEAL rows are written by AFTER INSERT triggers on the ledger tables, in the command's transaction; a SEALED or SEAL_ERROR group cannot receive rows. |
| E-PR15-2 | PR-15 | VS#1 chains: GL (group = journal: header, entries by line_no), INV_QTY and INV_VALUE (group = source event), DOMAIN_EVENT (group = command, events by command_event_index). |
| E-PR15-3 | PR-15 | Sealer: background process as DB role `rochell_sealer`, the only writer of seals and sealing states; advisory lock per chain, every 5 s, batches of 1,000; a group whose rows no longer match their stored hashes becomes SEAL_ERROR (CRITICAL alert) and the chain continues; close blocking in PR-16. |
| E-PR15-4 | PR-15 | WORM behind an interface: write-once file store for CI/development (refused outside TEST), S3 Object Lock at a second provider when B-03 is resolved; digest signed with ECDSA P-256; the e-mail copy is deferred until mail infrastructure exists. |
| E-PR15-5 | PR-15 | Digest day = local date of sealed_at in America/Santo_Domingo, generated at 00:15 for the previous day; no seals → no digest; each digest links to the last existing one. |
| E-PR15-6 | PR-15 | Verification is the command VerifyHashChain (`hash:verify`: Auditor, Controller): first invalid ledger_sequence, gaps, Merkle vs WORM, PENDING_SEAL older than 10 min, SEAL_ERROR groups; findings returned, not stored (PR-16). |
| E-PR15-7 | PR-15 | Module graph amendment: Rochell.Audit → Platform, Finance, Inventory (only to recompute row hashes); nothing depends on Audit. |
| E-PR15-8 | PR-15 | HS-01 is tested deterministically: 200 concurrent postings with the sealer running end in a valid chain, and a posting commits while a sealing transaction is open mid-batch. |
| E-PR16-1 | PR-16 | Reconciliations evaluate the whole ledger as of the run (global invariants), with zero tolerance; statuses MATCHED / EXCEPTIONS / FAILED (MATCHED_WITH_TOLERANCE unused in VS#1). |
| E-PR16-2 | PR-16 | Blocking per component: INV-MOV ← INV-QTY-BALANCE, INV-VALUE-BALANCE, INV-VALUE-GL, VALUE-GL-LINK, VAL-RESIDUAL, ACC-EVIDENCE (inventory documents); AP-REC ← AP-GL, ACC-EVIDENCE (invoices); GRNI-AGING is a warning and blocks nothing. |
| E-PR16-3 | PR-16 | VAL-RESIDUAL: orphan value (quantity 0, value ≠ 0) = ERROR, fixed by R-06; quantity > 0 with value ≤ 0 = WARNING; the unit-cost range criterion is deferred. |
| E-PR16-4 | PR-16 | ACC-EVIDENCE: a document POSTED needs a live journal of its posting event; REVERSED needs journals and no live AUTO journal; NOT_POSTED / POSTING_BLOCKED have no live journal. Journals of document-less events (repost, residual adjustment) are validated through their event. |
| E-PR16-5 | PR-16 | A period component closes only after the period's end date (local); no order between periods in VS#1. |
| E-PR16-6 | PR-16 | Append-only fin.close_snapshot with the component's reconciliation totals and balances; its SHA-256 is close_component_state.snapshot_hash; each close and re-close has its own snapshot. |
| E-PR16-7 | PR-16 | fin.reopen_request with RequestReopen (Controller), ApproveReopen / RejectReopen (second approver ≠ requester), all with step-up; replaces the baseline's ReopenComponent. |
| E-PR16-8 | PR-16 | Patch 1.1 date-coherence CHECK and business-date index on audit.integrity_state added with PR-16. |
| E-PR16-9 | PR-16 | Every run stored in rec.recon_run with its findings in rec.recon_exception (status OPEN); no resolution workflow in VS#1: a finding disappears when a later run no longer finds it. |
| E-PR17-1 | PR-17 | Read-only query pipeline in Platform: session, permission and RLS like commands, then SET TRANSACTION READ ONLY; no command_log, events or request_log. Explain requires `audit:read` (Controller, Auditor). |
| E-PR17-2 | PR-17 | Explain returns, per GL line: event (dates, command, user), document, rule and version, mapping used (validity, approver), policy versions, fiscal determination, late-entry dates, seal state and the rendered text. |
| E-PR17-3 | PR-17 | Generic wording in code for journals without their own template: exact reversals (R-02, R-07, repost reversals), repost generation n + 1, rounding (R-08) and a late-entry suffix. |
| E-PR17-4 | PR-17 | POL-01: R-05 and R-07B resolve the INVENTORY policy (method must be STOCK_COVERAGE) and record method, policy version, area quantity, Q, s and D per line (R-07B: s original and s now). Posting a price difference needs an ACTIVE INVENTORY policy; earlier journals are not rewritten (their values stay in the event payload). |
| E-PR17-5 | PR-17 | Placeholders come from the source document (gr_no, po_no, grr_id, rc_id, ncf) and the line's inputs; one without a value stays visible and complete = false. |
| E-PR17-6 | PR-17 | The HTTP endpoint and the screen arrive with PR-18; PR-17 delivers the query and its tests. |

Implementation rules derived from the above (no architectural change):

- Commands that are not plant-scoped require a company-wide assignment (`plant_id IS NULL`); plant-scoped commands accept a
  company-wide assignment or one for that plant.
- Segregation-of-duties checks are per company and serialized per user with an advisory lock.
- The outbox dispatcher processes companies one at a time so row-level security applies to it as to any command.
- `md.item.description` (NOT NULL) was added as a trivial field omitted by the frozen schema ("no necesito todavía cada campo trivial").
- Account-role mappings are loaded as DRAFT by the deployment role (`import-account-map`) and approved by the Controller; `prepared_by` was added to enforce preparer ≠ approver.
- Since PR-06 a rounding difference within `rounding_difference_tolerance` is booked on rule line R-08 (ROUNDING_DIFFERENCE, policy version in `determination_inputs`); only postings that need rounding depend on the POSTING policy.
- The posting rule version is chosen by the business date; the account mapping by the posting date.
- Value entry ids are pre-assigned (Errata E-4) so the Posting Engine line and the value entry reference each other in the same transaction.
- `RAW_MATERIAL` lines always use subledger INV and `AP_CONTROL` lines subledger AP (CHECK on `fin.gl_entry`).
- Conditional step-up: handlers call `context.RequireStepUpAsync` (evaluated by the identity authorizer) when the need depends on the data (E-PR08-2).
- Purchase-order commands are plant-scoped (the command carries the plant and the handler checks it matches the order); the order date is a trivial column added to validate UOM conversions.
- ADR-027 is enforced for purchase orders: a status (on insert or change) without its `core.state_history` row fails at COMMIT.
- Rejecting a purchase order uses `purchase_order:approve` (§14 lists no reject permission; whoever may approve may reject).
- Goods receipt and purchase order status changes require their `core.state_history` row in the same transaction (ADR-027, enforced by deferred triggers); a POSTED receipt requires its AUTO journal (K-25).
- Exact reversals point the inventory line's `subledger_ref` to the new inverse value entry (required by `gl_entry_inv_link`).
- Receipt reversal guard "nothing invoiced": `qty_invoiced ≤ qty_received − reversed quantity` on each PO line of the receipt.
- R-03B amounts: GRNI = |Δq| × P; stock value = q₁ × avg (the whole area value when q₁ empties it), split by lot with the remainder on the last; q₁ at P = GRNI × q₁ ÷ |Δq|; MUV = GRNI − q₁ at P; PPV = q₁ at P − stock value. Base quantities use the receipt's own conversion.
- Configure ⟂ activate for fiscal rules stays document-level (E-PR03-4 a): the same person may hold both roles but never activates a version they configured (CHECK `activated_by <> configured_by`).
- A fiscal test run requires an initialized deployment environment; without it the command fails (FAILED_TECHNICAL) instead of silently recording nothing.
- The application never locks append-only tables with FOR UPDATE (it needs the UPDATE privilege); it serializes with advisory locks instead.
- A source is registered (and approved) by the fiscal analyst; the specialist's activation reviews the version with its sources.
- Fiscal gate closed (FISCAL_GATE_CLOSED) when no purchase ITBIS rule is ACTIVE on the date, or when any rule has a version pending activation already in force on the date without an ACTIVE version covering it (SI-07). Only one purchase ITBIS rule may be active at a time.
- R-04 and R-05 are separate journals of one event and each balances: R-04 credits AP with Q·P + T − W, R-05 credits (or debits) AP with the price difference D; both AP lines reference the same AP document, so the payable is Q·P′ + T − W.
- Posting re-checks, under lock, that each line bills no more than received − invoiced (CC-03); the database CHECK `qty_invoiced ≤ qty_received` is the last guard.
- The R-05 price differences (D, Q_base, covered share, value entry) are kept in the SupplierInvoicePosted event payload; the reversal (R-07/R-07B) reads them from there.
- A value entry may be reversed once by its document and once by a repost (two partial unique indexes, migration 0017).
- Each inventory line posted by a repost records, in its determination inputs, the value entry it replaces (`reposts_value_entry_id`); document reversals map their original value entries and the Posting Engine follows that chain, so a reposted receipt or invoice still reverses.
- RepostEvent and the residual adjustment live in Procurement: under the module graph (E-PR08-1) it is the only module that may use Finance and Inventory together.
- Every inventory ledger row has as source an event committed in the same transaction (a repost's value entries belong to its JournalReposted event); that is what makes a source event a closed group for sealing.
- Blocking is a table (rec.recon_blocking) instead of the baseline's single blocks_component column, because ACC-EVIDENCE blocks both components; each finding carries the component it blocks.
- A component state changes only through its guard: OPEN/REOPENED → CLOSED with closer and snapshot hash, CLOSED → REOPENED, version + 1. Test fixtures that close directly must supply a closer and a snapshot hash (FinanceSetup.SetComponentAsync).
- A handler marked [SerializableTransaction] runs SERIALIZABLE (CloseComponent, T-13); serialization failures are retried by the pipeline.
- Master rows use optimistic concurrency: every change increments `version` by exactly 1 (database guard) and commands carry `expected_version`.
