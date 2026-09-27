# FIN-1 — Diario de ajustes, balanza de comprobación y estados financieros

**Estado: BORRADOR — pendiente de aprobación de Alexander Rochell.** Se congela cuando se aprueben las decisiones D-01…D-10 de
la sección 9 (errata E-FIN1-1…10). Corre en paralelo con VS#3 porque no depende de proveedores externos.

Fuentes: Architecture v2 §3.A (CFO: estados financieros derivados del GL con estructura de reporte versionada) y §3.B
(Controller: diario de ajustes con soporte y aprobación), §18 (MVP P0 Finanzas: "diario de ajustes", BI "balance, resultados");
v2.1 §3 (componentes de cierre), §16 regla P-34 (ManualAdjustmentPosted, solo cuentas no controladas) y la política de reversos;
Frozen Baseline v2.1.1 (Posting Engine, hash chain, Explain this entry); lo construido en VS#1 y VS#2.

## 1. Alcance

| Dentro de FIN-1 | Fuera de FIN-1 |
| --- | --- |
| Clasificación de cuentas (activo, pasivo, patrimonio, ingreso, costo, gasto) y catálogo editable por el Controller | Presupuesto; consolidación entre empresas |
| Diario de ajustes: preparar (Contador) → aprobar (Controller, otra persona) → contabilizado; solo cuentas no controladas | Asientos a cuentas controladas (bancos, CxC, CxP, inventarios): siempre por su documento |
| Reversa de un ajuste; reversa automática el día 1 del mes siguiente (devengos manuales) | Devengos automáticos de nómina, intereses, energía (P1) |
| Mayor por cuenta con saldo inicial, movimientos y saldo final, con enlace a "Explicar asiento" | Flujo de caja indirecto y proyección de 13 semanas (P2) |
| Balanza de comprobación por período o rango, con filtros por planta, proveedor/cliente y cuenta bancaria | Notas a los estados financieros |
| Balance general y estado de resultados desde una **estructura de reporte versionada** que aprueba el Controller | Mapeo formal a presentación NIIF completas (se deja preparado) |
| Exportación CSV de balanza, mayor y estados | Envío automático por correo |
| API, pantallas en español y pruebas como en VS#1/VS#2 | |

Regla de congelamiento y E-VS1-2 (sin datos reales hasta B-02 o paralelo conciliado) igual que en VS#1–VS#3.

## 2. Esquema (conceptual)

```sql
ALTER TABLE fin.account ADD COLUMN account_class text;          -- ASSET, LIABILITY, EQUITY, REVENUE, COST, EXPENSE
ALTER TABLE fin.account ADD COLUMN status text;                 -- ACTIVE, INACTIVE (nunca se borra una cuenta con movimientos)
ALTER TABLE fin.gl_journal: journal_type admite MANUAL_ADJUSTMENT; posting_rule_id NULL solo para MANUAL_ADJUSTMENT (CHECK)
CREATE TABLE fin.manual_journal ( manual_journal_id, company_id, journal_no AJ-000001, posting_date, description,
  support_ref, support_sha256, status (DRAFT, PENDING_APPROVAL, POSTED, REJECTED), prepared_by, approved_by,
  auto_reverse boolean, journal_id (al contabilizar), version );
CREATE TABLE fin.manual_journal_line ( manual_journal_id, line_no, account_id, debit, credit, plant_id, party_id, memo );
CREATE TABLE fin.report_structure_version ( report (BALANCE_SHEET, INCOME_STATEMENT), version, status, approved_by, effective_from );
CREATE TABLE fin.report_line ( report_structure_version_id, line_code, caption, parent_line_code, sign, order_no );
CREATE TABLE fin.report_line_account ( report_line_id, account_id );   -- cada cuenta en una sola línea por reporte
```

## 3. Comandos y consultas

| Tipo | Nombre | Permiso | Notas |
| --- | --- | --- | --- |
| Comando | CreateAccount, UpdateAccount, DeactivateAccount | `account:manage` (Controller) | Código único; una cuenta de control no se desactiva con saldo |
| Comando | PrepareManualJournal, UpdateManualJournal, SubmitManualJournal | `manual_journal:prepare` (Contador) | Cuadrado (Σ débito = Σ crédito), 2 decimales, solo cuentas no controladas y activas, período abierto |
| Comando | ApproveManualJournal, RejectManualJournal | `manual_journal:approve` (Controller) | Aprobador ≠ preparador (comando y CHECK); reautenticación; al aprobar se contabiliza (P-34) |
| Comando | ReverseManualJournal | `manual_journal:approve` | Reversa exacta en período abierto, con motivo |
| Comando | PrepareReportStructure, ApproveReportStructure | `configuration:*` (Controller / Aprobador de políticas) | Toda cuenta activa con clase debe estar en una línea |
| Consulta | GetTrialBalance(desde, hasta, filtros) | `ledger:read` | Saldo inicial, débitos, créditos, saldo final por cuenta; totales cuadran |
| Consulta | GetAccountLedger(cuenta, desde, hasta) | `ledger:read` | Movimientos con documento de origen y enlace a Explicar |
| Consulta | GetBalanceSheet(fecha), GetIncomeStatement(desde, hasta) | `ledger:read` | Según la estructura vigente; el resultado del ejercicio cuadra el balance |

