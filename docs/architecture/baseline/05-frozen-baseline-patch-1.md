# Architecture v2.1.1 — Frozen Baseline Patch 1

Sep 23, 2026 · @Alexander Rochell

## 1. Alcance del parche

Este parche aplica P-1 a P-8 sobre *Architecture v2.1.1 — Errata & Frozen Baseline* y **reemplaza normativamente** las partes indicadas. Todo lo no mencionado sigue vigente sin cambios. No agrega capacidades: las únicas piezas nuevas (tabla de integridad, solicitud de reapertura, permisos de segundo aprobador, regla de posteo de fixture) son el mínimo necesario para que P-2, P-8 y las pruebas existentes sean implementables.

| Sección de v2.1.1 | Estado tras el parche | Reemplazada por |
| --- | --- | --- |
| E-1 (tabla de máquina contable), E-11 | Parcialmente reemplazada | §2 P-1, P-2 |
| E-3 (flujo de command\_log) | Reemplazada | §2 P-3 |
| E-4 (invariante de enlace) | Reemplazada | §2 P-1 |
| §8 Schema (1/2), §9 Schema (2/2) | Tablas afectadas reemplazadas | §4 |
| §11 State machines (GR, SI, Journal, Period) | Reemplazadas | §5 |
| §12 Transaction boundaries (plantilla, T-02, T-03, T-08, T-10, T-11, T-13) | Reemplazadas | §5 |
| §13 Posting rules (R-02, R-07, R-T1 nuevo de fixture) | Reemplazadas | §6 |
| §14 Permissions (reopen, role:assign) | Filas reemplazadas | §3 P-8 |
| §15 Acceptance tests (AT-05, AT-06, SI-06, SI-08, y nuevos) | Reemplazados / añadidos | §7 |
| §17 Plan de PRs (PR-01, PR-02, PR-05, PR-15, PR-16) | Contenido ajustado, mismo orden | §5 (nota final) |

Regla de precedencia: si un texto de v2.1.1 contradice este parche, prevalece el parche.

## 2. P-1 a P-4

### P-1 — Atomicidad Inventory Value ↔ GL

**Regla.** En VS#1 ningún comando que crea `inv_value_entry` puede hacer COMMIT sin sus `gl_entry`. Si falta regla de posteo ACTIVE, mapeo rol → cuenta ACTIVE, período abierto alcanzable para la `posting_date`, determinación fiscal válida o cualquier otro requisito contable, el comando **completo hace ROLLBACK**: no existe GR, no cambia stock, no existe value entry ni journal, no existe command\_log; `obs.request_log` registra `REJECTED_DOMAIN` con código `POSTING_PREREQUISITE_MISSING` y detalle (regla, rol o período faltante).

**Preflight.** El Posting Engine expone `ValidatePrerequisites(event_type, contexto)` que se ejecuta en el paso 7 de la plantilla (antes de cualquier escritura) para fallar rápido con un mensaje claro. El preflight no sustituye la garantía: si una condición cambia entre el preflight y el posteo, el posteo lanza excepción y la TX hace rollback igual.

**POSTING\_BLOCKED** deja de existir en VS#1. Los documentos del slice que contabilizan solo pueden estar en `accounting_status ∈ {POSTED, REVERSED}` (GR, GoodsReceiptReversal, ReceiptCorrection POSTED) o `{NOT_POSTED, POSTED, REVERSED}` (SupplierInvoice, que existe en DRAFT antes de postear). El valor POSTING\_BLOCKED queda reservado en el enum para documentos fuera del slice que no crean valor de inventario (E-1 Invoice) y se prohíbe por CHECK en las tablas del slice.

**Invariantes obligatorios después de cada COMMIT** (constraint triggers diferidos + conciliación nocturna):

1. Cada `inv_value_entry` tiene **exactamente un** `gl_entry` que lo referencia, con el mismo importe en el lado de inventario (`gl_entry.inv_value_entry_id` UNIQUE + constraint trigger diferido de existencia e igualdad de importe). Toda línea de GL con rol de inventario referencia un value entry.
2. **Inventory Value Subledger = Inventory GL** por empresa × área de valuación × ítem: Σ `inv_value_entry.amount` = Σ (débito − crédito) de `gl_entry` con rol RAW\_MATERIAL y ese plant/item. Verificado al COMMIT para las posiciones tocadas por la TX (trigger diferido sobre las claves afectadas) y globalmente cada noche.

Para que el invariante 1 sea estrictamente 1 a 1 también en reversas: **todo journal REVERSAL o VALUATION\_REALLOCATION que toca inventario crea sus propios value entries** (inversos exactos o de reasignación), y cada uno queda ligado a su única línea de GL. La generación n + 1 de un RepostEvent también crea value entries nuevos por los mismos importes. El valor neto del subledger resulta de la suma de todos ellos.

**Consecuencia sobre RepostEvent.** Revertir por separado el journal de un GR dejaría un value entry sin GL efectivo y violaría P-1. Por eso `RepostEvent` pasa a ser **un solo comando atómico**: journal REVERSAL exacto de la generación n + journal AUTO de la generación n + 1 con el mapeo corregido, en la misma TX. No existe "revertir journal" como comando aislado para documentos con valor de inventario.

### P-2 — Accounting status ≠ Integrity seal

| Concepto | Significado | Quién lo escribe | Cuándo |
| --- | --- | --- | --- |
| `accounting_status = POSTED` | El journal fue confirmado (COMMIT), está balanceado y pasó sus constraints | Posting Engine | En la TX del comando |
| `integrity_status` | `PENDING_SEAL` → `SEALED` o `SEAL_ERROR` | Insertado como PENDING\_SEAL en la TX del comando; actualizado **solo** por el sellador | Asíncrono |

- Una operación comercial nunca espera al sellador.
- `integrity_status` vive en `audit.integrity_state` (una fila por ledger group: journal, grupo de quantity entries, grupo de value entries, evento), no en los documentos ni en los ledgers.
- **Inventory Close y Accounting Close exigen** `integrity_status = SEALED` para todos los ledger groups con `posting_date` en el período (y todos los eventos con `business_date` en el período). Un `SEAL_ERROR` bloquea el cierre y genera alerta CRITICAL.

**E-11 corregido:** "Un hecho está contabilizado si y solo si existe el `gl_journal` con `source_event_id` del hecho, confirmado y balanceado, y el documento tiene `accounting_status = POSTED`. El sellado es evidencia de **integridad**, no de contabilización; se exige para cerrar, no para operar."

### P-3 — Resultado del command\_log

Flujo dentro de la única transacción del comando:

1. `INSERT core.command_log (command_id, …, result_ref = <preasignado>, result_payload = NULL, committed_at = NULL)`. Si la clave ya existe, la sesión espera en el índice único; tras el COMMIT del primero, el INSERT falla, se hace ROLLBACK y se devuelve `result_ref`/`result_payload` de la fila existente.
2. Ejecutar el comando.
3. Inmediatamente antes de COMMIT: `UPDATE core.command_log SET result_payload = …, committed_at = clock_timestamp() WHERE command_id = …` (única actualización permitida).
4. COMMIT.

