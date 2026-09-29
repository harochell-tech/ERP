# Manufactura #1 — Producción, curado, liberación y costo estándar (Plan-to-Produce P0)

**Estado: CONGELADO — aprobado por Alexander Rochell el 2026-09-29**, con las decisiones D-01…D-18 tal como se recomiendan
(errata E-MFG1-1…18). Es la especificación; cualquier cambio sigue la regla de congelamiento.

Fuentes: Architecture v1 Parte IV §16–23 (orden, batch, rack, lote PT, recetas, máquinas); v2 C-01/C-03, H-25/H-28/H-29, §6
(modelo de costos: cost collector, variaciones, scrap), §7 (Production Ledger, modos de salida, agregados en tonelada seca), §8
(trazabilidad), §9 (liberación de calidad), §18 (MVP P0: Producción, Calidad, Costos), ADR-005, ADR-018, ADR-019, ADR-025; v2.1
§3 (OP-DAY, COST-SET), Corrección 6 / ADR-036 (estándar de planta), §10 (contratos `mfg`, `qa`), §13.3 (DDL conceptual), §14
(C-04…C-07), §16 (P-08…P-14); VS#3 D-01 y D-15; E-VS3-02-7; lo construido en VS#1…VS#3 y FIN-1.

## 1. Alcance y regla de congelamiento

