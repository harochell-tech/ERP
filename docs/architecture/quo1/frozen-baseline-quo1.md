# Cotizaciones #1 — Cotización de venta y conversión en pedido

**Estado: CONGELADO — aprobado por Alexander Rochell el 2026-09-29**, con las decisiones D-01…D-14 tal como se recomiendan
(errata E-QUO1-1…14). Es la especificación; cualquier cambio sigue la regla de congelamiento.

Fuentes: Architecture v1 §104 / §133 (Order-to-Cash empieza en la cotización) y §747; v2 §733 (relación QUOTED_AS Cotización →
Pedido) y D7 (impresión de cotizaciones en planta y oficina); v2.1 §11 Sales (agregado `Quote`, comandos `CreateQuote`,
`ConvertQuote`); VS#3 D-04 / E-VS3-4 (cotización fuera del núcleo, «PR posterior»); VS#3 lista de precios versionada y pedido
(E-VS3-03); FIS-1 proforma desde el pedido (E-FIS1-4).

## 1. Alcance

| Dentro de QUO-1 | Fuera de QUO-1 |
| --- | --- |
| Cotización a un cliente (ACTIVE o en borrador, es decir, prospecto con RNC / cédula) con líneas de producto terminado, precio y validez | CRM: oportunidades, embudo, seguimiento, tasa de conversión |
| Precio de la lista vigente; precio especial por debajo de la lista con aprobación | Contratos de precio por cliente (VS#3 P1), descuentos escalonados por política |
| ITBIS informativo con la regla vigente (como la proforma) | Reserva de inventario o ATP |
| Envío (congela), pérdida con motivo, anulación; vencimiento por fecha | Envío por correo desde el sistema (no hay servicio de correo) |
| Conversión en un pedido DRAFT con los precios cotizados; el pedido sigue su flujo (crédito, despacho, factura) | Conversión parcial o en varios pedidos |
| Vista imprimible de la cotización; pantallas; pruebas punta a punta | Asientos, inventario, documentos fiscales (una cotización no postea nada) |

Regla de congelamiento idéntica. E-VS1-2 aplica: **ningún dato real** hasta cerrar B-02 o un paralelo conciliado.

## 2. Esquema (nuevo, conceptual)

```sql
CREATE TABLE sal.quote (
  quote_id, company_id, quote_no ('COT-000001'), party_id, plant_id, quote_date, valid_until,
  delivery_term_code, site_address NULL, customer_ref NULL, price_list_version_id (la vigente al crear),
  status (DRAFT, PENDING_APPROVAL, SENT, CONVERTED, LOST, CANCELLED), total_net, lines_version,
  created_by, price_approved_by NULL, sales_order_id NULL (QUOTED_AS), lost_reason NULL, version );
CREATE TABLE sal.quote_line ( quote_id, lines_version, line_no, item_id, uom, quantity, list_price, unit_price, net_amount,
  CHECK (unit_price > 0) );                -- special = unit_price < list_price
ALTER TABLE sal.sales_order ADD quote_id NULL UNIQUE;   -- el pedido nacido de la cotización (una sola vez)
```

Permisos nuevos: `quote:manage` (Vendedor: crear, editar, enviar, perder, anular, convertir) y `quote:approve_price` (Aprobador de
políticas, que ya aprueba la lista de precios); lectura con `sales:read`. SoD: `quote:manage` ≠ `quote:approve_price`.

## 3. Agregado, comandos y eventos

| Agregado | Comandos | Eventos |
| --- | --- | --- |
| Quote | CreateQuote, UpdateDraftQuote, SubmitQuoteForApproval, ApproveQuotePrices, ReturnQuoteToDraft, SendQuote, MarkQuoteLost, CancelQuote, ConvertQuote, CopyQuote | QuoteCreated, QuoteSubmitted, QuotePricesApproved, QuoteSent, QuoteLost, QuoteCancelled, QuoteConverted |
| SalesOrder (VS#3, extendido) | ConvertQuote crea el pedido DRAFT con `quote_id` | SalesOrderCreated (existente) |

## 4. Máquina de estados

DRAFT → (si alguna línea tiene precio especial) PENDING_APPROVAL → aprobada (vuelve a DRAFT con los precios aprobados) o devuelta
→ DRAFT; DRAFT sin precios especiales pendientes → **SENT** (congelada, imprimible) → **CONVERTED** (pedido creado) o **LOST**
(motivo); DRAFT / SENT → **CANCELLED** (motivo). Una cotización SENT con `valid_until` pasada se muestra **Vencida** y no se
convierte (estado derivado de la fecha, sin proceso diario).

## 5. Transacciones y concurrencia

`ConvertQuote` bloquea la cotización (FOR UPDATE), crea el pedido con sus líneas y marca CONVERTED en una transacción; el índice
único de `sales_order.quote_id` garantiza un solo pedido por cotización aunque dos conversiones lleguen a la vez.

## 6. Pruebas de aceptación (propuestas)

| ID | Given | When | Then |
| --- | --- | --- | --- |
| QUO-01 | Lista de precios vigente con BLOQUE-6 a 50.00 | El Vendedor cotiza 1,000 bloques | COT-000001 DRAFT, precio 50.00, neto 50,000.00, ITBIS informativo 9,000.00 |
| QUO-02 | Línea a 45.00 (precio especial) | Se intenta enviar | Rechazado; el Aprobador de políticas aprueba (no el Vendedor) y se envía |
| QUO-03 | Cotización SENT | Se intenta editar | Rechazado (congelada); CopyQuote crea un DRAFT nuevo con las mismas líneas |
| QUO-04 | Cotización SENT vigente de un cliente ACTIVE | ConvertQuote | Pedido DRAFT con los precios cotizados y `quote_id`; la cotización queda CONVERTED; el pedido pasa por crédito |
| QUO-05 | Cotización vencida, o de un cliente aún no ACTIVE | ConvertQuote | Rechazado |
| QUO-06 | Dos conversiones simultáneas | ConvertQuote concurrente | Un solo pedido |
| QUO-07 | Cotización SENT | MarkQuoteLost sin motivo / con motivo | Rechazado / LOST |
| E2E-Q1 | Flujo completo | API y UI | Cotización → precio especial aprobado → envío → impresión → conversión → pedido confirmado por crédito |

## 7. Dependencias externas

| # | Qué | Quién | Bloquea |
| --- | --- | --- | --- |
| X-Q1 | Formato impreso de la cotización (logo, condiciones, texto legal) | Alexander | Datos reales |

## 8. Plan de PRs (propuesto)

| PR | Contenido | Pruebas |
| --- | --- | --- |
| QUO1-01 | Esquema, permisos, SoD, numeración | Esquema, SoD |
| QUO1-02 | Comandos y consultas de la cotización, aprobación de precios, copia, vista imprimible | QUO-01…03, QUO-07 |
| QUO1-03 | Conversión en pedido y concurrencia | QUO-04…06 |
| QUO1-04 | Pantallas, recorrido Playwright, E2E-Q1 por API, matriz de aceptación | E2E-Q1 |

## 9. Decisiones (aprobadas como E-QUO1-1…14)

| # | Decisión | Recomendación |
| --- | --- | --- |
| D-01 | ¿A quién se cotiza? | A un **cliente del maestro**, ACTIVE o en borrador (el prospecto se crea como cliente DRAFT con RNC / cédula, como hoy). Sin prospectos de texto libre. Solo se convierte si el cliente está ACTIVE |
| D-02 | Precio | Por defecto el de la **lista vigente** al crear. El Vendedor puede bajarlo (precio especial) o subirlo |
| D-03 | Precio especial | Toda línea por debajo de la lista requiere aprobación del **Aprobador de políticas** antes de enviar (sin umbral en el código; A-01 puede pedir luego un umbral por política) |
| D-04 | Validez | El Vendedor pone `valid_until` (obligatoria, ≥ fecha). Vencida = SENT con fecha pasada: se muestra y no se convierte; no hay estado ni proceso diario |
| D-05 | ITBIS | Informativo con la regla SALES_ITBIS vigente (igual que la proforma); no se guarda determinación |
| D-06 | Crédito e inventario | La cotización no evalúa crédito ni reserva stock; el crédito se evalúa en el pedido (SubmitForCredit) |
| D-07 | Conversión | Toda la cotización en **un pedido DRAFT** con los precios cotizados, la planta, el término y la dirección; el pedido guarda `quote_id` (QUOTED_AS). El pedido se puede ajustar antes de enviarlo a crédito como cualquier borrador |
| D-08 | Cambios después de enviar | La cotización enviada queda congelada; para cambiarla se **copia** (CopyQuote) y la anterior se anula o se marca perdida |
| D-09 | Estados | DRAFT, PENDING_APPROVAL, SENT, CONVERTED, LOST, CANCELLED (sección 4) |
| D-10 | Numeración | `COT-` + 6 dígitos por empresa, como `PV-` |
| D-11 | Roles y permisos | `quote:manage` (Vendedor), `quote:approve_price` (Aprobador de políticas), lectura `sales:read`; SoD entre ambos; sin roles nuevos |
| D-12 | Impresión | Vista imprimible con emisor, cliente, líneas, ITBIS, total, validez y condiciones; el formato final lo da Alexander (X-Q1). Puede servir de proforma para la DGII antes del pedido |
| D-13 | Asientos y fiscal | Ninguno: la cotización no postea, no mueve inventario ni emite e-CF |
| D-14 | Pantallas | Ventas › Cotizaciones: lista (estado, cliente, vencidas), formulario, detalle con acciones, impresión y «Convertir en pedido»; contador en Inicio de precios por aprobar |
