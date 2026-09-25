# Vertical Slice #2 — Pagos a proveedores y bancos (Procure-to-Pay completo)

**Estado: BORRADOR para aprobación de Alexander Rochell.** No es especificación vigente hasta que se apruebe junto con las
decisiones abiertas de la sección 11 (que pasarán a `errata.md` como E-VS2-n). Ningún código de VS#2 se escribe antes.

Fuentes: Architecture v2 §18 (MVP P0: "AP, bancos, conciliación bancaria semiautomática"), v2 §4 (control contra cambio de
cuenta bancaria), v2.1 §12 (`fin.ap_document`, `fin.payment`), §17 (reglas P-06, P-07, P-28), §18 (máquina de Payment), §20
(permisos de pago y cuenta bancaria), §22 (IDM-04), y lo construido en VS#1 (Posting Engine, AP subledger, cierre por
componentes, hash chain, API y UI). Autorizado a iniciar en paralelo por E-VS1-5.

## 1. Alcance y regla de congelamiento

VS#2 cierra el ciclo que VS#1 dejó abierto: hoy una factura de proveedor crea una cuenta por pagar que nadie paga.

| Dentro de VS#2 | Fuera de VS#2 |
| --- | --- |
| Cuentas bancarias propias de la empresa (DOP) | Moneda extranjera y revaluación (P-33) |
| Cuentas bancarias de proveedores, versionadas, con verificación y retención de 72 h | Anticipos a proveedores; notas de crédito de proveedor |
| Pago a proveedor: preparar (Tesorero) → liberar (Controller), aplicado a una o varias facturas del proveedor, total o parcial | Generación de archivos de pago para el banco; integración por API con bancos |
| Anulación antes de liberar; reversa después (transferencia devuelta) | Impresión y chequera (ver D-02) |
| Importación de extractos bancarios, conciliación semiautomática, cargos bancarios | Cobros de clientes (slice de ventas) |
| Conciliación BANK-GL y componente de cierre BANK-REC | Declaración IR-17 y reportes 606 (ver D-03) |
| Antigüedad de cuentas por pagar y propuesta de pagos (consultas) | Presupuesto y flujo de caja |
| API, pantallas en español y pruebas punta a punta como en VS#1 | |

Regla de congelamiento idéntica a VS#1 (Frozen Baseline §1): toda ambigüedad se para y se propone como errata. E-VS1-2 aplica
también aquí: **ningún dato bancario o contable real** hasta cerrar B-02 o una operación en paralelo conciliada.

## 2. Esquema (nuevo)

Convenciones de VS#1: `company_id` + RLS en toda tabla de empresa, FK compuestas, `numeric(19,4)` para montos, append-only
para lo que es evidencia, `version` optimista, `core.state_history` para cada cambio de estado.