Garantías en base de datos:

- Trigger `BEFORE UPDATE`: permite la actualización solo si `OLD.result_payload IS NULL`, `OLD.committed_at IS NULL`, cambian únicamente esas dos columnas, y la fila fue insertada por la transacción actual (`OLD.xmin = pg_current_xact_id()::xid`). Cualquier otro UPDATE o DELETE se rechaza. Por esto los comandos no usan SAVEPOINT (cambiaría el xmin).
- Constraint trigger diferido: al COMMIT, `result_payload IS NOT NULL AND committed_at IS NOT NULL`; si no, la TX falla.
- Un lector concurrente nunca ve la fila con NULL: no es visible hasta el COMMIT.

### P-4 — Semántica de reversa

**Regla.** Un journal `REVERSAL` es el **inverso matemático exacto** del journal original: mismas cuentas, mismas dimensiones, mismos importes con débito y crédito intercambiados, mismo `rule_line_code`. Nunca se recalcula con el estado actual. Si el original tenía value entries, el REVERSAL crea value entries inversos exactos (`value_type = REVERSAL_OF`, importe = −original, referencia al value entry original).

Si el estado actual del inventario exige una distribución distinta (porque parte del stock afectado ya salió o el área quedaría con residuo), se agrega en la **misma TX y bajo el mismo evento** un segundo journal `VALUATION_REALLOCATION`, calculado con el estado actual, con sus propios value entries de tipo `VALUATION_REALLOCATION`. Ambos journals juntos deben dejar Inventory Value Subledger = Inventory GL y representar correctamente la porción en stock vs consumida.

Aplicación en VS#1:

| Comando | Journal A (REVERSAL exacto) | Journal B (VALUATION\_REALLOCATION) |
| --- | --- | --- |
| ReverseGoodsReceipt | Inverso exacto de R-01 (Dr GRNI Q·P / Cr RAW\_MATERIAL Q·P) | Solo si tras A el área queda con `qty = 0` y `value ≠ 0`, o con `value < 0`: lleva el residuo a PURCHASE\_PRICE\_VARIANCE (R-02B) |
| ReverseSupplierInvoice | Inverso exacto de R-04 y R-05 originales | Solo si la porción de la diferencia de precio aún en stock cambió: reasigna (s − s′)·D entre RAW\_MATERIAL y PURCHASE\_PRICE\_VARIANCE (R-07B) |
| RepostEvent | Inverso exacto de la generación n | No aplica; la generación n + 1 es un journal AUTO normal |

Los journals B no son reversas: son valoración corriente y se explican con su propia plantilla.

## 3. P-5 a P-8

### P-5 — Claves foráneas seguras por empresa

RLS protege lecturas y escrituras de la aplicación, pero no impide que una fila de la empresa A apunte a una fila de la empresa B. v2.1.1 lo permitía.

**Regla.** Toda entidad company-scoped declara `UNIQUE (company_id, <pk>)`, y toda relación entre entidades company-scoped usa **FK compuesta** `(company_id, fk_id) → target(company_id, target_id)`. Las tablas hijas que no tenían `company_id` (líneas de documento, match\_result, tax\_determination\_line, gl\_entry ya lo tenía) lo reciben como `NOT NULL`.

| Clase | Tablas | company\_id |
| --- | --- | --- |
| Global verdadera | `md.uom`, `iam.permission`, `iam.role`, `iam.role_permission`, `iam.sod_rule`, `fin.posting_rule`, `fin.posting_rule_version`, `tax.fiscal_rule`, `tax.fiscal_rule_version`, `tax.fiscal_rule_source`, `tax.fiscal_rule_version_source`, `tax.fiscal_rule_test_run`, `acc.accounting_policy` | No |
| Raíz de tenant | `md.company` (nueva: company\_id, rnc, nombre) | Es la PK |
| Company-scoped | Todo lo demás del slice | Sí, con FK compuestas |

Casos particulares de VS#1: `inv_quantity_entry.legal_title_holder` y `accounting_owner` son polimórficos (empresa o party); en VS#1 solo pueden ser la propia empresa: `CHECK (legal_title_holder = company_id AND accounting_owner = company_id)` en el slice. `valuation_area_id` pasa a ser entidad (`md.valuation_area`) con FK compuesta desde plant, stock y valuación. `iam.role_assignment` referencia `md.company`.

**Nuevo test TEN-01** (§7): la empresa A intenta crear una OC con proveedor, ítem y ubicación de la empresa B, **saltándose la capa de aplicación** (INSERT directo con el rol de aplicación y RLS configurado para A) → la base rechaza por FK compuesta.

### P-6 — Unicidad temporal de versiones ACTIVE

| Tabla | Exclusion constraint |
| --- | --- |
| `fin.account_role_map` | `EXCLUDE USING gist (company_id WITH =, account_role WITH =, (COALESCE(item_category,'*')) WITH =, daterange(effective_from, effective_to, '[)') WITH &&) WHERE (status = 'ACTIVE')` |
| `fin.posting_rule_version` | `EXCLUDE USING gist (posting_rule_id WITH =, daterange(effective_from, effective_to, '[)') WITH &&) WHERE (status = 'ACTIVE')` |
| `acc.accounting_policy_version` | (ya existía) `EXCLUDE USING gist (policy_code WITH =, company_id WITH =, daterange(…) WITH &&) WHERE (status = 'ACTIVE')` |
| `tax.fiscal_rule_version` | `EXCLUDE USING gist (rule_id WITH =, daterange(effective_from, effective_to, '[)') WITH &&) WHERE (status = 'ACTIVE')` |
| `md.uom_conversion` | `EXCLUDE USING gist (company_id WITH =, item_id WITH =, from_uom WITH =, to_uom WITH =, daterange(effective_from, effective_to, '[)') WITH &&)` (siempre, no solo ACTIVE: una conversión no tiene estados) |

Convención: `effective_to` es exclusivo y NULL = abierto; `CHECK (effective_to IS NULL OR effective_to > effective_from)` en todas.

**Extensión requerida:** `btree_gist` (para `=` sobre uuid/text dentro de índices GiST). Se crea en la migración de PR-01: `CREATE EXTENSION IF NOT EXISTS btree_gist;`. El entorno de nube elegido en B-03 debe permitirla (PostgreSQL administrado de los principales proveedores la incluye); se verifica en el criterio de cierre de PR-01.

### P-7 — Procedencia de pruebas fiscales

- `tax.fiscal_rule_source.environment text NOT NULL CHECK (environment IN ('TEST','PRODUCTION'))`.
- `tax.fiscal_rule_version.definition_hash bytea NOT NULL`, calculado por trigger `BEFORE INSERT OR UPDATE` como `sha256(convert_to(definition::text, 'UTF8'))`. La forma canónica es la salida textual de `jsonb` de PostgreSQL (orden de claves determinístico); la aplicación nunca calcula este hash por su cuenta.
- `tax.fiscal_rule_test_run.definition_hash bytea NOT NULL`: el runner de pruebas copia el `definition_hash` de la versión que probó.
- Gate de activación (trigger al pasar a ACTIVE):
  1. existe un test\_run con `passed = true` y `test_run.definition_hash = fiscal_rule_version.definition_hash`;
  2. si `current_setting('app.environment') = 'PRODUCTION'`, existe al menos una fuente vinculada con `environment = 'PRODUCTION'`;
  3. `activated_by <> configured_by`.
