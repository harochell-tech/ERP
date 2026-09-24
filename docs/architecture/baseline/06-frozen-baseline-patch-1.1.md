# Architecture v2.1.1 — Frozen Baseline Patch 1.1

Sep 23, 2026 · @Alexander Rochell

## 1. Alcance

Patch 1.1 aplica cuatro correcciones puntuales sobre *Frozen Baseline Patch 1*. No hay revisión general ni capacidades nuevas; todo lo no mencionado sigue vigente.

## 2. Corrección 1 — Recepciones sin valor no soportadas en VS#1

**Regla.** Toda recepción del slice tiene valor contable. No se soportan materiales sin cargo (free-of-charge) en VS#1. Con esto, todo `GoodsReceipt` POSTED tiene, por construcción, `inv_value_entry` + `gl_journal` + `gl_entry`, y el `CHECK (amount <> 0)` de `inv_value_entry` nunca bloquea una recepción legítima del slice.

```sql
-- Reemplaza los CHECK de precio de §9.2
ALTER TABLE pur.purchase_order_line   DROP CONSTRAINT <unit_price >= 0>;
ALTER TABLE pur.purchase_order_line   ADD  CHECK (unit_price > 0);
ALTER TABLE pur.supplier_invoice_line DROP CONSTRAINT <unit_price >= 0>;
ALTER TABLE pur.supplier_invoice_line ADD  CHECK (unit_price > 0);
ALTER TABLE pur.goods_receipt_line    ADD  CHECK (unit_price > 0);   -- copia del precio de OC
```

El comando `CreatePurchaseOrder` / `UpdatePurchaseOrderDraft` valida el precio antes de escribir y devuelve error de dominio legible; el CHECK es la última defensa. La nota de K-05 ("recepciones a precio 0 solo crean quantity entry") queda sin efecto para VS#1.

## 3. Corrección 2 — Close gate de integridad por la fecha correcta

**Regla.** El cierre exige `SEALED` usando la fecha que gobierna cada ledger:

| Ledger en `audit.integrity_state` | Fecha que lo ubica en el período |
| --- | --- |
| GL, INV\_QTY, INV\_VALUE (y demás ledgers contables/operativos) | `posting_date` |
| DOMAIN\_EVENT | `business_date` |

```sql
-- Coherencia de fechas por tipo de ledger
ALTER TABLE audit.integrity_state ADD CHECK (
  (ledger = 'DOMAIN_EVENT' AND business_date IS NOT NULL AND posting_date IS NULL) OR
  (ledger <> 'DOMAIN_EVENT' AND posting_date IS NOT NULL)
);
CREATE INDEX integrity_pending_by_business_date ON audit.integrity_state (company_id, business_date)
  WHERE integrity_status <> 'SEALED' AND ledger = 'DOMAIN_EVENT';

-- Close gate conceptual (reemplaza la consulta de Patch 1 §4.2)
SELECT NOT EXISTS (
  SELECT 1 FROM audit.integrity_state s
  WHERE s.company_id = :company_id
    AND s.integrity_status <> 'SEALED'
    AND (
      (s.ledger <> 'DOMAIN_EVENT' AND s.posting_date  BETWEEN :period_start AND :period_end) OR
      (s.ledger =  'DOMAIN_EVENT' AND s.business_date BETWEEN :period_start AND :period_end)
    )
) AS integrity_ok;
```

`CloseComponent` rechaza el cierre si `integrity_ok = false` e informa cuántos grupos siguen en PENDING\_SEAL o SEAL\_ERROR por ledger.

## 4. Corrección 3 — El ambiente de producción no se puede suplantar

**Problema.** `current_setting('app.environment')` es un parámetro de sesión personalizado: cualquier sesión, incluida la del rol de aplicación, puede ejecutar `SET app.environment = 'TEST'` y saltarse el gate de P-7.

**Corrección.** El ambiente se lee de una tabla de una sola fila que solo el rol de despliegue puede escribir. El gate deja de consultar `current_setting` para esta decisión.