```sql
-- Cuentas bancarias propias (una cuenta contable por cuenta bancaria; dimensión BA)
CREATE TABLE fin.bank_account (
  bank_account_id uuid PRIMARY KEY, company_id uuid NOT NULL,
  bank_code text NOT NULL, account_number text NOT NULL, currency char(3) NOT NULL CHECK (currency = 'DOP'),
  gl_account_id uuid NOT NULL,              -- cuenta con rol BANK
  status text NOT NULL CHECK (status IN ('ACTIVE','CLOSED')),
  UNIQUE (company_id, bank_code, account_number)
);

-- Cuentas bancarias de terceros, versionadas (v2 §4: cambio = versión nueva en revisión)
CREATE TABLE md.party_bank_account (
  party_bank_account_id uuid PRIMARY KEY, company_id uuid NOT NULL, party_id uuid NOT NULL,
  version int NOT NULL, bank_code text NOT NULL, account_number text NOT NULL, account_holder text NOT NULL,
  status text NOT NULL CHECK (status IN ('REVIEW','VERIFIED','REJECTED','SUPERSEDED')),
  requested_by uuid NOT NULL, requested_at timestamptz NOT NULL,
  verified_by uuid, verified_at timestamptz,
  verification_evidence text,               -- p. ej. "llamada al 809-… registrado el …, persona …"
  payable_from timestamptz,                 -- verified_at + 72 h
  CHECK (verified_by IS NULL OR verified_by <> requested_by),
  CHECK ((status = 'VERIFIED') = (verified_at IS NOT NULL AND payable_from = verified_at + interval '72 hours')),
  UNIQUE (company_id, party_id, version)
);  -- a lo sumo una VERIFIED por proveedor (índice parcial)

-- Pagos (desembolsos en VS#2; RECEIPT llega con ventas)
CREATE TABLE fin.payment (
  payment_id uuid PRIMARY KEY, company_id uuid NOT NULL,
  direction text NOT NULL CHECK (direction = 'DISBURSEMENT'),
  party_id uuid NOT NULL, bank_account_id uuid NOT NULL, party_bank_account_id uuid,
  method text NOT NULL,                     -- D-02
  amount numeric(19,4) NOT NULL CHECK (amount > 0), currency char(3) NOT NULL CHECK (currency = 'DOP'),
  value_date date NOT NULL, bank_reference text,
  status fin.payment_status NOT NULL,       -- PREPARED, RELEASED, CLEARED, VOIDED, REVERSED
  prepared_by uuid NOT NULL, released_by uuid, posting_event_id uuid, version bigint NOT NULL,
  CHECK (released_by IS NULL OR released_by <> prepared_by),
  UNIQUE (company_id, bank_account_id, direction, bank_reference, amount, value_date)
);

-- Aplicación de un pago a facturas (append-only; desaplicar = fila de reversa)
CREATE TABLE fin.ap_application (
  application_id uuid PRIMARY KEY, company_id uuid NOT NULL,
  payment_id uuid NOT NULL, ap_doc_id uuid NOT NULL,
  amount numeric(19,4) NOT NULL CHECK (amount > 0),
  event_id uuid NOT NULL, reverses_application_id uuid UNIQUE
);

-- Extractos bancarios importados
CREATE TABLE fin.bank_statement (
  statement_id uuid PRIMARY KEY, company_id uuid NOT NULL, bank_account_id uuid NOT NULL,
  period_from date NOT NULL, period_to date NOT NULL,
  opening_balance numeric(19,4) NOT NULL, closing_balance numeric(19,4) NOT NULL,
  file_sha256 bytea NOT NULL, imported_by uuid NOT NULL, imported_at timestamptz NOT NULL,
  UNIQUE (company_id, bank_account_id, file_sha256)
);
CREATE TABLE fin.bank_statement_line (
  line_id uuid PRIMARY KEY, company_id uuid NOT NULL, statement_id uuid NOT NULL, bank_account_id uuid NOT NULL,
  value_date date NOT NULL, direction text NOT NULL CHECK (direction IN ('DEBIT','CREDIT')),
  amount numeric(19,4) NOT NULL CHECK (amount > 0), bank_reference text, description text NOT NULL,
  status text NOT NULL CHECK (status IN ('UNMATCHED','MATCHED','CHARGE_RECOGNIZED')),
  matched_payment_id uuid, charge_event_id uuid,
  UNIQUE (company_id, bank_account_id, direction, bank_reference, amount, value_date)   -- IDM-04
);
```

Cambios a tablas de VS#1: `fin.ap_document` sin cambio de columnas (su `open_amount` baja por aplicación con `version + 1`);
roles de cuenta nuevos `BANK` (control, subledger BANK) y `BANK_CHARGES`; componente de cierre `BANK-REC`.

## 3. Agregados, comandos y eventos

