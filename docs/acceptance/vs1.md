# Vertical Slice #1 — acceptance (PR-19)

Frozen Baseline v2.1.1 §15–§17 with Patch 1 and Patch 1.1 (errata E-PR19-1…15). The slice is accepted when every acceptance test
is green **and** the ledger code has been reviewed by a second person (§15.7, §17 PR-19, B-02).

## Status

| Criterion | State |
| --- | --- |
| 70 acceptance tests of §15 / Patch 1 / Patch 1.1, each with a tagged test | Done — enforced by `AcceptanceTraceabilityTests` (E-PR19-1) |
| Full regression green in CI (`ci / build-test`) | See the PR-19 CI run |
| CC-04 (50 workers × 60 s of mixed commands) | Green in the regular CI (E-PR19-2) |
| PF-01 on the reference hardware (GitHub `ubuntu-24.04`) | Workflow `load` (E-PR19-3); results below |
| Second-person review of the ledger modules (B-02) | **Pending** — the slice is not accepted until it is done (E-PR19-8) |

## PF-01

Harness `tests/Rochell.LoadHarness`: 10,000 goods receipts through the real command pipeline (application role, row-level
security, posting engine, ledgers, deferred checks), 8 workers, sealer running; then the 8 reconciliations.

| Run | Receipts | p50 / p95 / p99 / max (ms) | 8 reconciliations | Result |
| --- | --- | --- | --- | --- |
| Full-position check — local, 12 CPU (macOS, Docker Desktop) | 10,000 / 10,000 | 75 / 287 / 475 / 1003 | < 0.1 s, all MATCHED | pass |
| Full-position check — GitHub `ubuntu-24.04`, 4 CPU | 10,000 / 10,000 | 176 / **502** / 835 / 1599 | 0.1 s, all MATCHED | **fail** |
| Delta form (0021) — local, 12 CPU | 10,000 / 10,000 | 29 / 60 / 78 / 269 | 0.2 s, all MATCHED | pass |
| Delta form (0021) — GitHub `ubuntu-24.04`, 4 CPU | see the latest `load` run of the PR-19 branch | | | |

E-PR19-10: the full-position check re-summed the history of a position at every COMMIT; its cost grew with that history (median
32 ms at 1,000 receipts, 75 ms at 10,000) and it missed the limit on the reference runner. The delta form of Patch 1 precision 1
replaced it: each ledger insert adds its delta to control totals kept on the valuation row by SECURITY DEFINER triggers, and the
deferred check compares that one row. The median no longer grows with history.

PF-01 also caught a defect before it shipped: a second unique index on a table written by `INSERT … ON CONFLICT (pk)` makes
concurrent first inserts fail (23505); E-PR19-14 excludes those tables.

## Conditions still open (outside the code)

| # | Condition | Owner |
| --- | --- | --- |
| B-02 | Second-person review of the ledger code (Posting Engine, inventory ledger, receipts, corrections, invoices, repost, close) | Alexander |
| B-03 | Staging PostgreSQL 17 and object-lock (WORM) storage; then the daily digest, hash verification over WORM, and PF-01 repeated on staging | Tech lead |
| A-01 | Controller approves the account maps and policy values | Management + Controller |
| A-02 | Official DGII sources (PRODUCTION) for purchase ITBIS and withholding; until then production activation is blocked by design (P-7, E-PR19-9) | Fiscal specialist |
| A-03 | Google Workspace OIDC client and domain for staging | Alexander |
| E-PR19-11 | Nightly ACC-EVIDENCE / INV-VALUE-GL runs need a service identity; until then they run on demand and CloseComponent runs the blocking ones | Operations |

## Traceability matrix

Expected outcome as frozen in the baseline (the latest patch wins); tests as tagged `[Trait("Acceptance", "<ID>")]`
(`AcceptanceTraceabilityTests` fails when an ID is missing here or has no tagged test; keep the Tests column in sync when a
tagged test is renamed).

