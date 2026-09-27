# Vertical Slice #3 — Ventas, despacho y cobros (Order-to-Cash)

**Estado: CONGELADO — aprobado por Alexander Rochell el 2026-09-27**, con las decisiones D-01…D-16 tal como se recomiendan
(errata E-VS3-1…16). Es la especificación; cualquier cambio sigue la regla de congelamiento.

Fuentes: Architecture v2 §5 Decisión 1 (reconocimiento de ingreso por transferencia de control), §4.J (fiscal RD / e-CF), §12.3
(máquina e-CF), §18 (MVP P0: "Ventas y cobros", "Despacho"); v2.1 §2 (título legal ≠ control contable), §3 (componentes de cierre
AR-REC, OP-DAY, FISC-DOC), §4 (sin doble fuente de verdad: emisión fiscal externa), §5 (FiscalSequenceAuthority y contract test
CT-01…16), §6 (documentos de apertura), §10 (contrato del dominio Sales), §13.3 (`log.delivery`, `sal.invoice`), §14 (C-08…C-13),
§16 (reglas P-15…P-27), §18 (máquinas Sales Order, Delivery, Invoice, Payment); Frozen Baseline v2.1.1 E-1 (estados comercial,
contable y fiscal de la factura) y E-9 (política `revenue_accounting`); lo construido en VS#1 y VS#2.

## 1. Alcance y regla de congelamiento

VS#3 abre el lado del ingreso: hoy el sistema compra, recibe, paga y concilia, pero no vende.