| Agregado | Comandos | Eventos |
| --- | --- | --- |
| BankAccount (propia) | RegisterBankAccount, CloseBankAccount | BankAccountRegistered, BankAccountClosed |
| PartyBankAccount | RequestPartyBankAccount, VerifyPartyBankAccount, RejectPartyBankAccount | PartyBankAccountRequested, …Verified, …Rejected |
| Payment | PrepareSupplierPayment, UpdatePreparedPayment, VoidPayment, ReleaseSupplierPayment, ReversePayment | SupplierPaymentPrepared, …Voided, SupplierPaymentReleased, PaymentReversed, PaymentCleared |
| BankStatement | ImportBankStatement, MatchBankLine, UnmatchBankLine, RecognizeBankCharge | BankStatementImported, BankLineMatched, BankLineUnmatched, BankChargeRecognized |

Consultas: antigüedad de CxP por proveedor y vencimiento; propuesta de pago (facturas POSTED con saldo, vencidas o por vencer,
con cuenta del proveedor pagable); detalle de pago; extractos y líneas pendientes; conciliación BANK-GL.

## 4. Máquinas de estado

**PartyBankAccount:** REVIEW → VERIFIED (Controller, ≠ solicitante, reautenticación, evidencia obligatoria) o REJECTED. Una
nueva versión VERIFIED deja la anterior SUPERSEDED. **Pagable** solo si VERIFIED y `now ≥ payable_from` (v2.1 §11).

**Payment (desembolso):**

| Origen | Comando | Destino | Guardas | Efectos |
| --- | --- | --- | --- | --- |
| — | PrepareSupplierPayment | PREPARED | Proveedor ACTIVE; facturas POSTED del mismo proveedor; Σ aplicaciones = monto; cada aplicación ≤ saldo abierto actual | Sin asiento; no reserva saldo (D-06) |
| PREPARED | UpdatePreparedPayment / VoidPayment | PREPARED / **VOIDED** | Solo el Tesorero | Sin asiento |
| PREPARED | ReleaseSupplierPayment | RELEASED | Liberador ≠ preparador; reautenticación; cuenta del proveedor pagable (si método transferencia); cada aplicación ≤ saldo bajo bloqueo; período BANK-REC y AP-REC abiertos o fecha tardía como en VS#1 | Aplicaciones; `open_amount` −; asiento R-09 |
| RELEASED | (BankLineMatched) | CLEARED | Línea de extracto DEBIT con monto y fecha coherentes | — |
| RELEASED / CLEARED | ReversePayment | **REVERSED** | Controller, reautenticación, motivo; si CLEARED, la línea se desconcilia | Aplicaciones de reversa; `open_amount` +; reversa exacta de R-09 |

**BankStatementLine:** UNMATCHED → MATCHED (con pago) o CHARGE_RECOGNIZED (asiento R-10); MATCHED → UNMATCHED (desconciliar,
reautenticación).

## 5. Transacciones y concurrencia

Plantilla de VS#1 (una transacción por comando, idempotencia por `command_log`, eventos, outbox, historial). Orden de bloqueo
nuevo, compatible con el de VS#1: payment (N3) → ap_document ordenados por id (N8) → bank_account → componentes de período.
La liberación re-valida saldos bajo bloqueo; dos pagos simultáneos sobre la misma factura nunca dejan `open_amount < 0`
(CHECK como última guarda, prueba PAY-05). Importar el mismo extracto dos veces no crea líneas nuevas (UNIQUE, IDM-04).

## 6. Reglas de posteo

| Regla | Evento | Débito | Crédito | Dimensiones | Reversa |
| --- | --- | --- | --- | --- | --- |
| R-09 (v2.1 P-06) | SupplierPaymentReleased | AP_CONTROL (subledger AP por aplicación) | BANK | Co, Pa, BA | Exacta (R-09 inversa) al revertir el pago |
| R-10 (v2.1 P-28) | BankChargeRecognized | BANK_CHARGES | BANK | Co, BA | Exacta |
| R-11 (v2.1 P-07) | WithholdingRemitted | WITHHOLDING_PAYABLE | BANK | Co, BA | Exacta — solo si D-03 lo incluye |

Cada línea AP_CONTROL referencia el `ap_doc_id` aplicado, como en R-04. Cierre: R-09/R-10/R-11 pertenecen al componente
**BANK-REC**; la línea AP además exige AP-REC abierto (ver D-07).