| ID | Then (baseline) | Tests |
| --- | --- | --- |
| AT-01 | stock 40 t; valor 40,000; journals R-01 ×2; GRNI 40,000 Cr; PO RECEIVED | Rochell.Api.Tests · `AT01_two_receipts_of_20_t_against_an_approved_order_of_40_t_at_1000`<br>Rochell.Inventory.Tests · `AT01_AT02_receipts_keep_ledgers_balances_and_GL_in_agreement`<br>Rochell.Procurement.Tests · `AT01_receipt_moves_order_inventory_and_ledger_together` |
| AT-02 | AP = 40,000 + T − W; GRNI saldo 0; accounting POSTED; AP-GL MATCHED | Rochell.Api.Tests · `AT02_invoice_of_40_t_at_1000_with_ITBIS_and_withholding_clears_GRNI_and_AP_GL_matches`<br>Rochell.Inventory.Tests · `AT01_AT02_receipts_keep_ledgers_balances_and_GL_in_agreement`<br>Rochell.Procurement.Tests · `AT02_invoice_at_the_PO_price_clears_GRNI_and_creates_the_payable` |
| AT-03 | s = 0.25; RAW_MATERIAL +500 (value PRICE_ADJUSTMENT); PPV +1,500; INV-VALUE-GL MATCHED | Rochell.Procurement.Tests · `AT03_only_the_share_still_in_stock_goes_to_inventory_the_rest_to_PPV`<br>Rochell.Procurement.Tests · `AT03_price_difference_fully_covered_by_stock_goes_to_inventory` |
| AT-04 | Σ débito = Σ crédito; cada value entry con exactamente un gl_entry (VALUE-GL-LINK) | Rochell.Finance.Tests · `AT04_posting_writes_a_balanced_journal_lines_and_balances`<br>Rochell.Procurement.Tests · `CC04_fifty_workers_of_mixed_commands_leave_the_ledgers_reconciled` |
| AT-05 | ROLLBACK total: no existe GR, ni lote, ni quantity/value entry, ni journal, ni command_log, ni integrity_state; `qty_received` sin cambio; request_log `REJECTED_DOMAIN / POSTING_PREREQUISITE_MISSING` con el requisito faltante. Tras activar el mapeo, el reintento con la misma clave ejecuta normalmente | Rochell.Procurement.Tests · `AT05_missing_posting_prerequisite_writes_nothing` |
| AT-06 | Una TX: REVERSAL exacto de generación 1 + AUTO generación 2 con cuenta B; cada value entry con un solo gl_entry; subledger = GL; `UNIQUE(source_event_id, rule, generation)` respetado | Rochell.Procurement.Tests · `AT06_repost_moves_the_journal_to_the_current_mapping_and_the_receipt_can_still_be_reversed` |
| AT-07 | ACC-EVIDENCE reporta ERROR; hash verification falla | Rochell.Reconciliation.Tests · `AT07_a_journal_deleted_by_a_superuser_is_an_evidence_error_and_breaks_the_hash_chain` |
| CC-01 | Uno POSTED; el otro rechazado; qty_received = 30 | Rochell.Procurement.Tests · `CC01_concurrent_receipts_never_exceed_the_tolerance` |
| CC-02 | Una gana; la otra falla por guarda o versión | Rochell.Procurement.Tests · `CC02_a_reversal_and_a_correction_of_the_same_receipt_race_and_only_one_wins` |
| CC-03 | Resultado final respeta `qty_invoiced ≤ qty_received`; una de las dos falla | Rochell.Procurement.Tests · `CC03_posting_and_a_negative_receipt_correction_never_leave_more_invoiced_than_received` |
| CC-04 | Sin deadlocks no resueltos; AT-04 y conciliaciones INV-QTY-BALANCE e INV-VALUE-GL en cero | Rochell.Procurement.Tests · `CC04_fifty_workers_of_mixed_commands_leave_the_ledgers_reconciled` |
| CC-05 | Espera el lock; tras el cierre cae al primer día abierto con late_entry; si no existe período abierto alcanzable → rechazo (P-1) | Rochell.Reconciliation.Tests · `CC05_a_posting_into_a_period_being_closed_waits_and_lands_as_a_late_entry` |
| CMD-01 | Rechazado por trigger | Rochell.Platform.Tests · `CMD01_only_result_columns_may_be_written_even_inside_the_inserting_transaction`<br>Rochell.Platform.Tests · `CMD01_result_cannot_be_updated_after_commit` |
| CMD-02 | Falla por trigger diferido; ROLLBACK total | Rochell.Platform.Tests · `CMD02_commit_without_result_is_rejected` |
| CMD-03 | La segunda espera el índice y devuelve `result_ref`/`result_payload` del primero | Rochell.Platform.Tests · `ID02_CMD03_concurrent_same_key_executes_once_and_second_waits` |
| EX-01 | Evento, documento, regla y versión, mapeo, política, determinación fiscal, explicación renderizada | Rochell.Procurement.Tests · `EX01_a_receipt_line_explains_its_event_document_rule_mapping_and_text` |
| FIS-01 | `definition_hash` cambia; la activación se rechaza hasta un nuevo test run con el hash nuevo | Rochell.Tax.Tests · `FIS01_a_definition_never_changes_and_a_new_version_needs_its_own_passing_run` |
| FIS-02 | Rechazado | Rochell.Tax.Tests · `FIS02_in_production_a_version_with_only_TEST_sources_is_not_activated` |
| FIS-03 | El SET no tiene efecto sobre el gate; el UPDATE falla por permisos; la activación se rechaza por falta de fuente PRODUCTION | Rochell.Tax.Tests · `FIS03_a_session_cannot_spoof_the_environment_to_activate_with_TEST_sources` |
| HS-01 | Cadena válida; ningún comando esperó al sellador | Rochell.Audit.Tests · `HS01_a_command_commits_while_a_sealing_transaction_is_open`<br>Rochell.Audit.Tests · `HS01_two_hundred_concurrent_postings_seal_into_a_valid_chain` |
| HS-02 | Reporta el primer ledger_sequence inválido | Rochell.Audit.Tests · `HS02_an_amount_altered_by_a_superuser_is_reported_at_its_sequence` |
| ID-01 | Mismo resultado; 0 filas nuevas en todas las tablas; request_log registra DUPLICATE_RETURNED | Rochell.Platform.Tests · `ID01_duplicate_returns_original_result_and_writes_nothing` |
| ID-02 | Una ejecuta; la otra espera en el índice y devuelve el mismo resultado | Rochell.Platform.Tests · `ID02_CMD03_concurrent_same_key_executes_once_and_second_waits` |
| ID-03 | Sin fila en command_log; request_log con REJECTED_DOMAIN; reintento con misma clave vuelve a evaluarse | Rochell.Platform.Tests · `ID03_domain_rejection_leaves_no_command_log_and_allows_retry` |
| ID-04 | ROLLBACK total: sin GR, sin eventos, sin outbox, sin command_log; request_log con FAILED_TECHNICAL | Rochell.Platform.Tests · `ID04_technical_failure_rolls_back_everything` |
| ID-05 | Comando exitoso; error de observabilidad solo en log técnico | Rochell.Platform.Tests · `ID05_request_log_failure_never_affects_the_command` |
| ID-06 | GoodsReceiptPosted (seq 1) y PurchaseOrderLineReceived (seq 2) con la misma aggregate_version; command_event_index 1..n sin huecos | Rochell.Platform.Tests · `ID06_several_events_per_version_with_deterministic_order` |
| ID-07 | Inbox registra una vez; efecto único | Rochell.Platform.Tests · `ID07_redelivered_event_is_applied_once` |
| INT-01 | accounting_status POSTED; integrity_state PENDING_SEAL; la respuesta no esperó al sellador | Rochell.Api.Tests · `AT01_two_receipts_of_20_t_against_an_approved_order_of_40_t_at_1000` |
| INT-02 | integrity_state SEAL_ERROR; alerta CRITICAL; cierre bloqueado | Rochell.Api.Tests · `A_group_altered_before_sealing_raises_a_critical_alert_from_the_hosted_sealer`<br>Rochell.Reconciliation.Tests · `INT02_a_group_altered_before_sealing_is_a_SEAL_ERROR_that_blocks_the_close` |
| INT-03 | Rechazado por el close gate; tras sellar el evento → permitido | Rochell.Reconciliation.Tests · `INT03_one_unsealed_domain_event_of_the_period_blocks_the_close_until_it_is_sealed` |
| IV-01 | Rechazado por CHECK | Rochell.Inventory.Tests · `IV01_no_command_or_direct_write_leaves_stock_negative` |
| IV-02 | Valor removido = valor remanente (vaciado); valuation value = 0 | Rochell.Inventory.Tests · `Moving_average_issue_and_the_last_unit_takes_the_remaining_value` |
| IV-03 | VAL-RESIDUAL con hallazgo; cierre INV-MOV rechazado; tras ajuste aprobado R-06 → cierre permitido | Rochell.Procurement.Tests · `IV03_an_orphan_value_goes_to_zero_against_the_policy_account`<br>Rochell.Reconciliation.Tests · `IV03_an_orphan_value_blocks_INV_MOV_until_R06_removes_it` |
| IV-04 | No existe; test de esquema | Rochell.Inventory.Tests · `IV04_there_is_no_RESERVED_availability_nor_reserved_quantity` |
| IV-05 | Mismos importes contables (INV-09) | Rochell.Inventory.Tests · `IV05_the_physical_lot_issued_does_not_change_the_accounting` |
| PD-01 | posting_date primer día abierto; late_entry = true | Rochell.Finance.Tests · `PD01_closed_component_moves_the_posting_to_the_next_open_period_as_late_entry`<br>Rochell.Procurement.Tests · `PD01_receipt_of_a_closed_month_is_posted_late_in_the_next_open_period` |
| PD-02 | Rechazado; tras ejecutar el sellador → permitido | Rochell.Reconciliation.Tests · `PD02_an_unsealed_group_of_the_period_blocks_the_close_until_the_sealer_runs` |
| PF-01 | p95 PostGoodsReceipt < 500 ms; conciliaciones < 30 s | `tests/Rochell.LoadHarness` (workflow `load`) |
| RC-01 | Journal A inverso exacto de R-01; sin Journal B; value entries REVERSAL_OF; stock y valor vuelven al estado previo | Rochell.Procurement.Tests · `RC01_clean_reversal_restores_everything_and_keeps_the_original_journal` |
| RC-01b | A retira 30 t y 3,000 → área 0 t, −300; B (R-02B, target 0): R = 300 → Dr RAW_MATERIAL 300 / Cr PPV 300; área final 0 t, 0 | Rochell.Procurement.Tests · `RC01b_reversing_the_untouched_receipt_after_the_other_lot_was_consumed_reallocates_300` |
| RC-02 | Rechazado con mensaje "usar ReceiptCorrection" | Rochell.Procurement.Tests · `RC02_a_lot_that_moved_after_the_receipt_requires_a_correction` |
| RC-03 | q₁ = 2, q₂ = 1; RAW_MATERIAL −2·avg; MATERIAL_USAGE_VARIANCE 1·P; GRNI −3·P; cuadra | Rochell.Procurement.Tests · `RC03_less_received_splits_into_remaining_stock_and_usage_variance` |
| RC-04 | Rechazado (`qty_invoiced ≤ qty_received`) | Rochell.Procurement.Tests · `RC04_an_invoiced_quantity_cannot_be_corrected_away` |
| RC-05 | Rechazado; con ApproveOverReceipt previo → POSTED | Rochell.Procurement.Tests · `RC05_increase_beyond_tolerance_needs_an_approved_over_receipt` |
| RC-06 | Rechazado (CHECK aprobador ≠ creador) | Rochell.Procurement.Tests · `RC06_the_creator_can_never_be_the_approver` |
| REV-01 | Cada línea del REVERSAL = línea del original con débito y crédito intercambiados, mismas cuentas y dimensiones | Rochell.Finance.Tests · `REV01_reversal_is_the_exact_inverse_and_happens_once` |
| RL-01 | Rechazado (CHECK requested_by ≠ user_id) | Rochell.Identity.Tests · `RL01_nobody_can_request_a_role_for_themselves` |
| RO-01 | Rechazado; otro usuario con `period_component:second_approve` → REOPENED | Rochell.Reconciliation.Tests · `RO01_reopening_needs_a_second_approver_and_a_reclose_leaves_its_own_snapshot` |
| RO-02 | Rechazado por SoD | Rochell.Identity.Tests · `Database_rejects_conflicting_role_combinations`<br>Rochell.Identity.Tests · `RO02_whoever_may_reopen_a_period_cannot_also_second_approve_it` |
| SC-01 | Rechazado (CHECK y comando) | Rochell.Procurement.Tests · `SC01_creator_cannot_approve_even_holding_the_approver_role` |
| SC-02 | Rechazado por SoD | Rochell.Identity.Tests · `SC02_segregation_of_duties_blocks_the_approval` |
| SC-03 | Rechazado | Rochell.Identity.Tests · `Step_up_is_required_after_five_minutes_and_restored_by_re_authentication` |
| SC-04 | Rechazado | Rochell.Identity.Tests · `SC04_human_users_need_employee_subject_and_lowercase_email` |
| SI-01 | Rechazado (line_kind) | Rochell.Procurement.Tests · `SI01_and_other_invalid_registrations_are_rejected` |
| SI-02 | MATCH_EXCEPTION; Post rechazado; CHECK `qty_invoiced ≤ qty_received` impide forzarlo | Rochell.Procurement.Tests · `SI02_SI03_billing_more_than_received_is_not_approvable_until_the_receipt_is_corrected` |
| SI-03 | MATCHED | Rochell.Procurement.Tests · `SI02_SI03_billing_more_than_received_is_not_approvable_until_the_receipt_is_corrected` |
| SI-04 | Ambas operaciones exitosas (índice parcial) | Rochell.Procurement.Tests · `SI04_SI05_a_voided_invoice_frees_its_fiscal_number_and_an_active_one_does_not` |
| SI-05 | Rechazado | Rochell.Procurement.Tests · `SI04_SI05_a_voided_invoice_frees_its_fiscal_number_and_an_active_one_does_not` |
| SI-06 | Journal A inverso exacto (RAW_MATERIAL +500 revertido, variación 1,500 revertida); Journal B con R = 0.25·2,000 = 500: Dr RAW_MATERIAL 500 / Cr PPV 500; efecto neto en inventario = 0 (nada de la diferencia sigue en stock); subledger = GL; GRNI reabierto; `qty_invoiced` restaurada | Rochell.Procurement.Tests · `SI06_reversal_after_the_remaining_stock_left_moves_the_price_difference_back_to_variance` |
| SI-06b | Solo Journal A; sin Journal B | Rochell.Procurement.Tests · `SI06b_reversal_with_no_later_movement_is_only_the_exact_inverse` |
| SI-07 | Rechazado por gate fiscal | Rochell.Procurement.Tests · `SI07_a_withholding_rule_pending_its_source_closes_the_gate_and_nothing_is_written` |
| SI-08 | Rechazado en preflight; factura sigue MATCHED/NOT_POSTED; sin journal ni value entries | Rochell.Procurement.Tests · `SI08_non_recoverable_ITBIS_blocks_posting_and_the_invoice_can_then_be_voided` |
| TEN-01 | La base rechaza por FK compuesta; lo mismo para goods_receipt_line con lote de B y gl_entry con cuenta de B | Rochell.MasterData.Tests · `TEN01_masters_cannot_reference_rows_of_another_company` |
| TEN-02 | No ve filas de B (RLS) | Rochell.Identity.Tests · `Row_level_security_isolates_companies_for_the_application_role`<br>Rochell.Procurement.Tests · `Row_level_security_isolates_purchase_orders` |
| TMP-01 | Rechazado por exclusion constraint; con cierre del anterior al 15/10 → permitido. Igual para posting_rule_version, accounting_policy_version, fiscal_rule_version y uom_conversion | Rochell.Finance.Tests · `TMP01_active_mappings_and_rule_versions_cannot_overlap`<br>Rochell.MasterData.Tests · `TMP01_overlapping_conversions_are_rejected_by_the_database` |
| TST-01 | No existe | Rochell.ArchitectureTests · `Production_migrations_contain_no_test_fixtures` |
| VAL-01 | Cada value entry con exactamente un gl_entry de igual importe; Σ value = Σ RAW_MATERIAL por posición tocada | Rochell.Procurement.Tests · `CC04_fifty_workers_of_mixed_commands_leave_the_ledgers_reconciled` |
| VAL-02 | Falla por trigger diferido; ROLLBACK total | Rochell.Inventory.Tests · `P1_value_entry_without_its_GL_line_fails_at_commit` |
| VAL-03 | Comando: error de dominio; INSERT directo: rechazado por CHECK. Lo mismo para una línea de factura de proveedor a precio 0 | Rochell.Procurement.Tests · `VAL03_a_zero_price_line_is_rejected_by_the_database_too` |