| Dentro de VS#3 | Fuera de VS#3 |
| --- | --- |
| Clientes (party con RNC/cédula validado), términos de pago, límite de crédito | Contratos de precio por cliente (P1), CRM |
| Productos terminados con costo estándar aprobado; lista de precios versionada | Producción, racks, cost collectors, calidad (slice de manufactura, ver D-01) |
| Saldo inicial de producto terminado por documento de apertura (v2.1 §6) | Resto de la migración desde ADM Cloud |
| Pedido de venta con evaluación de crédito | Cotización (P0, siguiente PR si se aprueba D-04) |
| Conduce: vehículo y chofer, pesada de báscula manual, salida por portón, entrega (POD) | Consignación, bill-and-hold, transportista contratado (D-05) |
| Transferencia de control y reconocimiento de ingreso y costo (Policy Engine con 2 términos) | Pérdida en tránsito con seguro (solo la baja, P-30, D-10) |
| Factura desde conduces; ITBIS de ventas por el Tax Engine | Anticipos facturados antes de entregar (D-07) |
| e-CF por canal externo del proveedor con registro en Core (FISCAL_PENDING_EXTERNAL → ISSUED) | Gateway e-CF automático (VS#4, tras el contract test del proveedor, D-08) |
| Nota de crédito comercial (descuento, error de precio) | Devoluciones con reingreso a inventario (D-12); notas de débito |
| Cobros: registro, aplicación, desaplicación, cheque devuelto; retención del cliente | Autorizaciones fiscales Confotur (D-13); moneda extranjera |
| Conciliación del cobro con el extracto (línea CREDIT) | Collections Workbench (P1) |
| AR-GL, componente de cierre AR-REC; antigüedad de CxC y estado de cuenta | Reportes 607/608 (bloque fiscal, requiere A-02) |
| API, pantallas en español y pruebas punta a punta como en VS#1/VS#2 | |

Regla de congelamiento idéntica a VS#1 y VS#2. E-VS1-2 aplica: **ningún dato comercial, fiscal o contable real** hasta cerrar
B-02 o una operación en paralelo conciliada (E-VS2-10).

## 2. Esquema (nuevo, conceptual)

```sql
-- Maestros
ALTER TABLE md.party ADD COLUMN is_customer boolean NOT NULL DEFAULT false;
CREATE TABLE sal.customer_terms (           -- versionado; aprobado por Crédito
  company_id, party_id, version, payment_terms_days int, credit_limit numeric(19,4), credit_hold boolean,
  status (DRAFT, ACTIVE, SUPERSEDED), prepared_by, approved_by, effective_from );
-- md.item: item_type FINISHED_GOOD (además de RAW_MATERIAL), unidad de venta y conversiones existentes
CREATE TABLE md.standard_cost_version (     -- costo estándar de PT por área de valuación (A-01)
  item_id, valuation_area_id, version, unit_cost numeric(19,4), effective_from, status, approved_by );
CREATE TABLE sal.price_list_version (       -- precio por ítem y unidad, DOP, sin ITBIS
  company_id, price_list_id, version, effective_from, status, approved_by );
CREATE TABLE sal.price_list_line ( price_list_version_id, item_id, uom, unit_price numeric(19,4) );
CREATE TABLE log.vehicle ( vehicle_id, company_id, plate, capacity_kg numeric(18,6), status );
CREATE TABLE log.driver  ( driver_id, company_id, full_name, national_id, status );
CREATE TABLE log.delivery_term_policy (     -- versionada (v2 Decisión 1): 2 términos en VS#3
  term_code (PICKUP_AT_PLANT, DELIVERED_OWN_TRANSPORT), control_transfers_at (GATE_OUT, POD), version, status );

-- Documentos
CREATE TABLE sal.sales_order ( ..., party_id, plant_id, site_address, delivery_term_code, price_list_version_id,
  status sal.sales_order_status, credit_check_id, version );
CREATE TABLE sal.sales_order_line ( ..., item_id, uom, qty_ordered, unit_price, qty_delivered, qty_invoiced );
CREATE TABLE sal.credit_check ( ..., exposure, limit, overdue_days, decision (AUTO_APPROVED, NEEDS_APPROVAL), decided_by );
CREATE TABLE log.delivery ( ... v2.1 §13.3: delivery_no, sales_order_id, term, vehicle_id, driver_id, status, gate_out_at,
  weigh_ticket_ref, gross_kg, tare_kg, version );
CREATE TABLE log.delivery_line ( ... qty_planned, qty_issued, qty_delivered, qty_invoiced; CHECK qty_invoiced ≤ qty_delivered );
CREATE TABLE log.pod ( pod_id, delivery_id UNIQUE, received_by_name, received_at, evidence_ref, evidence_sha256, exception_reason );
CREATE TABLE inv.control_assessment ( ... UNIQUE (trigger_event_id) );          -- C-10
CREATE TABLE sal.invoice ( ... E-1: commercial_status, accounting_status, fiscal_status; encf UNIQUE por empresa;
  tax_determination_id; total, tax_total; CHECK de combinaciones válidas );
CREATE TABLE sal.invoice_line ( ..., delivery_line_id, qty, unit_price, net, itbis );
CREATE TABLE sal.credit_note ( ..., invoice_id, reason, commercial_status, accounting_status, fiscal_status, encf );
CREATE TABLE tax.fiscal_sequence_series ( ... v2.1 §5; authority PROVIDER_MANAGED en VS#3 );
CREATE TABLE tax.external_fiscal_record ( invoice_or_note_id, encf, issued_at, security_code, evidence_sha256, totals );
CREATE TABLE fin.ar_document ( ar_doc_id, party_id, doc_type (INVOICE, OPENING), source_doc_id, doc_date, due_date,
  original_amount, open_amount CHECK (0 ≤ open ≤ original), version );
CREATE TABLE fin.receipt ( receipt_id, receipt_no REC-, party_id, bank_account_id, method (TRANSFER, CHECK, CASH),
  amount, unapplied_amount CHECK (0 ≤ unapplied ≤ amount), value_date, bank_reference, status, version );
CREATE TABLE fin.ar_application ( application_id, receipt_id, ar_doc_id, amount, reverses_application_id UNIQUE );
CREATE TABLE fin.customer_withholding ( receipt_id, ar_doc_id, amount, certificate_ref );
```

Roles de cuenta nuevos: AR\_CONTROL, CONTRACT\_ASSET, FINISHED\_GOODS, FINISHED\_GOODS\_IN\_TRANSIT, COGS, REVENUE\_PRODUCT,
SALES\_DISCOUNTS, ITBIS\_PAYABLE, UNAPPLIED\_RECEIPTS, CASH\_IN\_TRANSIT, WITHHOLDING\_RECEIVABLE, TRANSIT\_LOSS. Componente de
cierre nuevo: **AR-REC** (y OP-DAY para despachos del día, D-15).

## 3. Agregados, comandos y eventos

| Agregado | Comandos | Eventos |
| --- | --- | --- |
| Customer / CustomerTerms | CreateCustomer, UpdateCustomer, ActivateCustomer, PrepareCustomerTerms, ApproveCustomerTerms | CustomerCreated…, CustomerTermsApproved |
| StandardCost / PriceList | PrepareStandardCost, ApproveStandardCost, PreparePriceList, ApprovePriceList | …Approved |
| OpeningFinishedGoods | RecordOpeningInventory (documento de apertura, v2.1 §6) | OpeningInventoryRecorded |
| SalesOrder | CreateSalesOrder, UpdateSalesOrderDraft, SubmitForCredit, ApproveCredit, RejectCredit, CancelSalesOrder, CloseSalesOrder | SalesOrderConfirmed, CreditApproved, CreditBlocked, SalesOrderCancelled, SalesOrderClosed |
| Delivery | PlanDelivery, StartLoading, ConfirmLoaded, RecordWeighingAndGateOut, RecordPod, RecordReturnTrip, CancelDelivery | GoodsIssued, GoodsDelivered, DeliveryReturned, ControlTransferred |
| Invoice | CreateInvoiceFromDeliveries, IssueInvoice, RecordExternalFiscalDocument, VoidUnfiscalizedInvoice | InvoiceIssueRequested, InvoiceIssued, ExternalFiscalDocumentIssued, InvoiceVoided |
| CreditNote | CreateCreditNote, IssueCreditNote, RecordExternalFiscalDocument | CreditNoteIssued |
| Receipt | RecordReceipt, ApplyReceipt, UnapplyReceipt, RecordCustomerWithholding, MarkReceiptBounced, ReverseReceipt | ReceiptRecorded, ReceiptApplied, ReceiptUnapplied, ReceiptBounced |
| BankStatement (VS#2) | MatchBankLine extendido a cobros (línea CREDIT ↔ recibo) | ReceiptMatched |

## 4. Máquinas de estado

Las de v2.1 §18 para Sales Order, Delivery y Payment (recibo), y E-1 para Invoice (tres columnas: comercial, contable, fiscal),
recortadas al alcance:

- **Sales Order:** DRAFT → PENDING\_CREDIT → CONFIRMED → PARTIALLY\_DELIVERED → DELIVERED → **CLOSED**; RejectCredit vuelve a
  DRAFT; **CANCELLED** solo sin entregas; CloseShort con motivo y aprobación.
- **Delivery:** PLANNED → LOADING → LOADED → (pesada + portón) IN\_TRANSIT (entregado en obra) o **DELIVERED** (retiro en
  planta) → POD → **DELIVERED** / **DELIVERED\_WITH\_EXCEPTIONS**; **RETURNED** (rechazo total); **CANCELLED** antes del portón.
- **Invoice:** comercial DRAFT → CONFIRMED → PARTIALLY\_PAID / **PAID** / **CREDITED** / **VOIDED**; contable NOT\_POSTED →
  POSTED (misma TX) → REVERSED (solo al anular); fiscal PENDING → PENDING\_EXTERNAL → ACCEPTED\_EXTERNAL (VS#3) o REJECTED.
  Combinaciones inválidas bloqueadas por CHECK (tabla de E-1).
- **Receipt:** RECORDED → MATCHED (conciliación) → PARTIALLY\_APPLIED / **APPLIED**; **BOUNCED**; **REVERSED** sin aplicaciones.

## 5. Transacciones y concurrencia

C-08 (portón), C-09 (POD), C-10 (transferencia de control, siempre dentro de C-08 o C-09), C-11 (emitir factura), C-12 (cobro),
C-13 (aplicar cobro) de v2.1 §14. Orden de bloqueo nuevo, compatible con VS#1/VS#2: sales\_order → delivery → delivery\_lines
(por id) → stock/valuation (VS#1) → ar\_document (por id) → receipt → bank\_account → componentes de período. Dos facturas de las
mismas líneas de conduce nunca facturan más que lo entregado (bloqueo + CHECK `qty_invoiced ≤ qty_delivered`); dos aplicaciones
del mismo recibo nunca exceden su monto (CHECK `unapplied ≥ 0`).

## 6. Reglas de posteo

| Regla | Evento | Débito | Crédito | Notas |
| --- | --- | --- | --- | --- |
| P-15 | GoodsIssued (entregado en obra) | FINISHED\_GOODS\_IN\_TRANSIT | FINISHED\_GOODS | A costo estándar |
| P-16 | ControlTransferred sin factura | COGS; CONTRACT\_ASSET | FINISHED\_GOODS (o …\_IN\_TRANSIT); REVENUE\_PRODUCT | Precio del pedido; E-9 fija el rol del activo de contrato |
| P-18 | InvoiceIssued (control ya transferido) | AR\_CONTROL | CONTRACT\_ASSET; ITBIS\_PAYABLE | ITBIS por regla fiscal activa |
| P-22 | CreditNoteIssued (comercial) | SALES\_DISCOUNTS; ITBIS\_PAYABLE | AR\_CONTROL | Referencia el e-NCF original |
| P-23 | ReceiptRecorded | BANK o CASH\_IN\_TRANSIT | UNAPPLIED\_RECEIPTS | Efectivo/cheque en tránsito hasta depositar |
| P-25 | ReceiptApplied | UNAPPLIED\_RECEIPTS | AR\_CONTROL | — |
| P-27 | WithholdingByCustomer | WITHHOLDING\_RECEIVABLE | AR\_CONTROL | Con certificado |
| P-29 | CashInTransitDeposited | BANK | CASH\_IN\_TRANSIT | — |
| P-30 | TransitLossRecognized | TRANSIT\_LOSS | FINISHED\_GOODS\_IN\_TRANSIT | POD con faltante, control retenido |
| OPEN-INV | OpeningInventoryRecorded (PT) | FINISHED\_GOODS | MIGRATION\_CLEARING | v2.1 §6; período OPENING |

Reversos exactos como en VS#1/VS#2. Todas las reglas llegan en DRAFT y las aprueba el Controller (A-01).

## 7. Permisos y segregación de funciones

Roles nuevos (D-03): **VENDEDOR** (clientes y pedidos), **CREDITO** (términos y aprobación de crédito), **DESPACHO** (conduces,
báscula, portón, POD), **FACTURACION** (facturas, notas de crédito, registro fiscal externo), **COBROS** (recibos y
aplicaciones). SoD nuevos: crear cliente ≠ aprobar crédito (v2 §3.C); aprobar crédito ≠ crear pedido; facturar ≠ registrar
cobro; registrar cobro ≠ conciliar banco; nota de crédito ≠ quien facturó (cuatro ojos); configurar regla fiscal ≠ emitir
factura (v2 §3.C). Director y Auditor ven todo (lectura).

## 8. Conciliaciones y cierre

| Conciliación | A | B | Bloquea |
| --- | --- | --- | --- |
| AR-GL | Σ open\_amount por cliente | Saldo AR\_CONTROL por cliente | AR-REC |
| CONTRACT-ASSET | Entregado no facturado valorizado | Saldo CONTRACT\_ASSET | AR-REC |
| RECEIPT-APPL | Σ aplicaciones vivas + no aplicado | Monto de recibos vivos | AR-REC, BANK-REC |
| FG-VALUE-GL | Valuación de PT | FINISHED\_GOODS + …\_IN\_TRANSIT | INV-MOV |
| DELIVERY-OPEN | Conduces en tránsito > 24 h | — (advertencia) | — |
| FISC-DOC | Facturas y notas con e-CF no final al cierre | — (error) | FISC-DOC |
| BANK-GL (VS#2) | Se extiende a líneas CREDIT de cobros | | BANK-REC |

## 9. Pruebas de aceptación (propuestas)

| ID | Given | When | Then |
| --- | --- | --- | --- |
| SAL-01 | Cliente activo con límite 100,000 y exposición 0 | Pedido de 50,000 | Crédito auto-aprobado; CONFIRMED |
| SAL-02 | Exposición 90,000 | Pedido de 20,000 | PENDING\_CREDIT; CREDITO aprueba; el vendedor no puede aprobar |
| SAL-03 | Pedido CONFIRMED, retiro en planta | Portón con pesada | DELIVERED; control transferido; P-16 (COGS a estándar, ingreso, activo de contrato) |
| SAL-04 | Entregado en obra | Portón y luego POD | P-15 al portón; P-16 al POD; sin ingreso antes del POD |
| SAL-05 | POD con faltante | RecordPod con excepción | Control solo de lo recibido; P-30 por la diferencia |
| SAL-06 | Conduces entregados | Factura por lo entregado | P-18: AR = neto + ITBIS; activo de contrato 0; no se puede facturar de más |
| SAL-07 | Factura PENDING\_EXTERNAL | RecordExternalFiscalDocument con totales distintos | Rechazado; con totales iguales → ACCEPTED\_EXTERNAL |
| SAL-08 | Factura aceptada | Nota de crédito parcial | P-22; saldo AR baja; e-NCF original referenciado |
| SAL-09 | Dos facturas simultáneas de las mismas líneas | Emisión concurrente | Una sola factura por lo entregado |
| AR-01 | Factura de 118,000 | Cobro de 100,000 + retención 18,000 aplicados | Factura PAID; P-23, P-25, P-27; AR-GL MATCHED |
| AR-02 | Cobro aplicado | Cheque devuelto | Aplicaciones revertidas; factura reabierta |
| AR-03 | Cobro por transferencia | Línea CREDIT del extracto | Conciliada; BANK-GL cuadra |
| AR-04 | Mes con ventas y cobros | Cierre AR-REC | AR-GL, CONTRACT-ASSET y RECEIPT-APPL MATCHED; cierre permitido |
| E2E-S1 | Flujo completo | Por la API y por la UI | Pedido → conduce → POD → factura → e-CF externo → cobro → conciliación |
| INV-S | Secuencias aleatorias | Después de cada paso | Invariantes (AR, activo de contrato, PT, crédito) como INV-P |

## 10. Dependencias externas

| # | Qué | Quién | Bloquea |
| --- | --- | --- | --- |
| X-1 | Nombre del proveedor de e-CF y acceso a su ambiente de pruebas — **a definir** (2026-09-27) | Alexander | VS#4 (Gateway); VS#3 usa canal externo |
| X-2 | Fuentes DGII vigentes: ITBIS de ventas, hecho generador (entrega o factura), retenciones de clientes, tipos de e-CF (A-02) | Fiscal | Datos reales; en staging se usan fuentes TEST |
| X-3 | Costo estándar de cada producto terminado y lista de precios (A-01) — **se definen dentro del sistema** (pantallas de VS3-02/VS3-10) | Controller | Datos reales |
| X-4 | Términos de pago y límites de crédito — **por cada cliente**, definidos en el sistema (ver E-VS3-17 para proveedores) | Crédito | Datos reales |
| X-5 | Vehículos, choferes y capacidad de báscula — **se definen dentro del sistema** | Despacho | Datos reales |

## 11. Plan de PRs (propuesto)

| PR | Contenido | Pruebas |
| --- | --- | --- |
| VS3-01 | Esquema: clientes, términos, PT y costo estándar, precios, vehículos, choferes, roles y permisos | Esquema, RLS, SoD |
| VS3-02 | Maestros: comandos de cliente, crédito, costo estándar, precios; saldo inicial de PT | — |
| VS3-03 | Pedido de venta y crédito | SAL-01, SAL-02 |
| VS3-04 | Conduce: carga, pesada, portón, POD; Policy Engine; P-15, P-16, P-30 | SAL-03…05 |
| VS3-05 | Factura desde conduces, ITBIS de ventas, e-CF externo, anulación; P-18 | SAL-06, SAL-07, SAL-09 |
| VS3-06 | Nota de crédito comercial; P-22 | SAL-08 |
| VS3-07 | Cobros: registro, aplicación, retención, cheque devuelto, conciliación con extracto | AR-01…03 |
| VS3-08 | AR-GL, CONTRACT-ASSET, RECEIPT-APPL, componente AR-REC | AR-04 |
| VS3-09 | API, consultas (CxC, estado de cuenta, pedidos, conduces), OpenAPI | E2E-S1 por API |
| VS3-10 | Pantallas (Ventas, Despacho, Facturación, Cobros) y recorrido Playwright | E2E-S1 por UI |
| VS3-11 | Propiedades, concurrencia y trazabilidad | INV-S, SAL-09 |

## 12. Decisiones (para aprobar como E-VS3-1…16)

| # | Decisión | Recomendación |
| --- | --- | --- |
| D-01 | ¿De dónde sale el producto terminado si aún no hay producción? | Solo del **saldo inicial por documento de apertura** (v2.1 §6), a costo estándar aprobado. La entrada por producción llega con el slice de manufactura. Nada de "entradas manuales" de PT que luego haya que deshacer |
| D-02 | ¿Se venden también productos comprados (reventa)? | No en VS#3: solo PT propio. La reventa se agrega luego con el mismo flujo |
| D-03 | Roles de venta | Cinco roles nuevos (Vendedor, Crédito, Despacho, Facturación, Cobros) con los SoD de la sección 7 |
| D-04 | ¿Cotización en VS#3? | No en el núcleo; el pedido se crea directo. Cotización como PR posterior (P0) |
| D-05 | Términos de entrega | Dos: **retira en planta** (control al portón) y **entregado en obra con camión propio** (control al POD) |
| D-06 | Evidencia del POD y del ticket de báscula | Nombre de quien recibe, fecha y hora, referencia y SHA-256 del archivo (foto o firma escaneada). El archivo se guarda en el almacenamiento de objetos, no en la base |
| D-07 | Facturas anticipadas (antes de entregar) | Fuera de VS#3. Los pagos por adelantado se registran como **cobro no aplicado** hasta que haya factura |
| D-08 | e-CF | VS#3 emite por el **canal externo del proveedor**: Core arma el paquete fiscal, el usuario emite en el portal y registra el e-NCF en Core (v2.1 §4.1). El Gateway automático es VS#4, después del contract test CT-01…16 |
| D-09 | Secuencias e-NCF | PROVIDER\_MANAGED en VS#3 (las asigna el portal); Core valida formato y unicidad |
| D-10 | ITBIS de ventas y hecho generador | Regla fiscal SALES\_ITBIS versionada (Tax Engine de VS#1). Mientras A-02 no confirme si el ITBIS nace con la entrega, la obligación se calcula a la **fecha de factura** y la conciliación señala los entregados sin facturar de más de 30 días |
| D-11 | Cobros | Transferencia, cheque y efectivo; efectivo y cheque en tránsito hasta el depósito; retención del cliente con certificado |
| D-12 | Devoluciones con reingreso de producto | Fuera de VS#3: nota de crédito solo comercial (precio, descuento, error) |
| D-13 | Confotur y autorizaciones fiscales | Slice fiscal propio después de VS#3 (v2 Decisión 2); VS#3 no vende con exención |
| D-14 | Exposición de crédito | CxC abierta + pedidos confirmados no entregados + entregado no facturado. Días de atraso que bloquean: parámetro de una política nueva CREDIT (A-01) |
| D-15 | Cierre de despachos del día (OP-DAY) | Advertencia de conduces en tránsito > 24 h; OP-DAY como componente bloqueante llega con producción |
| D-16 | Datos reales | Igual que E-VS1-2 / E-VS2-10: ninguno hasta B-02 o paralelo conciliado |