- Cualquier cambio de `definition` recalcula el hash, invalida automáticamente todos los test runs anteriores y devuelve la versión a DRAFT si no estaba ACTIVE (una versión ACTIVE no se edita: se crea otra).

### P-8 — Segundo aprobador existente en IAM

v2.1.1 exigía "Controller + Director" para reabrir un componente y "segundo admin o Director" para asignar roles, pero no existía ese actor en IAM.

| Nuevo permiso | Uso | Rol que lo recibe en VS#1 |
| --- | --- | --- |
| `period_component:second_approve` | Segunda aprobación de ReopenComponent | Rol nuevo **Segundo aprobador de cierre** (asignado a Dirección) |
| `role:second_approve` | Segunda aprobación de AssignRole / RevokeRole | Rol nuevo **Segundo aprobador de seguridad** (asignado a Dirección) |

Mecánica (mínima, sin capacidad nueva): la reapertura y la asignación de roles pasan a ser solicitud + segunda aprobación.

- `fin.reopen_request(company_id, request_id, period_id, component, reason, requested_by, requested_at, second_approved_by, second_approved_at, status)` con `CHECK (second_approved_by <> requested_by)`; `ReopenComponent` exige `period_component:reopen` a quien solicita y `ApproveReopen` exige `period_component:second_approve`; el componente cambia a REOPENED en la TX de `ApproveReopen`.
- `iam.role_assignment_request` con la misma forma para roles; `granted_by` (solicitante con `role:assign`) ≠ `second_approved_by` (con `role:second_approve`) ≠ `user_id` beneficiario.
- SoD: `period_component:reopen` ↔ `period_component:second_approve` y `role:assign` ↔ `role:second_approve` no pueden coexistir en el mismo usuario.

Filas reemplazadas de §14 de v2.1.1:

| Permiso | Roles | Scope | SoD | Reauth |
| --- | --- | --- | --- | --- |
| period\_component:reopen | Controller | Co | period\_component:second\_approve | S |
| period\_component:second\_approve | Segundo aprobador de cierre | Co | period\_component:reopen | S |
| role:assign / revoke | Administrador de seguridad | Grupo | Cualquier permiso transaccional; role:second\_approve | S |
| role:second\_approve | Segundo aprobador de seguridad | Grupo | role:assign | S |

## 4. Schema reemplazado

Se expresa como diferencia normativa sobre §8–§9 de v2.1.1; al implementar, PR-02…PR-16 crean directamente la forma final (no se migra desde la versión anterior porque nunca se desplegó).

### 4.1 Tenant y claves compuestas (P-5)

```sql
-- PR-01
CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE md.company (company_id uuid PRIMARY KEY, rnc text NOT NULL UNIQUE, legal_name text NOT NULL);
CREATE TABLE md.valuation_area (
  company_id uuid NOT NULL REFERENCES md.company, valuation_area_id uuid PRIMARY KEY,
  code text NOT NULL, UNIQUE (company_id, valuation_area_id), UNIQUE (company_id, code)
);

-- Patrón aplicado a TODA tabla company-scoped del slice:
--   company_id uuid NOT NULL REFERENCES md.company
--   UNIQUE (company_id, <pk>)
-- y TODA FK entre tablas company-scoped es compuesta. Lista completa:
```

| Tabla hija | FK compuesta → tabla padre |
| --- | --- |
| md.plant | (company\_id, valuation\_area\_id) → md.valuation\_area |
| md.location | (company\_id, plant\_id) → md.plant |
| md.item, md.party | (company\_id) → md.company |
| md.uom\_conversion (+ company\_id) | (company\_id, item\_id) → md.item |
| pur.purchase\_order | (company\_id, party\_id) → md.party; (company\_id, plant\_id) → md.plant |
| pur.purchase\_order\_line (+ company\_id) | (company\_id, po\_id) → purchase\_order; (company\_id, item\_id) → md.item |
| pur.goods\_receipt | (company\_id, po\_id) → purchase\_order; (company\_id, location\_id) → md.location; (company\_id, posting\_event\_id) → core.domain\_event |
| pur.goods\_receipt\_line (+ company\_id) | (company\_id, gr\_id) → goods\_receipt; (company\_id, po\_line\_id) → purchase\_order\_line; (company\_id, lot\_id) → inv.lot |
| pur.goods\_receipt\_reversal | (company\_id, reversed\_gr\_id) → goods\_receipt; (company\_id, posting\_event\_id) → domain\_event |
| pur.receipt\_correction | (company\_id, gr\_id), (company\_id, gr\_line\_id), (company\_id, posting\_event\_id) |
| pur.supplier\_invoice | (company\_id, party\_id) → md.party; (company\_id, tax\_determination\_id) → tax.tax\_determination; (company\_id, posting\_event\_id) |
| pur.supplier\_invoice\_line (+ company\_id) | (company\_id, si\_id); (company\_id, po\_line\_id) |
| pur.match\_result (+ company\_id) | (company\_id, si\_line\_id) |
| tax.tax\_determination\_line (+ company\_id) | (company\_id, determination\_id) |
| inv.lot | (company\_id, item\_id) |
| inv.inv\_quantity\_entry | (company\_id, plant\_id), (company\_id, location\_id), (company\_id, item\_id), (company\_id, lot\_id), (company\_id, source\_event\_id) |
| inv.inv\_value\_entry | (company\_id, valuation\_area\_id), (company\_id, item\_id), (company\_id, quantity\_entry\_id), (company\_id, source\_event\_id), (company\_id, reverses\_value\_entry\_id) |
| inv.stock\_balance | (company\_id, location\_id), (company\_id, item\_id), (company\_id, lot\_id) |
| inv.valuation\_balance | (company\_id, valuation\_area\_id), (company\_id, item\_id) |
| fin.account\_role\_map | (company\_id, account\_id) → fin.account |
| fin.gl\_journal | (company\_id, period\_id) → fin.period; (company\_id, source\_event\_id) → domain\_event; (company\_id, reverses\_journal\_id) → gl\_journal |
| fin.gl\_entry | (company\_id, journal\_id); (company\_id, account\_id); (company\_id, inv\_value\_entry\_id); (company\_id, source\_event\_id); (company\_id, plant\_id); (company\_id, item\_id); (company\_id, party\_id) |
| fin.ap\_document | (company\_id, party\_id); (company\_id, source\_doc\_id) → pur.supplier\_invoice |
| core.domain\_event | (company\_id, command\_id) → core.command\_log |
| core.document\_link, core.state\_history, core.outbox (+ company\_id), core.inbox (+ company\_id) | (company\_id, event\_id) → domain\_event |
| iam.role\_assignment | (company\_id) → md.company |
| audit.integrity\_state, audit.ledger\_seal | (company\_id) → md.company |

