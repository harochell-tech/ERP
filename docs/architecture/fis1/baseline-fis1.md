# Fiscal #1 — Autorizaciones fiscales: ventas exentas a proyectos CONFOTUR

**Estado: PROPUESTA — pendiente de aprobación de Alexander Rochell** (redactada el 2026-09-29). Al aprobarse, las decisiones
D-01…D-16 pasan a errata E-FIS1-1…16 y este documento queda CONGELADO con la misma regla que los slices anteriores.

Fuentes: Architecture v2 Decisión 2 (autorización fiscal como submodelo, "nunca una bandera de cliente") y ADR-020; v2.1 §16 P-20 y
§18 (máquina de la autorización fiscal); v1 §139 y §439; VS#3 D-13 / E-VS3-13 (slice fiscal propio); E-VS3-05-1/2/8/9 (ITBIS de
ventas, fecha de factura, e-CF 31/32, paquete fiscal); E-VS3-06-2 (tasa de la nota de crédito); expediente A-02 y la investigación
de fuentes oficiales de 2026-09-29:

- Ley 158-01 art. 4 (c): exención de ITBIS en materiales para la construcción y el primer equipamiento del proyecto turístico
  (DGII, D); Ley 195-13 art. 9 (plazo: 15 años desde el fin de la construcción y el equipamiento, operar dentro de 3 años, V).
- La resolución de clasificación CONFOTUR **no basta**: el comprador presenta la **certificación de exención de ITBIS de la DGII**,
  emitida (vía Hacienda, Oficina Virtual) sobre una **proforma firmada y sellada por el suplidor** con los artículos de la lista
  de materiales aprobada. Sin certificación se factura con ITBIS (DGII, D).
- Se factura con **e-CF 44 (Regímenes Especiales)**: todas las líneas con IndicadorFacturacion = 4 (exento), sin campos de ITBIS;
  el e-CF no tiene un campo para el número de la certificación (formato e-CF v1.0, V).
- Para la proporcionalidad del ITBIS de compras, las ventas exentas "por destino" a regímenes especiales cuentan como gravadas
  (DGII CA3833, D).

## 1. Alcance y regla de congelamiento

| Dentro de FIS-1 | Fuera de FIS-1 |
| --- | --- |
| Autorización fiscal (certificación DGII) por cliente y proyecto, con alcance por artículo (cantidad y monto) y evidencia | Solicitud de la exención ante Hacienda / DGII (la hace el cliente) |
| Registro por Crédito o Facturación, verificación por el Especialista fiscal (cuatro ojos), suspensión, vencimiento, agotamiento | Otros regímenes especiales (zonas francas, gubernamental e-CF 45, exportación e-CF 46) |
| Proforma imprimible desde el pedido (lo que el cliente lleva a la DGII) | Cotizaciones como documento propio (slice de cotizaciones) |
| Factura exenta e-CF 44 desde conduces cubiertos por una autorización ACTIVE, con consumo por línea | Conversión retroactiva de facturas ya emitidas con ITBIS |
| Nota de crédito de una factura exenta (sin ITBIS) que devuelve el consumo; anulación que lo libera | Reportes 607 / IT-1 y proporcionalidad (slice de reportes fiscales) |
| Conciliación del consumo y alertas de vencimiento; pantallas; pruebas punta a punta | Validación en línea contra la DGII (no existe servicio público) |

Regla de congelamiento idéntica. E-VS1-2 aplica: **ningún dato real** hasta cerrar B-02 o un paralelo conciliado.

## 2. Esquema (nuevo, conceptual)

```sql
CREATE TABLE tax.fiscal_authorization (       -- una certificación DGII (por proforma)
  authorization_id, company_id, party_id (cliente), regime ('CONFOTUR'), certificate_no, issued_on, valid_until NULL,
  project_name, confotur_resolution_no, project_term_ends_on NULL, sales_order_id NULL (la proforma),
  status (DRAFT, PENDING_VERIFICATION, ACTIVE, SUSPENDED, EXHAUSTED, EXPIRED, REJECTED),
  registered_by, verified_by, version );
CREATE TABLE tax.fiscal_authorization_line (  -- alcance: artículo, cantidad y monto neto autorizados
  authorization_id, line_no, item_id, uom, qty_authorized, net_authorized, qty_consumed, net_consumed,
  CHECK (0 ≤ consumed ≤ authorized) );
CREATE TABLE tax.fiscal_authorization_document ( authorization_id, kind (CERTIFICADO_DGII, RESOLUCION_CONFOTUR, LISTA_MATERIALES,
  PROFORMA), evidence_ref, evidence_sha256, added_by );
CREATE TABLE tax.fiscal_authorization_consumption ( authorization_line, invoice_line_id, qty, net, reverses_consumption_id );
ALTER TABLE sal.invoice ADD fiscal_authorization_id NULL;   -- e-CF 44 ⇔ autorización
-- sal.invoice.ecf_type admite '44'; e-NCF ^E44[0-9]{10}$
```

