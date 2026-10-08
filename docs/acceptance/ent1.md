# ENT-1 — acceptance matrix (driver confirms from the delivery note's QR)

Baseline `docs/architecture/ent1/frozen-baseline-ent1.md` (E-ENT-1…8, E-ENT1-01-1…10).

| Decision | What is checked | Test |
| --- | --- | --- |
| E-ENT-1, E-ENT1-01-2/4 | Gate out of an own-transport delivery opens one link; the conduce shows the QR only after the gate; a link Core did not sign is INVALID | `DriverConfirmationSchemaTests.A_confirmation_is_kept_once…`, `DriverConfirmationTests.A_wrong_pin_is_kept…`, `DriverPagesApiTests`, E2E-ENT |
| E-ENT-2, E-ENT1-01-1/3 | Dispatch sets the PIN (4 digits, only DESPACHO); wrong PINs are kept; 5 lock the link; Dispatch reopens it as a new generation and the old QR stops working | `DriverConfirmationTests.Five_wrong_pins_lock…`, E2E-ENT |
| E-ENT-3, E-ENT1-01-7/8 | Receiver, cédula, full / differences with a note, photo or signature, location, both times; JPEG / PNG ≤ 5 MB | `DriverConfirmationSchemaTests`, `DriverPagesApiTests`, E2E-ENT |
| E-ENT-4, E-ENT1-01-5 | A full receipt is the POD (P-16) under CONFIRMACION_ENTREGA, which holds only `delivery:driver_confirm` | `DriverConfirmationTests.A_wrong_pin_is_kept_and_a_full_receipt_becomes_the_pod`, `DriverConfirmationSchemaTests.Dispatch_sets_pins…`, E2E-ENT |
| E-ENT-5 | The photo goes to the evidence store; Dispatch sees it after its SHA-256 is checked | `GetDriverEvidence` in E2E-ENT («Ver foto») |
| E-ENT-6, E-ENT1-01-6 | The phone's time within gate out … server + 5 min, otherwise the server's; offline confirmations wait on the phone | `DriverConfirmationTests` (both bounds); the page's IndexedDB queue |
| E-ENT-7 | ADM Cloud retired from 2026-11-01 | Operational (staging becomes production) |
| E-ENT-8 | QR bottom right with its line; none before the gate | E2E-ENT |
| E-ENT1-01-9 | Dispatch's POD first annuls the link: «registrada por Despacho» | `DriverConfirmationTests.A_pod_recorded_by_dispatch_first_annuls_the_link` |
| E-ENT1-01-10 | Differences: the delivery waits, Inicio counts it, the POD form starts from the driver's confirmation and cites it | `DriverConfirmationTests.Differences_wait_for_dispatch…`, E2E-ENT |

E2E-ENT: `web/e2e/delivery-qr-journey.spec.ts`.