### 4.2 Tablas y columnas cambiadas o nuevas

```sql
-- P-3: command_log
CREATE TABLE core.command_log (
  company_id uuid NOT NULL REFERENCES md.company, command_id uuid PRIMARY KEY,
  command_type text NOT NULL, idempotency_key text NOT NULL,
  session_id uuid NOT NULL REFERENCES iam.session,
  result_ref uuid NOT NULL,                  -- preasignado
  result_payload jsonb,                      -- NULL hasta el paso 3
  committed_at timestamptz,
  UNIQUE (company_id, command_id),
  UNIQUE (company_id, command_type, idempotency_key),
  CHECK ((result_payload IS NULL) = (committed_at IS NULL))
);
-- TRIGGER command_log_update_guard BEFORE UPDATE / DELETE (P-3)
-- CONSTRAINT TRIGGER command_log_completed DEFERRABLE INITIALLY DEFERRED: result_payload NOT NULL al COMMIT

-- P-4: value entries de reversa y reasignación
ALTER TYPE inv.value_type ADD VALUE 'REVERSAL_OF';
ALTER TYPE inv.value_type ADD VALUE 'VALUATION_REALLOCATION';
ALTER TABLE inv.inv_value_entry ADD COLUMN reverses_value_entry_id uuid;  -- FK compuesta
ALTER TABLE inv.inv_value_entry ADD CONSTRAINT reversal_link
  CHECK ((value_type = 'REVERSAL_OF') = (reverses_value_entry_id IS NOT NULL));
CREATE UNIQUE INDEX one_reversal_per_value_entry ON inv.inv_value_entry (reverses_value_entry_id)
  WHERE reverses_value_entry_id IS NOT NULL;
ALTER TABLE inv.inv_value_entry DROP CONSTRAINT <check quantity_entry_id IS NOT NULL OR …>;
ALTER TABLE inv.inv_value_entry ADD CHECK (quantity_entry_id IS NOT NULL
  OR value_type IN ('PRICE_ADJUSTMENT','RESIDUAL_ADJUSTMENT','VALUATION_REALLOCATION','REVERSAL_OF'));

-- P-1/P-4: journals
ALTER TABLE fin.gl_journal DROP CONSTRAINT <journal_type check>;
ALTER TABLE fin.gl_journal ADD CHECK (journal_type IN ('AUTO','REVERSAL','VALUATION_REALLOCATION'));
ALTER TABLE fin.gl_journal ADD CHECK ((journal_type = 'REVERSAL') = (reverses_journal_id IS NOT NULL));
-- gl_entry.inv_value_entry_id se mantiene UNIQUE (P-1, invariante 1)
-- CONSTRAINT TRIGGER value_gl_link DEFERRABLE INITIALLY DEFERRED:
--   para cada inv_value_entry insertado en la TX: existe 1 gl_entry con igual importe en el lado de inventario
--   para cada gl_entry con account_role = 'RAW_MATERIAL': inv_value_entry_id NOT NULL
-- CONSTRAINT TRIGGER inventory_subledger_equals_gl DEFERRABLE INITIALLY DEFERRED:
--   para cada (company, valuation_area, item) tocado: Σ value = Σ(debit − credit) RAW_MATERIAL

-- P-1: estados contables del slice
ALTER TABLE pur.goods_receipt ADD CHECK (accounting_status IN ('POSTED','REVERSED'));
ALTER TABLE pur.goods_receipt_reversal ADD CHECK (accounting_status = 'POSTED');
ALTER TABLE pur.receipt_correction ADD CHECK (
  (document_status = 'POSTED' AND accounting_status = 'POSTED') OR
  (document_status <> 'POSTED' AND accounting_status = 'NOT_POSTED'));
ALTER TABLE pur.supplier_invoice ADD CHECK (accounting_status IN ('NOT_POSTED','POSTED','REVERSED'));
ALTER TABLE pur.supplier_invoice ADD CHECK (NOT (document_status = 'VOIDED' AND accounting_status <> 'NOT_POSTED'));
ALTER TABLE pur.supplier_invoice ADD CHECK (NOT (document_status IN ('DRAFT','MATCH_EXCEPTION') AND accounting_status <> 'NOT_POSTED'));
ALTER TABLE pur.supplier_invoice ADD CHECK ((document_status = 'REVERSED') = (accounting_status = 'REVERSED'));

-- P-2: integridad
CREATE TABLE audit.integrity_state (
  company_id uuid NOT NULL REFERENCES md.company,
  ledger text NOT NULL, group_ref uuid NOT NULL, posting_date date, business_date date,
  integrity_status text NOT NULL CHECK (integrity_status IN ('PENDING_SEAL','SEALED','SEAL_ERROR')),
  ledger_sequence bigint, error_detail text, updated_at timestamptz NOT NULL,
  PRIMARY KEY (company_id, ledger, group_ref),
  CHECK ((integrity_status = 'SEALED') = (ledger_sequence IS NOT NULL))
);
CREATE INDEX integrity_pending ON audit.integrity_state (company_id, posting_date) WHERE integrity_status <> 'SEALED';
-- INSERT (PENDING_SEAL) por el rol de aplicación en la TX del comando; UPDATE solo por el rol del sellador (GRANT)

-- P-6: exclusion constraints (ver §3) + CHECK effective_to > effective_from en las cinco tablas

-- P-7: procedencia fiscal
ALTER TABLE tax.fiscal_rule_source ADD COLUMN environment text NOT NULL CHECK (environment IN ('TEST','PRODUCTION'));
ALTER TABLE tax.fiscal_rule_version ADD COLUMN definition_hash bytea NOT NULL;   -- trigger BEFORE INSERT/UPDATE
ALTER TABLE tax.fiscal_rule_test_run ADD COLUMN definition_hash bytea NOT NULL;
-- TRIGGER activate_gate (P-7, condiciones 1–3)

-- P-8: segundo aprobador
INSERT INTO iam.permission VALUES ('period_component:second_approve'), ('role:second_approve');
INSERT INTO iam.sod_rule VALUES ('period_component:reopen','period_component:second_approve'), ('role:assign','role:second_approve');
CREATE TABLE fin.reopen_request (
  company_id uuid NOT NULL, request_id uuid PRIMARY KEY, period_id uuid NOT NULL,
  component text NOT NULL, reason text NOT NULL,
  requested_by uuid NOT NULL, requested_at timestamptz NOT NULL,
  second_approved_by uuid, second_approved_at timestamptz,
  status text NOT NULL CHECK (status IN ('REQUESTED','APPROVED','REJECTED')),
  FOREIGN KEY (company_id, period_id) REFERENCES fin.period (company_id, period_id),
  CHECK (second_approved_by IS NULL OR second_approved_by <> requested_by),
  CHECK ((status = 'APPROVED') = (second_approved_by IS NOT NULL))
);
CREATE TABLE iam.role_assignment_request (
  company_id uuid NOT NULL REFERENCES md.company, request_id uuid PRIMARY KEY,
  user_id uuid NOT NULL, role_id uuid NOT NULL, plant_id uuid, action text NOT NULL CHECK (action IN ('ASSIGN','REVOKE')),
  requested_by uuid NOT NULL, second_approved_by uuid, status text NOT NULL,
  CHECK (requested_by <> user_id),
  CHECK (second_approved_by IS NULL OR (second_approved_by <> requested_by AND second_approved_by <> user_id))
);

-- Close gate (P-2): la función de cierre verifica
--   NOT EXISTS (SELECT 1 FROM audit.integrity_state WHERE company_id = :c
--               AND integrity_status <> 'SEALED' AND posting_date BETWEEN :start AND :end)
```