## 4. Reglas de contabilización

| Regla | Evento | Débito / Crédito | Notas |
| --- | --- | --- | --- |
| P-34 | ManualJournalApproved | Las líneas del ajuste | Solo cuentas sin rol de control; dimensiones obligatorias según la cuenta; journal MANUAL\_ADJUSTMENT con el mismo hash chain |
| P-34R | ManualJournalReversed / reversa automática | Líneas invertidas | REVERSAL enlazado al original; la automática se contabiliza el día 1 del mes siguiente |

## 5. Garantías de base de datos

- Un ajuste POSTED tiene exactamente un journal MANUAL\_ADJUSTMENT vivo (K-25 como los demás documentos).
- Un journal MANUAL\_ADJUSTMENT nunca toca una cuenta con rol de control (trigger).
- Aprobador ≠ preparador (CHECK); líneas inmutables después de enviar a aprobación.
- La estructura de reporte vigente cubre toda cuenta activa con clase (validado al aprobar; conciliación STRUCT-COVERAGE).

## 6. Cierre

Los ajustes pertenecen al componente **ACR-NTX** (v2.1 §3.1: devengos sin efecto tributario) salvo que el Contador marque el
ajuste como tributario, que va a **ACR-TAX** (D-06). Conciliaciones nuevas: MANUAL-EVIDENCE (cada ajuste POSTED con su journal;
bloquea ACR-NTX/ACR-TAX) y TB-BALANCED (la balanza cuadra y el resultado del ejercicio cuadra el balance; bloquea ACR-NTX).

## 7. Pruebas de aceptación (propuestas)

| ID | Given | When | Then |
| --- | --- | --- | --- |
| GL-01 | Ajuste cuadrado a cuentas de gasto y pasivo | Preparar, enviar, aprobar (otra persona) | POSTED; journal MANUAL\_ADJUSTMENT; balanza refleja el ajuste |
| GL-02 | Ajuste a una cuenta de control (banco, CxP, inventario) | Enviar | Rechazado (comando y base) |
| GL-03 | Preparador intenta aprobar | Aprobar | Rechazado |
| GL-04 | Ajuste con reversa automática | Cambio de mes | Reversa el día 1 del mes siguiente |
| GL-05 | Mes con compras, pagos, cargos y ajustes | Balanza | Débitos = créditos; saldo por cuenta = Σ asientos |
| GL-06 | Estructura de reporte aprobada | Balance y resultados | Activo = pasivo + patrimonio + resultado; cada cuenta en una sola línea |
| GL-07 | Cuenta con movimientos | Desactivar | Permitido solo sin saldo; nunca se borra |

## 8. Plan de PRs (propuesto)

| PR | Contenido | Pruebas |
| --- | --- | --- |
| FIN1-01 | Esquema: clase de cuenta, ajustes, estructura de reporte; permisos y roles (Contador) | Esquema, SoD |
| FIN1-02 | Catálogo editable y diario de ajustes con aprobación y reversa | GL-01…04, GL-07 |
| FIN1-03 | Balanza, mayor, balance y resultados; exportación CSV | GL-05, GL-06 |
| FIN1-04 | Pantallas (Contabilidad › Diario de ajustes, Balanza, Mayor, Estados) y recorrido Playwright | Recorrido UI |

## 9. Decisiones (para aprobar como E-FIN1-1…10)

| # | Decisión | Recomendación |
| --- | --- | --- |
| D-01 | ¿Quién prepara ajustes? | Rol nuevo **CONTADOR** (`manual_journal:prepare`, `ledger:read`); aprueba el Controller |
| D-02 | Cuentas permitidas en ajustes | Solo cuentas sin rol de control (v2.1 P-34); las controladas se mueven por su documento |
| D-03 | Catálogo de cuentas | El Controller crea, edita y desactiva cuentas desde la pantalla (hoy solo por CLI); nunca se borra una cuenta con movimientos |
| D-04 | Clasificación | Seis clases: activo, pasivo, patrimonio, ingreso, costo, gasto; obligatoria para toda cuenta activa |
| D-05 | Estructura de los estados | Versionada, preparada por el Controller y aprobada por el Aprobador de políticas; sin estructura aprobada los estados no se generan |
| D-06 | Componente de cierre de los ajustes | ACR-NTX por defecto; ACR-TAX si el ajuste se marca tributario |
| D-07 | Soporte del ajuste | Referencia y SHA-256 del documento soporte, obligatorios |
| D-08 | Reversa automática | Opcional por ajuste; se contabiliza el día 1 del mes siguiente (servicio en segundo plano) |
| D-09 | Permiso de lectura contable | Nuevo `ledger:read` para Contador, Controller, Auditor y Director |
| D-10 | Exportación | CSV (UTF-8, separador coma) de balanza, mayor y estados; Excel más adelante |