Permisos nuevos: `fiscal_authorization:register` (Crédito, Facturación), `fiscal_authorization:verify` (Especialista fiscal),
`fiscal_authorization:suspend` (Especialista fiscal); lectura con `sales:read`. SoD: registrar ≠ verificar.

## 3. Agregados, comandos y eventos

| Agregado | Comandos | Eventos |
| --- | --- | --- |
| FiscalAuthorization | RegisterFiscalAuthorization, UpdateDraftAuthorization, AttachAuthorizationDocument, SubmitForVerification, VerifyAuthorization, RejectAuthorization, SuspendAuthorization, ReactivateAuthorization | FiscalAuthorizationRegistered, …Verified (ACTIVE), …Suspended, …Exhausted, …Expired |
| Invoice (VS#3, extendido) | CreateInvoiceFromDeliveries con `fiscalAuthorizationId`; IssueInvoice (e-CF 44); VoidUnfiscalizedInvoice | AuthorizationConsumed, AuthorizationReleased |
| CreditNote (VS#3, extendido) | IssueCreditNote de una factura e-CF 44 | AuthorizationReleased |

## 4. Máquina de estados

DRAFT → PENDING_VERIFICATION → **ACTIVE** (el Especialista fiscal verifica los documentos y la vigencia; step-up; ≠ quien
registró) → **SUSPENDED** ⇄ ACTIVE; **EXHAUSTED** cuando todo el alcance está consumido (vuelve a ACTIVE si una nota o anulación
libera consumo); **EXPIRED** al pasar `valid_until` (al usarla o por conciliación); **REJECTED** desde PENDING_VERIFICATION.

## 5. Transacciones y concurrencia

La emisión de la factura exenta bloquea la autorización (FOR UPDATE) antes que las líneas de conduce; dos facturas simultáneas
nunca consumen más que lo autorizado (CHECK consumed ≤ authorized). Suspender o vencer una autorización no toca facturas ya
emitidas.

## 6. Reglas de posteo

| Regla | Evento | Débito | Crédito | Notas |
| --- | --- | --- | --- | --- |
| P-18 | InvoiceIssued (exenta) | AR_CONTROL | CONTRACT_ASSET / UNBILLED_RECEIVABLE | La misma P-18 sin línea de ITBIS (monto cero); la determinación fiscal registra la autorización |
| P-22 | CreditNoteIssued (de una exenta) | SALES_DISCOUNTS | AR_CONTROL | Sin ITBIS (la tasa de la línea es 0) |

## 7. Conciliaciones

| Conciliación | A | B | Severidad |
| --- | --- | --- | --- |
| AUTH-CONSUMPTION | Consumo registrado por línea de autorización | Σ líneas de facturas e-CF 44 vivas − notas de crédito | ERROR (bloquea AR-REC) |
| AUTH-EXPIRY | Autorizaciones ACTIVE que vencen en ≤ 15 días o cuyo proyecto supera su plazo | — | WARNING |
| EXEMPT-WITHOUT-AUTH | Facturas sin ITBIS de artículos gravados sin autorización | — | ERROR (bloquea AR-REC) |

## 8. Pruebas de aceptación (propuestas)

| ID | Given | When | Then |
| --- | --- | --- | --- |
| FIS-01 | Autorización registrada por Crédito | El mismo usuario la verifica | Rechazado; el Especialista fiscal la verifica → ACTIVE |
| FIS-02 | Autorización ACTIVE por 1,000 bloques / 50,000.00 | Factura de 600 bloques del cliente | e-CF 44, ITBIS 0, P-18 sin ITBIS, consumo 600 / 30,000.00 |
| FIS-03 | Quedan 400 autorizados | Factura exenta de 500 | Rechazada (excede el alcance); con ITBIS se puede facturar normal |
| FIS-04 | Autorización de otro cliente o de otro artículo | Factura exenta | Rechazada |
| FIS-05 | Autorización SUSPENDED o vencida | Factura exenta | Rechazada; las facturas emitidas no cambian |
| FIS-06 | Factura exenta emitida | Nota de crédito de 100 | Sin ITBIS; el consumo baja 100 y la autorización vuelve a ACTIVE si estaba EXHAUSTED |
| FIS-07 | Dos facturas exentas simultáneas por el saldo completo | Emisión concurrente | Una sola consume |
| FIS-08 | Registro externo del e-CF | e-NCF E44… con totales iguales | ACCEPTED_EXTERNAL; un E31 para una factura 44 se rechaza |
| FIS-09 | Mes con ventas exentas | Cierre AR-REC | AUTH-CONSUMPTION y EXEMPT-WITHOUT-AUTH MATCHED |
| E2E-F1 | Flujo completo | API y UI | Proforma → registro → verificación → conduce → factura e-CF 44 → nota de crédito |

## 9. Dependencias externas

| # | Qué | Quién | Bloquea |
| --- | --- | --- | --- |
| X-1 | Confirmar con el contador las preguntas abiertas de la investigación (vigencia de la certificación, sustitutos, 607 / IT-1, número en InformacionAdicionalComprador) | Contador | Datos reales |
| X-2 | Muestra real (anonimizada) de una certificación DGII y de una resolución CONFOTUR para ajustar los campos | Alexander | Datos reales |

## 10. Plan de PRs (propuesto)

| PR | Contenido | Pruebas |
| --- | --- | --- |
| FIS1-01 | Esquema, permisos, e-CF 44 en facturas | Esquema, SoD |
| FIS1-02 | Comandos de la autorización (registro, documentos, verificación, suspensión) y consultas; proforma | FIS-01 |
| FIS1-03 | Factura exenta: consumo, e-CF 44, registro externo; nota de crédito y anulación liberan consumo | FIS-02…08 |
| FIS1-04 | Conciliaciones AUTH-CONSUMPTION, AUTH-EXPIRY, EXEMPT-WITHOUT-AUTH | FIS-09 |
| FIS1-05 | Pantallas, recorrido Playwright, E2E-F1 por API, matriz de aceptación | E2E-F1 |

## 11. Decisiones (para aprobar como E-FIS1-1…16)

| # | Decisión | Recomendación |
| --- | --- | --- |
| D-01 | ¿Qué habilita vender sin ITBIS? | Solo una **autorización fiscal ACTIVE** que registra la **certificación de exención de la DGII**; la resolución CONFOTUR sola no basta (DGII). Nunca una marca del cliente (v2 Decisión 2, ADR-020) |
| D-02 | Granularidad | **Una autorización por certificación** (por proforma), con alcance por **artículo, cantidad y monto neto**; los artículos no listados se facturan con ITBIS (la DGII no confirmó sustitutos) |
| D-03 | Quién registra y verifica | Registra Crédito o Facturación con los documentos (certificado, resolución, lista, proforma: referencia + SHA-256); **verifica el Especialista fiscal** (cuatro ojos, step-up) antes de ACTIVE |
| D-04 | Proforma | Vista imprimible del **pedido de venta** con ITBIS, RNC y firma / sello del suplidor, para que el cliente la presente; la autorización guarda el pedido de origen (opcional) |
| D-05 | Tipo de comprobante | **e-CF 44** con todas las líneas exentas; una factura es **toda exenta o toda gravada**: si un conduce mezcla artículos cubiertos y no cubiertos, se hacen dos facturas |
| D-06 | Determinación fiscal | El Tax Engine recibe la autorización: sin línea de ITBIS para lo cubierto y la autorización (número, id) guardada en los insumos de la determinación, para Explicar el asiento |
| D-07 | Asiento | Se reutiliza **P-18** (su línea de ITBIS queda en cero y no se escribe); no se crea P-20 (queda reservada) |
| D-08 | Consumo | Se descuenta al **emitir** la factura, en la misma transacción; la anulación antes del e-CF y la nota de crédito lo devuelven |
| D-09 | Vencimiento | `valid_until` de la certificación (si la trae) y `project_term_ends_on` (15 años de la Ley 195-13) informativo; una autorización vencida no se usa y avisa 15 días antes |
| D-10 | Entregas antes de la certificación | El hecho imponible sigue en la fecha de factura (E-VS3-10): el conduce puede esperar sin facturar (aviso de 30 días de VS#3); **no hay conversión** de facturas ya emitidas con ITBIS |
| D-11 | Nota de crédito de una factura exenta | e-CF 34 sin ITBIS (la tasa de la línea es 0); devuelve consumo |
| D-12 | Número de certificación en el e-CF | Va en el paquete fiscal y como `InformacionAdicionalComprador` sugerido; el formato no tiene campo propio (pendiente de confirmar, X-1) |
| D-13 | Roles y permisos | Permisos nuevos de la sección 2; sin roles nuevos |
| D-14 | Conciliaciones | AUTH-CONSUMPTION y EXEMPT-WITHOUT-AUTH bloquean AR-REC; AUTH-EXPIRY solo avisa |
| D-15 | Reportes y proporcionalidad | Fuera: los datos quedan listos para el 607 (NCF E44, ITBIS 0) y la proporcionalidad (ventas exentas por destino) del slice de reportes |
| D-16 | Datos reales | Igual que siempre: ninguno hasta B-02 o paralelo conciliado |