## 5. State machines y transaction boundaries reemplazados

### 5.1 State machines (reemplaza §11.2, §11.4, §11.5, §11.7)

**GoodsReceipt**

| Origen | Comando | document\_status | accounting\_status | Guardas |
| --- | --- | --- | --- | --- |
| — | PostGoodsReceipt | POSTED | POSTED | Preflight contable OK; si falla en cualquier punto → ROLLBACK, el GR no existe |
| POSTED | ReverseGoodsReceipt | **REVERSED** | **REVERSED** | Guardas E-8 5.2 + preflight |
| POSTED / CORRECTED | ReceiptCorrection POSTED | CORRECTED | POSTED (sin cambio) | — |

Eliminadas: POSTING\_BLOCKED y la transición RepostEvent desde POSTING\_BLOCKED.

**SupplierInvoice**

| Origen | Comando | document\_status | accounting\_status | Guardas / efecto |
| --- | --- | --- | --- | --- |
| — | RegisterSupplierInvoice | DRAFT | NOT\_POSTED | Sin cambios |
| DRAFT | MatchSupplierInvoice | MATCHED / MATCH\_EXCEPTION | NOT\_POSTED | Sin cambios |
| MATCH\_EXCEPTION | ApproveMatchException / re-match | MATCHED | NOT\_POSTED | Sin cambios |
| MATCHED (NOT\_POSTED) | PostSupplierInvoice | MATCHED | POSTED | Preflight contable y fiscal; si falla → ROLLBACK: la factura queda exactamente como estaba (MATCHED, NOT\_POSTED) y request\_log REJECTED\_DOMAIN |
| DRAFT / MATCH\_EXCEPTION / MATCHED (NOT\_POSTED) | VoidSupplierInvoice | **VOIDED** | NOT\_POSTED | Sin cambios |
| MATCHED (POSTED) | ReverseSupplierInvoice | **REVERSED** | **REVERSED** | Journal A exacto + Journal B si aplica (P-4) |

**Journal**

| Tipo | Se crea en | Relación |
| --- | --- | --- |
| AUTO | TX del comando de negocio; generación n | — |
| REVERSAL | TX de ReverseGoodsReceipt, ReverseSupplierInvoice o RepostEvent | Inverso exacto; `reverses_journal_id` UNIQUE |
| VALUATION\_REALLOCATION | Misma TX y mismo evento que un REVERSAL, solo si P-4 lo exige | Independiente; calculado con estado actual |

`RepostEvent` (único comando que re-contabiliza): en una sola TX crea REVERSAL exacto de la generación n (con value entries REVERSAL\_OF) y AUTO generación n + 1 (con value entries nuevos). Guarda: la generación n no está revertida; el documento está POSTED; el mapeo o regla vigente difiere del usado. No existe "revertir un journal" aislado.

**Period component**

| Origen | Comando | Destino | Guardas |
| --- | --- | --- | --- |
| OPEN | CloseComponent | CLOSED | Conciliaciones del componente sin ERROR **y** ningún ledger group del período con `integrity_status <> SEALED` |
| CLOSED | RequestReopen | CLOSED (con reopen\_request REQUESTED) | `period_component:reopen`; motivo; reauth |
| CLOSED | ApproveReopen | REOPENED | `period_component:second_approve`; aprobador ≠ solicitante; reauth |
| CLOSED | RejectReopen | CLOSED | Segundo aprobador |
| REOPENED | CloseComponent | CLOSED | Mismas guardas de cierre |

**RoleAssignment**: RequestRoleChange (role:assign) → ApproveRoleChange (role:second\_approve, ≠ solicitante, ≠ beneficiario) → la asignación se crea en la TX de la aprobación.

### 5.2 Plantilla de transacción (reemplaza §12)

1. Generar en memoria todos los IDs, incluido `result_ref`.
2. `BEGIN` (READ COMMITTED); `SET LOCAL app.company_id`, `app.session_id`.
3. `INSERT core.command_log (…, result_ref, result_payload = NULL, committed_at = NULL)`; si duplica → esperar, ROLLBACK y devolver la fila existente.
4. `pg_advisory_xact_lock_shared` del período/componente.
5. Bloqueos de negocio en orden global.
6. Guardas de dominio.
7. **Preflight contable/fiscal** (`ValidatePrerequisites`): regla ACTIVE, mapeo ACTIVE, período abierto alcanzable, reglas fiscales ACTIVE. Falla → excepción de dominio → ROLLBACK.
8. `INSERT core.domain_event`.
9. Documento, `core.state_history`.
10. `inv_quantity_entry` → `inv_value_entry`.
11. Posting Engine: `gl_journal` → `gl_entry` (cualquier excepción → ROLLBACK).
12. Proyecciones.
13. `INSERT audit.integrity_state` PENDING\_SEAL por cada ledger group y evento creado.
14. `INSERT core.outbox`.
15. `UPDATE core.command_log SET result_payload, committed_at` (única actualización).
16. `COMMIT` → constraint triggers diferidos: balance del journal, value ↔ GL 1 a 1, subledger = GL por posición tocada, command\_log completo.
17. Fuera de la TX: `obs.request_log` (SUCCEEDED, DUPLICATE\_RETURNED, REJECTED\_DOMAIN, CONFLICT\_RETRYABLE o FAILED\_TECHNICAL).

### 5.3 Transacciones afectadas

