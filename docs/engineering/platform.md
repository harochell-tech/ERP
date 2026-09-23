# Platform (PR-02)

`src/Rochell.Platform` is provider-agnostic (`System.Data.Common`). Hosts and tests supply an `NpgsqlDataSource`.

## Command pipeline (Frozen Baseline Patch 1 §5.2)

1. Ids are generated before any INSERT (`command_id`, `result_ref`, events).
2. `BEGIN` (READ COMMITTED); `set_config('app.company_id' | 'app.session_id' | 'app.correlation_id', …, true)`.
3. `INSERT core.command_log` with `result_payload = NULL`. A concurrent command with the same
   `(company_id, command_type, idempotency_key)` waits on the unique index; when the first commits, the second
   rolls back and returns the stored `result_ref`/`result_payload` (`DUPLICATE_RETURNED`).
4. Handler: `CommandContext.AppendEventAsync` inserts `core.domain_event` immediately (documents reference it),
   with `event_sequence` per aggregate version, `command_event_index` per command and the canonical `row_hash`.
5. `INSERT core.outbox` for events drafted with `Publish = true`, in emission order.
6. The only `UPDATE core.command_log` (result + `committed_at`) right before `COMMIT`.
7. `COMMIT`: deferred trigger rejects a command_log row without result.
8. Outside the transaction: `obs.request_log` via `RequestLogWriter` (queue + `FlushAsync`; failures never reach commands).

Retries: whole command, same idempotency key, only on `40001`/`40P01`, backoff 50/200/800 ms + jitter.
Handlers must not call external systems; those go through the outbox.

## Database guarantees

| Object | Guarantee |
| --- | --- |
| `core.command_log` | Result written once, only by the inserting transaction (`xmin` guard); immutable afterwards; no DELETE/TRUNCATE |
| `core.domain_event`, `core.inbox`, `core.state_history`, `core.document_link` | Append-only (UPDATE/DELETE/TRUNCATE raise, even for the owner) |
| `core.outbox` | Application role may update only `available_at`, `dispatched_at`, `attempts` |
| `core.deployment_environment` | One row, written by the deployment role, immutable; `core.current_environment()` is the only source for the fiscal production gate |
| Company FKs | `(company_id, id)` composite keys: rows of one company cannot reference another company's rows |
| `rochell_app` | No DELETE/TRUNCATE anywhere; no writes to `md.company` or `core.deployment_environment` |

## Row hash v1

`SHA-256( field("ROCHELL-LEDGER-v1") ‖ field(ledger) ‖ field(col₁) ‖ … )`, each field = 4-byte big-endian length + UTF-8
(NULL = `FFFFFFFF`). All columns except `row_hash`, in declared order (erratum E-PR02-2). Timestamps are microsecond UTC,
JSON is RFC 8785 restricted to integers ≤ 2^53−1 (decimals as strings; erratum E-PR02-3). Golden vectors in
`tests/Rochell.Platform.Tests/CanonicalHashTests.cs` were produced by an independent implementation.

## Outbox dispatcher

`OutboxDispatcher.DispatchPendingAsync` claims rows `FOR UPDATE OF o SKIP LOCKED` in `outbox_id` order, delivers each event to
matching `IEventConsumer`s — each in its own transaction with an `INSERT … ON CONFLICT DO NOTHING` into `core.inbox`
(duplicate delivery = no-op) — then marks the row dispatched. A failing consumer leaves the row pending with exponential
backoff (max 300 s). The host schedules the dispatcher (PR-18).