```sql
-- Creada y poblada por la migración inicial, ejecutada con el rol de despliegue (owner)
CREATE TABLE core.deployment_environment (
  singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
  environment text NOT NULL CHECK (environment IN ('TEST','PRODUCTION')),
  set_by text NOT NULL, set_at timestamptz NOT NULL
);
-- El valor se inserta en el despliegue de cada ambiente, nunca desde la aplicación
REVOKE ALL ON core.deployment_environment FROM PUBLIC, rochell_app;
GRANT SELECT ON core.deployment_environment TO rochell_app;

-- Función usada por el gate fiscal (owner = rol de despliegue)
CREATE FUNCTION core.current_environment() RETURNS text
  LANGUAGE sql STABLE SECURITY DEFINER SET search_path = core, pg_temp
  AS 'SELECT environment FROM core.deployment_environment WHERE singleton';
REVOKE ALL ON FUNCTION core.current_environment() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION core.current_environment() TO rochell_app;
```

Reglas complementarias:

- El trigger `activate_gate` de `tax.fiscal_rule_version` usa `core.current_environment()`; si la tabla está vacía, la activación se rechaza (falla cerrada).
- El rol de aplicación no es owner de las tablas ni de los triggers, no es miembro del rol de despliegue y no tiene `SET ROLE` hacia él; por tanto no puede deshabilitar el trigger ni modificar la tabla.
- `app.environment` puede seguir existiendo solo como dato informativo para logs; ninguna regla de negocio lo lee (prueba de arquitectura: búsqueda de `current_setting('app.environment')` en funciones de gate = 0).
- Cambiar el valor de la tabla requiere una migración de despliegue firmada, registrada en el audit log.

## 5. Corrección 4 — Nombre y trazabilidad del método de asignación de diferencia de precio

Sin cambio de algoritmo. Se nombra y se versiona:

| Elemento | Definición |
| --- | --- |
| Parámetro de política | `invoice_price_variance_allocation_method` en la política contable del slice (marco de Corrección 9 de v2.1) |
| Valor único permitido en VS#1 | `STOCK_COVERAGE` (el valor se valida por CHECK del parámetro; otros valores requieren errata) |
| Fórmula | `s = min(qty_área, Q) / Q`; porción `s·D` a RAW\_MATERIAL y `(1 − s)·D` a PURCHASE\_PRICE\_VARIANCE (R-05), y `s′` en R-07B |
| Naturaleza | **Política de asignación contable bajo promedio móvil** del área de valuación. No es trazabilidad física: no identifica qué lote o qué unidades recibidas siguen en stock; usa la cobertura de existencias del área como aproximación de la parte de la diferencia aún capitalizable |
| Registro | `determination_inputs` de cada journal R-05, R-07 y R-07B guarda `invoice_price_variance_allocation_method`, su `policy_version_id`, `qty_área` usado, `Q`, `s` (o `s′`) y `D` |

Explain (EX-01) muestra el nombre del método y la versión de política en la explicación de R-05 y R-07B.

## 6. Tests añadidos

| ID | Given | When | Then |
| --- | --- | --- | --- |
| VAL-03 | Proveedor e ítem activos | CreatePurchaseOrder con una línea a `unit_price = 0` (vía comando y vía INSERT directo con el rol de aplicación) | Comando: error de dominio; INSERT directo: rechazado por CHECK. Lo mismo para una línea de factura de proveedor a precio 0 |
| INT-03 | Período con todos los journals e inventario SEALED; un `domain_event` del período en PENDING\_SEAL (`posting_date` NULL, `business_date` dentro del período) | CloseComponent | Rechazado por el close gate; tras sellar el evento → permitido |
| FIS-03 | Base de producción (`core.deployment_environment = PRODUCTION`); versión de regla fiscal con test run verde y solo fuentes TEST | Sesión con el rol de aplicación ejecuta `SET app.environment = 'TEST'`, intenta `UPDATE core.deployment_environment` y luego ActivateFiscalRuleVersion | El SET no tiene efecto sobre el gate; el UPDATE falla por permisos; la activación se rechaza por falta de fuente PRODUCTION |
| POL-01 | Factura con diferencia de precio | PostSupplierInvoice | `determination_inputs` contiene `invoice_price_variance_allocation_method = STOCK_COVERAGE`, `policy_version_id`, `qty_área`, `Q`, `s`, `D`; Explain los muestra |

Las pruebas existentes no cambian de resultado: AT-02 y AT-03 ya usaban precios positivos; PD-02 e INT-01/02 siguen pasando con el close gate por fecha correcta; FIS-01/02 pasan con `core.current_environment()`.

## 7. Veredicto

# VS1 DESIGN FROZEN — READY TO CODE

Arquitectura cerrada. Siguiente paso: cerrar B-01, B-02 y B-03 e iniciar PR-01.