| ID | Comando | Cambio respecto a v2.1.1 |
| --- | --- | --- |
| T-02 | PostGoodsReceipt | Sin rama POSTING\_BLOCKED: falta de requisitos contables → ROLLBACK total. Inserta integrity\_state para: evento, grupo de quantity entries, grupo de value entries, journal |
| T-03 | ReverseGoodsReceipt | Journal A = REVERSAL exacto de R-01 con value entries REVERSAL\_OF (−Q·P). Tras A, si el área queda `qty = 0 ∧ value ≠ 0` o `value < 0`: Journal B R-02B con value entry VALUATION\_REALLOCATION por el residuo. Ambos bajo el evento GoodsReceiptReversed |
| T-08 | PostSupplierInvoice | Preflight contable y fiscal (incluye ITBIS no recuperable → rechazo). Cualquier fallo → ROLLBACK; la factura permanece MATCHED/NOT\_POSTED |
| T-10 | ReverseSupplierInvoice | Journal A = REVERSAL exacto de R-04 + R-05 (incluido value entry REVERSAL\_OF del PRICE\_ADJUSTMENT). Journal B R-07B si s′ ≠ s. `qty_invoiced −=`; AP de reversa |
| T-11 | RepostEvent | Atómico: REVERSAL exacto de la generación n + AUTO generación n + 1, ambos con value entries propios, en una TX |
| T-13 | CloseComponent | Añade la verificación de integridad (ningún PENDING\_SEAL ni SEAL\_ERROR del período) |
| T-14 (nuevo) | RequestReopen / ApproveReopen | ApproveReopen: lock exclusivo del período/componente; `reopen_request` → APPROVED; componente → REOPENED |
| T-15 (nuevo) | RequestRoleChange / ApproveRoleChange | ApproveRoleChange: evaluación SoD + creación/revocación de `role_assignment` |
| Sellador | (fuera de comandos) | Única escritura: `audit.ledger_seal` y `UPDATE audit.integrity_state` a SEALED / SEAL\_ERROR |

### 5.4 Ajustes al plan de PRs (mismo orden)

| PR | Ajuste |
| --- | --- |
| PR-01 | Migración inicial con `CREATE EXTENSION btree_gist`; verificación de que el PostgreSQL de staging la permite; `md.company` |
| PR-02 | command\_log de P-3 (trigger de guarda y trigger diferido); company\_id en outbox/inbox; FKs compuestas |
| PR-03 | Permisos y roles de segundo aprobador; role\_assignment\_request; T-15 |
| PR-04 | `md.valuation_area`; FKs compuestas; exclusion en uom\_conversion |
| PR-05 | Tipos de journal AUTO/REVERSAL/VALUATION\_REALLOCATION; exclusion en account\_role\_map y posting\_rule\_version; preflight `ValidatePrerequisites` |
| PR-07 | Triggers diferidos value ↔ GL 1 a 1 y subledger = GL; value types REVERSAL\_OF y VALUATION\_REALLOCATION; regla R-T1 de fixture |
| PR-10 | T-03 con Journal A/B |
| PR-12 | environment y definition\_hash con gate |
| PR-13 | T-08 sin POSTING\_BLOCKED; T-10 con Journal A/B |
| PR-14 | RepostEvent atómico |
| PR-15 | `audit.integrity_state` y actualización por el sellador |
| PR-16 | Cierre con verificación de integridad; T-14 |

## 6. Posting rules reemplazadas

Reemplaza R-02 y R-07 de §13 y añade R-02B, R-07B, R-REP y R-T1. R-01, R-03a/b, R-04, R-05, R-06 y R-08 siguen iguales, con dos precisiones: toda línea RAW\_MATERIAL referencia su propio value entry (P-1) y, en R-04, una determinación con ITBIS no recuperable hace fallar el preflight (rechazo, no POSTING\_BLOCKED).

Notación: Q, P, P′, D = Q·(P′ − P) como en §13; `s` = porción en stock usada en el posteo original (guardada en `determination_inputs`); `s′ = min(qty_área_actual, Q) / Q`; `avg₀` = costo promedio del área antes del comando.

| Regla | Journal | Débito | Crédito | Value entries |
| --- | --- | --- | --- | --- |
| R-02 (A) | REVERSAL de R-01 | GRNI = Q·P | RAW\_MATERIAL = Q·P | REVERSAL\_OF = −Q·P por cada value entry RECEIPT del GR |
| R-02B (B) | VALUATION\_REALLOCATION, solo si tras A el área queda con `qty = 0 ∧ value ≠ 0` o `qty > 0 ∧ value ≤ 0` | Con `target = 0` si qty = 0, o `target = qty × avg₀` si qty > 0, y `R = target − value_tras_A`: si R > 0, RAW\_MATERIAL = R | si R > 0, PURCHASE\_PRICE\_VARIANCE = R (lados invertidos si R < 0) | VALUATION\_REALLOCATION = R |
| R-07 (A) | REVERSAL de R-04 y R-05 | Espejo exacto: AP\_CONTROL, WITHHOLDING\_PAYABLE; y los créditos de R-05 como débitos | GRNI = Q·P; ITBIS\_RECOVERABLE = T; RAW\_MATERIAL = s·D y PURCHASE\_PRICE\_VARIANCE = (1 − s)·D (si D > 0; invertido si D < 0) | REVERSAL\_OF = −s·D |
| R-07B (B) | VALUATION\_REALLOCATION, solo si `s′ ≠ s` | Con `R = (s − s′)·D`: si R > 0, RAW\_MATERIAL = R | si R > 0, PURCHASE\_PRICE\_VARIANCE = R (invertido si R < 0) | VALUATION\_REALLOCATION = R |
| R-REP | REVERSAL exacto de la generación n + AUTO generación n + 1 | Según regla y mapeo vigentes | Según regla y mapeo vigentes | REVERSAL\_OF de cada value entry de n; nuevos value entries en n + 1 por los mismos importes |
| R-T1 (solo tests) | AUTO del servicio interno IssueStock en fixtures | INVENTORY\_ADJUSTMENT = q·avg (vaciado si llega a cero) | RAW\_MATERIAL = q·avg | Value entry de salida |

Comprobación de R-07 A + B (D > 0): inventario = −s·D + (s − s′)·D = **−s′·D** (lo que sigue en stock); variación = −(1 − s)·D − (s − s′)·D = **−(1 − s′)·D** (lo ya consumido). Subledger y GL se mueven por los mismos importes en ambos journals, así que Inventory Value Subledger = Inventory GL se mantiene al COMMIT.

Comprobación de R-02 A + B: A retira exactamente lo que R-01 agregó; B solo corrige el efecto del promedio móvil sobre el remanente del área, contra variación de precio, y nunca deja `qty = 0` con valor.

R-T1 existe únicamente en las migraciones del proyecto de pruebas (`tests/migrations`), no en `db/migrations`; una prueba de arquitectura verifica que la regla no existe en la base de producción.

## 7. Acceptance tests cambiados y nuevos

### 7.1 Reemplazados

