# Explain this entry (PR-17)

`ExplainEntry(glEntryId)` — a read-only query (`audit:read`: Controller, Auditor) — answers "why does this GL line exist?"
from the journal and what it references, never from document screens (E-11):

| Part | Where it comes from |
| --- | --- |
| Event | `core.domain_event` of the journal, its command (`command_log`) and the user of the session |
| Document | The document whose `posting_event_id` is that event (receipt, receipt reversal, correction, supplier invoice); an invoice reversal by its aggregate; a repost by the event it reposts |
| Rule | Rule code and the version used, with its close component |
| Mapping | `account_role_map_id` recorded in the line: role, category, validity, approver |
| Policies | Every `policy_version_id` recorded in the line (e.g. INVENTORY for R-05 / R-07B, POSTING for rounding) |
| Fiscal | The supplier invoice's tax determination and its lines |
| Integrity | Seal state and ledger_sequence of the journal (PR-15) |
| Text | The rule line's template with document and input placeholders; exact reversals quote the original line; repost generations and late entries say so |

R-05 and R-07B record in each line the allocation method, policy version, area quantity, Q, s and D (POL-01), so Explain can
show how a price difference was split between stock and variance.

## Query pipeline

`QueryPipeline` authorizes like a command (session, permission, step-up, RLS; the session's last activity may be refreshed),
then switches the transaction to READ ONLY before the handler runs: a query cannot write. No command_log, events or
request_log. PR-18 exposes queries and commands over HTTP.