## 7. Permisos y segregación de funciones

Rol nuevo **TESORERO**. Permisos nuevos (se siembran como en VS#1):

| Permiso | Roles | Scope | SoD (no coexiste con) | Reautenticación |
| --- | --- | --- | --- | --- |
| bank_account:manage | Controller | Co | payment:prepare | S |
| party_bank_account:request | Tesorero | Co | party_bank_account:verify | S |
| party_bank_account:verify | Controller | Co | party_bank_account:request | S |
| payment:prepare / void | Tesorero | Co | payment:release | — |
| payment:release | Controller (ver D-05) | Co | payment:prepare; supplier:create; supplier_invoice:register / post | S |
| payment:reverse | Controller | Co | payment:prepare | S |
| bank_statement:import, bank_line:match | Tesorero | Co | — | — |
| bank_line:unmatch, bank_charge:recognize | Contador (Controller en VS#2) | Co | — | S (unmatch) |
| payment:read, bank:read | Tesorero, Cuentas por pagar, Controller, Auditor | Co | — | — |

## 8. Conciliaciones y cierre

| Conciliación | A | B | Bloquea |
| --- | --- | --- | --- |
| BANK-GL | Saldo GL de BANK por cuenta bancaria al corte | Saldo final del extracto ± partidas en tránsito identificadas (pagos RELEASED no conciliados, líneas UNMATCHED) | BANK-REC |
| AP-GL (existente) | Σ `open_amount` | Saldo AP_CONTROL | AP-REC |
| PAY-APPL | Σ aplicaciones vivas por pago | Monto de pagos RELEASED/CLEARED | BANK-REC y AP-REC |

Objetivo de v2 §20 (A6): banco libro − extracto, neto de partidas identificadas = 0.00.

## 9. Pruebas de aceptación

| ID | Given | When | Then |
| --- | --- | --- | --- |
| PAY-01 | Factura AT-02 POSTED (CxP 45,040) y cuenta del proveedor pagable | Preparar y liberar pago por 45,040 | `open_amount` 0; R-09: Dr AP_CONTROL 45,040 / Cr BANK 45,040; AP-GL y PAY-APPL MATCHED |
| PAY-02 | Dos facturas del proveedor | Un pago parcial que cubre la primera y parte de la segunda | Saldos correctos por factura; un asiento con dos líneas AP |
| PAY-03 | Factura con saldo 1,000 | Aplicación de 1,000.01 | Rechazado; nada escrito |
| PAY-04 | Pago PREPARED por el Tesorero | El mismo usuario intenta liberarlo | Rechazado (comando y CHECK) |
| PAY-05 | Dos pagos PREPARED de la misma factura por el total | Liberación simultánea | Uno RELEASED; el otro rechazado; `open_amount` 0 |
| PAY-06 | Cuenta del proveedor verificada hace 71 h | Liberar pago por transferencia | Rechazado; a las 72 h se acepta |
| PAY-07 | Cambio de cuenta del proveedor solicitado | Pago a la cuenta nueva sin verificar | Rechazado; la cuenta anterior VERIFIED sigue pagable hasta ser reemplazada |
| PAY-08 | Pago RELEASED | ReversePayment | REVERSED; reversa exacta de R-09; saldos de facturas restaurados; factura reversable otra vez (VS#1 exige CxP abierta) |
| PAY-09 | Liberación sin reautenticación reciente | Liberar | Rechazado STEP_UP_REQUIRED |
| PAY-10 | Solicitante = verificador de la cuenta | Verificar | Rechazado (comando y CHECK) |
| BNK-01 | Extracto importado | Se importa otra vez (mismo archivo o solapado) | Sin líneas nuevas; reporte de duplicados (IDM-04) |
| BNK-02 | Pago RELEASED y línea DEBIT con misma referencia y monto | Conciliación sugerida y confirmada | Línea MATCHED; pago CLEARED |
| BNK-03 | Línea de comisión bancaria | Reconocer cargo | R-10; línea CHARGE_RECOGNIZED |
| BNK-04 | Mes con pagos y cargos conciliados | BANK-GL | MATCHED; cierre de BANK-REC permitido |
| BNK-05 | Pago RELEASED sin línea de extracto al corte | BANK-GL | Partida en tránsito listada; MATCHED si el saldo cuadra neto de ella |
| E2E-01 | Flujo de VS#1 + pago + extracto | Por la API y por la UI | Todo lo anterior, punta a punta |
| INV-P | Secuencias aleatorias de preparar/liberar/revertir/conciliar | Después de cada paso | Invariantes (Σ aplicaciones, saldos ≥ 0, AP-GL, BANK-GL) — pruebas por propiedades como E-VS1-3 |

## 10. Plan de PRs (propuesto)

| PR | Contenido | Pruebas |
| --- | --- | --- |
| VS2-01 | Esquema: cuentas propias, cuentas de terceros versionadas, roles BANK/BANK_CHARGES, rol TESORERO y permisos | Esquema, RLS, SoD, PAY-10 |
| VS2-02 | Cuentas: registrar, solicitar, verificar, rechazar; regla de 72 h | PAY-06, PAY-07 |
| VS2-03 | Pago: preparar, actualizar, anular, liberar (R-09) con aplicaciones | PAY-01…05, PAY-09 |
| VS2-04 | Reversa de pago | PAY-08 |
| VS2-05 | Extractos: importar, conciliar, cargos (R-10) | BNK-01…03 |
| VS2-06 | BANK-GL, PAY-APPL y componente BANK-REC en el cierre | BNK-04, BNK-05 |
| VS2-07 | API, consultas (antigüedad, propuesta), OpenAPI | E2E-01 por API |
| VS2-08 | Pantallas (Tesorería) y recorrido Playwright | E2E-01 por UI |
| VS2-09 | Pruebas por propiedades, concurrencia y trazabilidad de aceptación | INV-P, CC de pagos |

Cada PR sigue CLAUDE.md §3 (migración forward-only, pruebas, documentación, CI verde, tu aprobación).

## 11. Decisiones abiertas (necesito tu aprobación antes de congelar)

| # | Decisión | Recomendación |
| --- | --- | --- |
| D-01 | ¿Quién registra las cuentas bancarias **propias**? | Comando del Controller con reautenticación (no la CLI de despliegue), porque cambian en el tiempo |
| D-02 | Métodos de pago en VS#2 | Solo **transferencia** (la retención de 72 h aplica). Cheques y su numeración en un slice posterior |
| D-03 | ¿Incluir el pago de retenciones a la DGII (R-11)? | **No** en VS#2: sin IR-17 ni fuentes oficiales (A-02) el monto no está respaldado; queda para el slice fiscal |
| D-04 | Formato de extractos | CSV genérico con mapeo por banco (fecha, referencia, monto, débito/crédito, descripción). Necesito saber **con qué bancos opera la empresa** para el primer mapeo |
| D-05 | ¿Quién libera pagos? v2.1 dice "Controller / Director", y no existe el rol Director | Controller en VS#2; límites por monto y rol Director cuando la política de aprobación de pagos exista (A-01) |
| D-06 | ¿Preparar un pago reserva el saldo de las facturas? | No; la liberación re-valida bajo bloqueo (PAY-05). Evita saldos "retenidos" por pagos que nunca se liberan |
| D-07 | Período del pago | Fecha de negocio = fecha valor; BANK-REC y AP-REC deben estar abiertos o aplica el registro tardío de VS#1 |
| D-08 | 72 h: ¿horas calendario o hábiles? | Calendario (regla simple y verificable); desde la verificación, no desde la solicitud |
| D-09 | Pagos parciales y a varias facturas | Sí, del mismo proveedor; un pago = un proveedor |
| D-10 | Datos reales | Igual que E-VS1-2: ninguno hasta B-02 o paralelo conciliado |