| ID | Given | When | Then |
| --- | --- | --- | --- |
| AT-05 | OC aprobada; **sin** mapeo ACTIVE para RAW\_MATERIAL (o sin regla R-01 ACTIVE, o sin período abierto alcanzable) | PostGoodsReceipt | ROLLBACK total: no existe GR, ni lote, ni quantity/value entry, ni journal, ni command\_log, ni integrity\_state; `qty_received` sin cambio; request\_log `REJECTED_DOMAIN / POSTING_PREREQUISITE_MISSING` con el requisito faltante. Tras activar el mapeo, el reintento con la misma clave ejecuta normalmente |
| AT-06 | GR posteado con mapeo A; se activa mapeo B para RAW\_MATERIAL | RepostEvent | Una TX: REVERSAL exacto de generación 1 + AUTO generación 2 con cuenta B; cada value entry con un solo gl\_entry; subledger = GL; `UNIQUE(source_event_id, rule, generation)` respetado |
| SI-06 | Factura POSTED con D = 2,000 y s = 0.25; después se retira todo el stock restante con IssueStock (fixture), s′ = 0 | ReverseSupplierInvoice | Journal A inverso exacto (RAW\_MATERIAL +500 revertido, variación 1,500 revertida); Journal B con R = 0.25·2,000 = 500: Dr RAW\_MATERIAL 500 / Cr PPV 500; efecto neto en inventario = 0 (nada de la diferencia sigue en stock); subledger = GL; GRNI reabierto; `qty_invoiced` restaurada |
| SI-06b | Igual a SI-06 pero sin movimientos posteriores (s′ = s) | ReverseSupplierInvoice | Solo Journal A; sin Journal B |
| SI-08 | Determinación fiscal produce ITBIS no recuperable | PostSupplierInvoice | Rechazado en preflight; factura sigue MATCHED/NOT\_POSTED; sin journal ni value entries |
| RC-01 | GR sin factura, lotes intactos, sin otros movimientos en el área | ReverseGoodsReceipt | Journal A inverso exacto de R-01; sin Journal B; value entries REVERSAL\_OF; stock y valor vuelven al estado previo |
| RC-01b | GR1 30 t a 100 y GR2 30 t a 80; IssueStock de 30 t de lote GR2 (avg 90 → sale 2,700); área: 30 t, 2,700 | ReverseGoodsReceipt(GR1) | A retira 30 t y 3,000 → área 0 t, −300; B (R-02B, target 0): R = 300 → Dr RAW\_MATERIAL 300 / Cr PPV 300; área final 0 t, 0 |
| PD-02 | Período con conciliaciones OK pero un journal en PENDING\_SEAL | CloseComponent | Rechazado; tras ejecutar el sellador → permitido |
| CC-05 | Cierre INV-MOV en curso; el sellador ya selló el período | Llega un GR con posting\_date del período | Espera el lock; tras el cierre cae al primer día abierto con late\_entry; si no existe período abierto alcanzable → rechazo (P-1) |

### 7.2 Nuevos

| ID | Given | When | Then |
| --- | --- | --- | --- |
| TEN-01 | Empresas A y B; RLS configurado para A | INSERT directo (sin aplicación) de una OC de A con party, item, plant/location de B | La base rechaza por FK compuesta; lo mismo para goods\_receipt\_line con lote de B y gl\_entry con cuenta de B |
| TEN-02 | Empresas A y B | Consultar vía aplicación con sesión de A | No ve filas de B (RLS) |
| INT-01 | Comando exitoso | Se consulta inmediatamente | accounting\_status POSTED; integrity\_state PENDING\_SEAL; la respuesta no esperó al sellador |
| INT-02 | Grupo con fila alterada antes del sellado | Sellador corre | integrity\_state SEAL\_ERROR; alerta CRITICAL; cierre bloqueado |
| CMD-01 | Comando en ejecución | Se intenta UPDATE de command\_log desde otra TX o después del COMMIT | Rechazado por trigger |
| CMD-02 | Comando cuyo código omite el paso 15 (simulado) | COMMIT | Falla por trigger diferido; ROLLBACK total |
| CMD-03 | Dos solicitudes con la misma clave | Concurrentes | La segunda espera el índice y devuelve `result_ref`/`result_payload` del primero |
| VAL-01 | Cualquier comando que crea value entries | COMMIT | Cada value entry con exactamente un gl\_entry de igual importe; Σ value = Σ RAW\_MATERIAL por posición tocada |
| VAL-02 | Código que inserta un value entry sin su gl\_entry (simulado) | COMMIT | Falla por trigger diferido; ROLLBACK total |
| REV-01 | Cualquier journal revertido | Se compara | Cada línea del REVERSAL = línea del original con débito y crédito intercambiados, mismas cuentas y dimensiones |
| TMP-01 | Mapeo ACTIVE vigente desde el 1/10 abierto | Activar otro mapeo del mismo rol desde el 15/10 | Rechazado por exclusion constraint; con cierre del anterior al 15/10 → permitido. Igual para posting\_rule\_version, accounting\_policy\_version, fiscal\_rule\_version y uom\_conversion |
| FIS-01 | Versión de regla con test run verde | Se modifica `definition` | `definition_hash` cambia; la activación se rechaza hasta un nuevo test run con el hash nuevo |
| FIS-02 | Base con `app.environment = PRODUCTION`; regla con solo fuentes TEST | ActivateFiscalRuleVersion | Rechazado |
| RO-01 | Componente CLOSED | Controller solicita reapertura y el mismo usuario intenta aprobarla | Rechazado; otro usuario con `period_component:second_approve` → REOPENED |
| RO-02 | Usuario con `period_component:reopen` | Se le asigna `period_component:second_approve` | Rechazado por SoD |
| RL-01 | Administrador solicita asignarse un rol a sí mismo | RequestRoleChange | Rechazado (CHECK requested\_by ≠ user\_id) |
| TST-01 | Base construida con `db/migrations` | Se busca la regla R-T1 | No existe |

## 8. Before → After

| # | Before (v2.1.1) | After (Patch 1) |
| --- | --- | --- |
| P-1 | GR podía confirmarse con accounting POSTING\_BLOCKED, dejando stock y value entries sin journal | Falta de requisito contable → ROLLBACK total; POSTING\_BLOCKED prohibido en tablas del slice; preflight + excepción |
| P-1 | Invariante valor ↔ GL verificado solo como "al menos un gl\_entry" y conciliación nocturna | Triggers diferidos al COMMIT: 1 value entry ↔ 1 gl\_entry de igual importe; Σ subledger = GL por posición tocada |
| P-1 | RepostEvent exigía reversa previa separada | RepostEvent atómico: REVERSAL exacto + nueva generación en una TX |
| P-2 | E-11 exigía el sello para considerar algo "contabilizado" | Contabilizado = journal confirmado y balanceado + accounting POSTED; sello es `integrity_status` separado |
| P-2 | Sin estado de integridad consultable | `audit.integrity_state` PENDING\_SEAL / SEALED / SEAL\_ERROR; el cierre exige SEALED |
| P-3 | command\_log con result\_payload en el INSERT, sin guarda de inmutabilidad | INSERT con result\_payload NULL → UPDATE único antes del COMMIT; trigger de guarda por xmin; trigger diferido de completitud |
| P-4 | R-02 retiraba a Q·avg con variación (recalculado) | R-02 = inverso exacto de R-01; R-02B VALUATION\_REALLOCATION solo para residuo del área |
| P-4 | R-07 recalculaba la porción en stock | R-07 = inverso exacto de R-04/R-05; R-07B reasigna (s − s′)·D |
| P-4 | Journal types AUTO, REVERSAL | AUTO, REVERSAL, VALUATION\_REALLOCATION; value types REVERSAL\_OF y VALUATION\_REALLOCATION |
| P-5 | FKs simples; aislamiento solo por RLS | `md.company`, `md.valuation_area`; UNIQUE(company\_id, id) y FKs compuestas en todas las relaciones company-scoped; company\_id en tablas hijas |
| P-5 | legal\_title\_holder / accounting\_owner sin restricción | En VS#1 = company\_id por CHECK |
| P-6 | Solo accounting\_policy\_version con exclusión | Exclusion constraints en account\_role\_map, posting\_rule\_version, accounting\_policy\_version, fiscal\_rule\_version, uom\_conversion; `btree_gist` en PR-01 |
| P-7 | Fuente fiscal sin ambiente; test run sin vínculo a la definición | `environment` TEST/PRODUCTION; `definition_hash` en versión y test run; gate compara hashes y exige fuente PRODUCTION en producción |
| P-8 | "Director" / "segundo admin" sin existencia en IAM | Permisos `period_component:second_approve` y `role:second_approve`; roles de segundo aprobador; solicitudes con CHECK de actores distintos; SoD |
| Derivado | Tests usaban IssueStock sin posteo definido (rompería P-1) | Regla R-T1 solo en migraciones de prueba; TST-01 verifica su ausencia en producción |
| Derivado | AT-05, AT-06, SI-06, SI-08, RC-01, PD-02, CC-05 | Reescritos (§7.1); añadidos SI-06b, RC-01b, TEN-01/02, INT-01/02, CMD-01/02/03, VAL-01/02, REV-01, TMP-01, FIS-01/02, RO-01/02, RL-01, TST-01 |