Hoy el producto terminado solo entra por el saldo inicial (VS#3 D-01). Este slice lo hace entrar por producción, consumiendo
materia prima, y cierra el ciclo de costo con variaciones.

| Dentro de MFG-1 | Fuera de MFG-1 |
| --- | --- |
| Máquinas por planta y configuración producto × máquina (unidades por ciclo y por rack) | Moldes como activo (vida, depreciación por ciclo), cambios de molde (MFG-2) |
| Turnos por planta y fecha operativa (el turno de noche no se parte) | Programación con capacidad de curado, MPS, MRP (MFG-3) |
| Recetas versionadas por producto (y máquina), aprobadas por el Gerente de planta | Laboratorio: resistencia, granulometría, FINAL_RELEASED, recall (MFG-2) |
| Corrida de producción (orden operativa sin costo): producto × máquina × turno | Paradas, OEE, integración con HMI / PLC (MES, MFG-2) |
| Resumen de turno: batches, consumo real por material, unidades buenas, scrap por punto | Silos y medición de pilas (conciliación de silo, B7) |
| Racks propuestos desde unidades ÷ unidades por rack; lote PT por turno | Pools de conversión completos, capacidad ociosa, prorrateo de variaciones (MFG-2) |
| Curado en ubicación CURADO y liberación preliminar (o bloqueo) por Calidad | Retrabajo, COPQ |
| Costo estándar con desglose (materiales por receta + conversión) y revaluación al cambiarlo | Costo real por orden |
| Cost collector producto × planta × mes; liquidación con variación de precio y de uso | Varias máquinas por planta con estándar ponderado (Corrección 6), si no aplica hoy (D-04) |
| Componentes de cierre OP-DAY y COST-SET; conciliaciones de producción | |
| API, pantallas en español y pruebas punta a punta como en VS#1…VS#3 | |

Regla de congelamiento idéntica a VS#1…VS#3. E-VS1-2 aplica: **ningún dato real** hasta cerrar B-02 o una operación en paralelo
conciliada.

## 2. Esquema (nuevo, conceptual)

```sql
-- Maestros
CREATE TABLE md.machine ( machine_id, company_id, plant_id, code, name, status );                 -- CLI o pantalla (D-05)
CREATE TABLE mfg.product_machine_config ( item_id, machine_id, units_per_cycle, units_per_rack, version, status );
CREATE TABLE mfg.shift ( shift_id, company_id, plant_id, code, starts_at time, ends_at time, status ); -- fecha operativa = día de inicio
CREATE TABLE mfg.recipe_version (         -- por producto (y máquina opcional); inmutable al aprobarse
  recipe_version_id, item_id, machine_id NULL, version, effective_from, units_per_batch numeric(18,6),
  status (DRAFT, ACTIVE, SUPERSEDED), prepared_by, approved_by );
CREATE TABLE mfg.recipe_line ( recipe_version_id, material_item_id, qty_per_batch numeric(18,6) );  -- en UdM base del material
ALTER TABLE md.standard_cost_version ADD material_cost, conversion_cost;  -- unit_cost = material + conversión (D-09)
CREATE TABLE md.standard_cost_material ( cost_version_id, material_item_id, std_qty_per_unit, std_price ); -- desde la receta

-- Documentos
CREATE TABLE mfg.production_run ( run_id, run_no PR-, plant_id, machine_id, item_id, shift_id, business_date,
  recipe_version_id, cost_collector_id, status mfg.run_status, version );
CREATE TABLE mfg.shift_summary ( summary_id, run_id UNIQUE, batches, good_units, status (DRAFT, POSTED, REVERSED), posted_by );
CREATE TABLE mfg.material_consumption ( summary_id, material_item_id, location_id, qty, theoretical_qty ); -- movimiento ISSUE
CREATE TABLE mfg.scrap ( summary_id NULL, lot_id NULL, point (MIX, FRESH, CURING, YARD), qty, reason );
CREATE TABLE mfg.rack ( rack_id, run_id, rack_no, units, lot_id, status mfg.rack_status );
CREATE TABLE qa.fg_lot ( lot_id → inv.lot, run_id UNIQUE, cured_from, min_release_at, status qa.lot_status,
  released_by, released_at, block_reason );
CREATE TABLE mfg.cost_collector ( collector_id, plant_id, item_id, period (año-mes), status (OPEN, SETTLED), settled_by );
```

Movimientos de inventario nuevos: `PRODUCTION_RECEIPT` (entrada de PT a CURADO) y `PRODUCTION_ISSUE` (consumo de materia prima;
distinto del ISSUE genérico para trazabilidad). La liberación usa `TRANSFER` CURADO → PATIO (ya existe). Ubicación de sistema
nueva por planta: **CURADO** (como TRANSITO), que nunca es origen de un despacho.

Roles de cuenta nuevos: **WIP** (control, subledger de producción), **CONVERSION\_ABSORPTION**, **MATERIAL\_PRICE\_VARIANCE**,
**PRODUCTION\_SCRAP**, **STANDARD\_REVALUATION**. Se reutiliza MATERIAL\_USAGE\_VARIANCE (VS#1). Componentes de cierre nuevos:
**OP-DAY** (resúmenes de turno del período contabilizados) y **COST-SET** (collectors liquidados).

## 3. Agregados, comandos y eventos

| Agregado | Comandos | Eventos |
| --- | --- | --- |
| Machine / Config / Shift | CreateMachine, DefineProductMachineConfig, DefineShift | …Defined |
| Recipe | PrepareRecipe, ApproveRecipe | RecipeApproved |
| StandardCost (VS#3, extendido) | PrepareStandardCost (con desglose desde la receta), ApproveStandardCost | StandardCostApproved, StandardCostRevalued |
| ProductionRun | StartProductionRun, CancelProductionRun | ProductionRunStarted |
| ShiftSummary | RecordShiftSummary, PostShiftSummary, ReverseShiftSummary | MaterialConsumed, RackCreated (por rack), ScrapRecorded |
| FgLot | ReleaseLot, BlockLot, UnblockLot, ScrapLot | LotReleased, LotBlocked, ScrapRecorded |
| CostCollector | SettleCostCollector | CostCollectorSettled |

## 4. Máquinas de estado

- **Production Run:** PLANNED → IN\_PROGRESS → **COMPLETED** (resumen contabilizado); **CANCELLED** sin resumen.
- **Shift Summary:** DRAFT → **POSTED** (una sola TX: consumo, racks, lote, scrap) → **REVERSED** (solo si el lote no tiene
  movimientos posteriores).
- **Rack:** CREATED → CURING → **RELEASED** / **BLOCKED** → SCRAPPED (v1 §18, sin QC\_PENDING en P0).
- **FG Lot:** CURING → PRELIM\_RELEASED (transferido a PATIO) / BLOCKED → (desbloqueo con motivo) CURING; **SCRAPPED**. "Lo que
  no está liberado no existe para despacho".
- **Cost Collector:** OPEN → **SETTLED** (mes) ; reabrir solo con el período (VS#1).

## 5. Transacciones y concurrencia

C-05 + C-06 de v2.1 en **una** transacción al contabilizar el resumen de turno (PostShiftSummary): consumo (PRODUCTION\_ISSUE a
costo promedio), racks y lote, entrada de PT a CURADO a estándar, scrap. C-07 (liberación) es una transferencia sin valor.
Orden de bloqueo: production\_run → shift\_summary → cost\_collector → stock/valuation (VS#1, por ítem) → componentes de
período. Dos contabilizaciones del mismo resumen nunca producen dos entradas (UNIQUE run\_id + versión); el consumo nunca deja
stock negativo (P-3).

## 6. Reglas de posteo

| Regla | Evento | Débito | Crédito | Notas |
| --- | --- | --- | --- | --- |
| P-08 | MaterialConsumed | WIP | RAW\_MATERIAL | Cantidad real × costo promedio (VS#1) |
| P-10 | RackCreated (por resumen) | FINISHED\_GOODS | WIP (estándar de materiales); CONVERSION\_ABSORPTION (estándar de conversión) | Unidades buenas × estándar vigente |
| P-12 | ScrapRecorded (PT en curado o patio) | PRODUCTION\_SCRAP | FINISHED\_GOODS | A estándar; el scrap de mezcla o fresco no tiene asiento (queda en la variación de uso) |
| P-13 | CostCollectorSettled | MATERIAL\_USAGE\_VARIANCE; MATERIAL\_PRICE\_VARIANCE (± según signo) | WIP | Deja WIP del collector en cero (D-11) |
| REVAL | StandardCostRevalued | FINISHED\_GOODS o STANDARD\_REVALUATION | STANDARD\_REVALUATION o FINISHED\_GOODS | (nuevo − anterior) × existencias al cambiar el estándar (D-10) |

Ejemplo trabajado de P-13 (valores ilustrativos): estándar de BLOQUE-6 = materiales 22.10 (cemento 1.2 kg × 8.00, agregado
0.012 t × 1,000.00, aditivo 0.01 l × 50.00) + conversión 5.90 = 28.00. Mes con 10,000 unidades buenas: P-10 Dr PT 280,000.00 /
Cr WIP 221,000.00, Cr CONVERSION\_ABSORPTION 59,000.00. Consumo real: cemento 12,500 kg × 8.20, agregado 121 t × 990.00, aditivo
100 l × 50.00 → P-08 WIP 227,290.00. Liquidación: uso = (12,500 − 12,000) × 8.00 + (121 − 120) × 1,000.00 = 5,000.00; precio =
12,500 × 0.20 + 121 × (−10.00) = 1,290.00; total 6,290.00 = 227,290.00 − 221,000.00. Los gastos de conversión reales (nómina,
energía) quedan en sus cuentas y CONVERSION\_ABSORPTION los compensa: la diferencia es la sub o sobreabsorción del mes.

Reversos exactos como en VS#1…VS#3. Todas las reglas llegan en DRAFT y las aprueba el Controller (A-01).

## 7. Permisos y segregación de funciones

Roles nuevos (D-14): **SUPERVISOR\_PRODUCCION** (corridas, resumen de turno, racks, scrap de proceso), **GERENTE\_PLANTA**
(aprueba recetas y configuración producto × máquina, contabiliza o revierte resúmenes, scrap de PT), **CALIDAD** (libera,
bloquea y desbloquea lotes). El Controller prepara el estándar con desglose (APROBADOR\_POLITICAS lo aprueba, como VS#3) y
liquida collectors. SoD nuevos: preparar receta ≠ aprobar receta; registrar resumen ≠ contabilizarlo; producir (supervisor) ≠
liberar lote (calidad); liquidar collector ≠ aprobar estándar. Scrap de PT y reversión de resumen con step-up (v2.1 ADR-038).
Director y Auditor ven todo (lectura).

## 8. Conciliaciones y cierre

| Conciliación | A | B | Bloquea |
| --- | --- | --- | --- |
| WIP-GL | Σ WIP por collector (consumo − absorción − liquidado) | Saldo WIP | COST-SET |
| WIP-OPEN | Collectors del período sin liquidar | — (error al cierre) | COST-SET |
| SHIFT-OPEN | Corridas sin resumen contabilizado del período | — (error) | OP-DAY |
| USAGE-TOLERANCE | Consumo real vs teórico por material y resumen | Tolerancia de política PRODUCTION (A-01) | — (advertencia) |
| CURING-OVERDUE | Lotes en CURING con más horas que el máximo de política | — (advertencia) | — |
| FG-VALUE-GL (VS#3) | Se extiende a la ubicación CURADO | | INV-MOV |

Dependencias: OP-DAY → COST-SET → INV-MOV (no se cierra inventario con producción sin liquidar).

## 9. Pruebas de aceptación (propuestas)

| ID | Given | When | Then |
| --- | --- | --- | --- |
| MFG-01 | Receta sin aprobar | StartProductionRun | Rechazado (RECIPE\_NOT_ACTIVE); quien preparó no puede aprobar |
| MFG-02 | Corrida con receta y estándar vigentes | Resumen con 18 batches y consumo real | P-08 a costo promedio; P-10 a estándar; racks = ⌈unidades ÷ unidades por rack⌉; lote en CURADO |
| MFG-03 | Consumo mayor que el stock | PostShiftSummary | Rechazado; nada contabilizado |
| MFG-04 | Lote en CURADO | Pedido y conduce del producto | El despacho no toma unidades de CURADO |
| MFG-05 | Lote en CURADO antes del mínimo de curado | ReleaseLot | Rechazado; después del mínimo, transferido a PATIO sin asiento |
| MFG-06 | Lote liberado con rotura en patio | ScrapLot (step-up) | P-12; existencia y valor bajan |
| MFG-07 | Mes del ejemplo de §6 | SettleCostCollector | Uso 5,000.00; precio 1,290.00; WIP del collector 0 |
| MFG-08 | Estándar nuevo con existencias | ApproveStandardCost | REVAL por (nuevo − anterior) × existencias; P-3 cuadra |
| MFG-09 | Resumen contabilizado sin movimientos posteriores | ReverseShiftSummary | Reversos exactos de P-08/P-10; stock y WIP como antes |
| MFG-10 | Dos contabilizaciones simultáneas del mismo resumen | Concurrencia | Una sola entrada |
| MFG-11 | Período con producción | Cierre | SHIFT-OPEN, WIP-OPEN, WIP-GL MATCHED; OP-DAY → COST-SET → INV-MOV |
| E2E-M1 | Flujo completo | Por la API y por la UI | Receta → estándar → corrida → resumen → curado → liberación → conduce → factura → liquidación |
| INV-M | Secuencias aleatorias | Después de cada paso | P-1, P-3, WIP-GL, lotes liberados ⇔ en PATIO, nada despachado desde CURADO |

## 10. Dependencias externas

| # | Qué | Quién | Bloquea |
| --- | --- | --- | --- |
| X-1 | Máquinas por planta y, por producto, unidades por ciclo y por rack — se definen en el sistema | Gerente de planta | Datos reales |
| X-2 | Recetas por producto (kg de cemento, t secas de agregado por tipo, l de aditivo, unidades por batch) | Gerente de planta / laboratorio | Datos reales |
| X-3 | Turnos por planta y horas mínimas y máximas de curado por producto | Gerente de planta | Datos reales |
| X-4 | Costo de conversión estándar por producto (mano de obra, energía, mantenimiento por unidad) | Controller | Datos reales |
| X-5 | Tolerancia de consumo (±% por material) de la política PRODUCTION | Controller (A-01) | Datos reales |

## 11. Plan de PRs (propuesto)

| PR | Contenido | Pruebas |
| --- | --- | --- |
| MFG1-01 | Esquema: máquinas, turnos, recetas, corridas, resúmenes, racks, lotes, collectors, roles, permisos, CURADO | Esquema, RLS, SoD |
| MFG1-02 | Maestros: máquinas, configuración, turnos, recetas (preparar/aprobar); estándar con desglose y revaluación | MFG-01, MFG-08 |
| MFG1-03 | Corrida y resumen de turno: consumo, racks, lote, P-08, P-10; reversión | MFG-02, MFG-03, MFG-09 |
| MFG1-04 | Curado y liberación: CURADO fuera del despacho, ReleaseLot, BlockLot, ScrapLot, P-12 | MFG-04…06 |
| MFG1-05 | Cost collector y liquidación P-13; WIP-GL, WIP-OPEN, SHIFT-OPEN, USAGE-TOLERANCE; OP-DAY y COST-SET | MFG-07, MFG-11 |
| MFG1-06 | API y consultas (producción del día, lotes en curado, collectors, variaciones) | E2E-M1 por API |
| MFG1-07 | Pantallas (Producción, Calidad, Costos) y recorrido Playwright | E2E-M1 por UI |
| MFG1-08 | Propiedades, concurrencia y matriz de aceptación | INV-M, MFG-10 |

## 12. Decisiones (para aprobar como E-MFG1-1…18)

| # | Decisión | Recomendación |
| --- | --- | --- |
| D-01 | ¿Qué es la "orden" de producción en P0? | Una **corrida operativa sin costo** por producto × máquina × turno (v2 §6). El costo vive en el cost collector |
| D-02 | Modo de salida de materiales | **Resumen de turno para todos los materiales** (cemento, agregados, aditivo): el sistema propone el consumo teórico (receta × batches) y el supervisor registra el real. Un solo modo por material y planta (criterio B5). El backflush del aditivo (ADR-019) queda como propuesta teórica, no como salida automática |
| D-03 | Unidad de los agregados | Tonelada seca como UdM base (v2 §7); el supervisor puede registrar en m³ con la conversión vigente del ítem (VS#1). La corrección por humedad llega con laboratorio (MFG-2) |
| D-04 | Máquinas por planta | Hoy se asume **una línea por planta** (v1: tres plantas con Besser V3-12), así que estándar de planta = estándar de línea y la Corrección 6 no aplica aún. Si una planta tiene dos líneas, se agrega en MFG-2 |
| D-05 | ¿Quién crea máquinas, turnos y configuración? | Pantallas con permiso de GERENTE\_PLANTA (no CLI), versionadas; las plantas y ubicaciones siguen por CLI |
| D-06 | Racks | Propuestos por el sistema: ⌈unidades buenas ÷ unidades por rack⌉; el supervisor puede ajustar la cantidad de racks, no las unidades. Sin etiqueta de rack física en P0 |
| D-07 | Lote de PT | **Un lote por corrida** (planta + máquina + producto + turno + fecha), código `PT-<producto>-<yyyyMMdd>-<turno>`; el despacho sigue tomando lotes FIFO (E-VS3-04-5) |
| D-08 | Curado y liberación | El PT entra a la ubicación **CURADO** (no despachable). Calidad libera (preliminar) después de las horas mínimas de curado del producto, con inspección visual, transfiriendo a PATIO; o bloquea con motivo. Resistencia y liberación final: MFG-2 |
| D-09 | Costo estándar | Se extiende el estándar de VS#3 con desglose: **materiales** (receta ÷ unidades por batch × precio estándar de cada material) + **conversión** (monto por unidad que fija el Controller). Precio estándar de materia prima: lo fija el Controller al preparar el estándar |
| D-10 | Cambiar el estándar con existencias | Se permite (hoy E-VS3-02-7 lo prohíbe): al aprobar, **revaluación** de las existencias del área por (nuevo − anterior) × cantidad contra STANDARD\_REVALUATION, en la misma TX. Reemplaza E-VS3-02-7 |
| D-11 | Cost collector y WIP al fin de mes | Collector por **producto × planta × mes**. Los bloques se mezclan y moldean en el mismo turno, así que **no hay WIP que pase de mes**: la liquidación deja el WIP del collector en cero |
| D-12 | Variaciones en P0 | **Precio** (costo promedio real vs precio estándar, por cantidad real) y **uso** (cantidad real vs estándar por unidades buenas, a precio estándar). Capacidad ociosa, pools de conversión y prorrateo a inventario: MFG-2 (política PRODUCTION con umbral, ADR-039) |
| D-13 | Scrap | Cuatro puntos (mezcla, fresco, curado, patio). Mezcla y fresco: solo cantidad; su costo queda en la variación de uso. Curado y patio: P-12 a estándar contra PRODUCTION\_SCRAP, con step-up. Normal vs anormal: MFG-2 |
| D-14 | Roles de producción | Tres nuevos (Supervisor de producción, Gerente de planta, Calidad) con los SoD de la sección 7 |
| D-15 | Fecha operativa y turno de noche | La fecha operativa es la del **inicio** del turno; un turno de noche nunca se parte en dos fechas (v1 §12) |
| D-16 | Cierre | OP-DAY (resúmenes del período contabilizados) y COST-SET (collectors liquidados) bloquean en ese orden antes de INV-MOV |
| D-17 | HMI / PLC | Fuera de MFG-1: el resumen de turno es manual. El conteo de ciclos del HMI (Planta 3) se usa como referencia en MFG-2 (criterio B6: racks vs HMI ≤ 1 %) |
| D-18 | Datos reales | Igual que E-VS1-2: ninguno hasta B-02 o un paralelo conciliado |