## 9. Re-ejecución de los acceptance tests

Ejecución mental de cada prueba de §15 de v2.1.1 y de §7 de este parche contra el diseño parcheado. Resultado: **PASA** (sin cambios), **PASA (reescrita)** o **PASA con precisión** (la prueba es válida, pero la re-ejecución exigió fijar un detalle de implementación que se documenta aquí y queda congelado).

| Prueba | Resultado | Observación |
| --- | --- | --- |
| AT-01 | PASA | Cada GR: value entry ↔ gl\_entry 1 a 1; integrity\_state PENDING\_SEAL |
| AT-02 | PASA | D = 0: sin R-05 ni value entries adicionales |
| AT-03 | PASA | Las 30 t salen por IssueStock con R-T1; s = 0.25; PRICE\_ADJUSTMENT +500 con su línea de GL |
| AT-04 | PASA | — |
| AT-05 | PASA (reescrita) | ROLLBACK total |
| AT-06 | PASA (reescrita) | RepostEvent atómico |
| AT-07 | PASA | Además el sellador marca SEAL\_ERROR si el borrado ocurre antes de sellar |
| RC-01 / RC-01b | PASA (reescrita / nueva) | Cifras de RC-01b verificadas: área final 0 t, 0 |
| RC-02, RC-04, RC-05, RC-06 | PASA | — |
| RC-03 | PASA | Salida de 28 t por R-T1; q₁ = 2 con value entry y línea RAW\_MATERIAL; q₂ = 1 solo a MATERIAL\_USAGE\_VARIANCE (sin value entry, correcto) |
| SI-01…SI-05 | PASA | — |
| SI-06 / SI-06b | PASA (reescrita / nueva) | Cifras verificadas: neto inventario 0, neto variación −2,000 |
| SI-07 | PASA | El gate fiscal falla en el preflight; la factura queda MATCHED/NOT\_POSTED |
| SI-08 | PASA (reescrita) | — |
| ID-01, ID-02, ID-03, ID-05, ID-06 | PASA | ID-01/02 leen `result_payload` escrito antes del COMMIT del primero |
| ID-04 | PASA | El fallo puede ocurrir en preflight (paso 7) o en posteo (paso 11); ambos → ROLLBACK |
| ID-07 | PASA | inbox ahora con company\_id y FK compuesta al evento |
| CC-01, CC-02, CC-03 | PASA | — |
| CC-04 | PASA con precisión | Ver precisión 1 |
| CC-05 | PASA (reescrita) | — |
| IV-01, IV-04, IV-05 | PASA | IV-05 usa R-T1 |
| IV-02 | PASA | Vaciado aplicado por R-T1 |
| IV-03 | PASA con precisión | Ver precisión 2 |
| SC-01…SC-04 | PASA | — |
| PD-01 | PASA | Existe período abierto alcanzable |
| PD-02 | PASA (reescrita) | — |
| HS-01, HS-02 | PASA | — |
| EX-01 | PASA con precisión | Ver precisión 3 |
| PF-01 | PASA con precisión | Depende de la precisión 1 |
| TEN-01/02, INT-01/02, CMD-01/02/03, VAL-01/02, REV-01, TMP-01, FIS-01/02, RO-01/02, RL-01, TST-01 | PASA | Nuevas; coherentes con §4–§6 |

### Precisiones congeladas por la re-ejecución

1. **Forma del trigger Subledger = GL.** Recalcular Σ histórico por posición en cada COMMIT haría fallar PF-01 y crecería sin límite. El constraint trigger diferido verifica la **igualdad de deltas de la transacción**: para cada (empresa, área, ítem) tocado, Σ `inv_value_entry.amount` insertados en la TX = Σ (débito − crédito) de líneas RAW\_MATERIAL insertadas en la TX. Como el estado previo cumplía la igualdad (inducción desde la apertura en cero), la igualdad total se mantiene. Es correcto bajo concurrencia en READ COMMITTED porque cada TX solo compara sus propias filas. La igualdad total se verifica cada noche (INV-VALUE-GL).
2. **Residuo en IV-03.** Con P-1 no es posible crear un residuo sin su contrapartida contable. El fixture inserta, con el rol de pruebas, un par value entry + gl\_entry balanceado que deja `qty = 0 ∧ value ≠ 0`; así la prueba ejercita VAL-RESIDUAL y el bloqueo de cierre sin violar P-1.
3. **Plantillas de explicación.** PR-17 debe incluir plantillas para R-02B, R-07B y R-REP además de R-01…R-08; EX-01 cubre un journal VALUATION\_REALLOCATION.
4. **Guarda de command\_log.** Si el cast `pg_current_xact_id()::xid` no está disponible en la versión desplegada, se usa una columna `created_xact xid8 NOT NULL DEFAULT pg_current_xact_id()` y el trigger compara con `pg_current_xact_id()`. Mismo comportamiento; se decide en PR-02 sin cambio de diseño.
5. **Hash de definición fiscal.** La forma canónica es la salida textual de `jsonb` de PostgreSQL. Si una actualización mayor de PostgreSQL cambiara esa salida, todos los hashes cambiarían y las activaciones exigirían nuevos test runs: falla segura, aceptada.

## 10. Veredicto

# VS1 DESIGN FROZEN — READY TO CODE

**Blockers reales de diseño: ninguno.** P-1 a P-8 están aplicados, todas las pruebas de la baseline pasan contra el diseño parcheado, y los cinco detalles surgidos en la re-ejecución quedaron fijados en §9 sin abrir decisiones nuevas.

Separados y sin efecto sobre el diseño (no motivan otra revisión arquitectónica): B-01 confirmación de ADR-009, B-02 revisor de ledgers, B-03 repositorio/CI/staging con `btree_gist` disponible.
