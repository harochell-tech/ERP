# Fiscal #1b — La proforma como documento de cobro (enmienda a FIS-1)

**Estado: CONGELADO — aprobado por Alexander Rochell el 2026-10-01** (errata E-FIS1b-1…11 y E-FIS1b-01-1…14, con la opción
contable A). Enmienda el documento base de FIS-1 (`frozen-baseline-fis1.md`); lo que aquí no se menciona sigue igual.

## 1. Por qué

El cliente de un proyecto CONFOTUR recibe el material y lo paga según sus términos (30 días, por ejemplo) **mucho antes** de que
se pueda emitir el comprobante fiscal: la certificación de exención de la DGII tarda, cita el número de la proforma con la que el
cliente la solicitó y puede cubrir varias proformas. FIS-1 trataba la proforma como una impresión del pedido y dejaba lo
entregado sin facturar sin vencimiento y sin poder cobrarse: los cobros quedaban como anticipos y el crédito del cliente seguía
ocupado. Hechos del negocio (Alexander, 2026-10-01):

- Una proforma por cada conduce / entrega.
- La mayoría de los clientes paga la proforma **con ITBIS** y se le devuelve cuando sale la certificación; a otros se les cobra
  sin ITBIS y solo queda pendiente facturar.
- Una certificación puede cubrir varias proformas.
- Si la certificación no sale, se factura e-CF 31 y el ITBIS se cobra.

## 2. Alcance

| Dentro | Fuera |
| --- | --- |
| Proforma PF-000001, una por conduce entregado de un pedido marcado «exención en trámite» | Proformas de pedidos sin esa marca (siguen con conduce → factura) |
| Vencimiento, antigüedad y estado de cuenta en columna aparte; atraso para el crédito | Facturar media proforma (se factura completa) |
| Asignación de recibos a proformas y su reverso | Asiento al asignar (opción B, descartada) |
| Autorización fiscal con la lista de proformas que cubre; alcance calculado de sus líneas | Solicitud de la exención ante la DGII (la hace el cliente) |
| Factura desde proformas (e-CF 44 o 31 / 32) que hereda lo cobrado | Conversión de facturas ya emitidas con ITBIS |
| Devolución al cliente de su saldo a favor (DEV-000001), con dos personas | Devolución de producto con reingreso a inventario |
| Conciliaciones, pantallas, impresión y prueba de punta a punta | Envío del e-CF a la DGII (VS#4) |

## 3. Contabilidad (opción A)

La entrega ya registró el ingreso y el «entregado sin facturar» (P-16); la proforma solo le pone número, vencimiento y saldo a ese
importe, sin asiento. El recibo queda en anticipos de clientes (UNAPPLIED_RECEIPTS) hasta que exista la factura: asignarlo a una
proforma es un hecho del auxiliar (`fin.proforma_allocation`), no del mayor. Al emitir la factura, lo asignado se aplica a ella
con la regla de siempre (P-25) y el sobrante — el ITBIS adelantado cuando sale e-CF 44 — queda como saldo a favor del cliente,
que se devuelve o se asigna a otra proforma. Consecuencia aceptada: mientras no salga el e-CF, el balance general muestra por
separado el activo (entregado sin facturar) y el pasivo (anticipos) por el mismo importe; la antigüedad, el estado de cuenta y
el crédito muestran el neto.

## 4. Esquema (migración 0066)

| Tabla / columna | Reglas |
| --- | --- |
| `sal.sales_order.exemption_pending`, `proforma_collects_itbis` | La marca del pedido y si se cobra con ITBIS; solo cambian en borrador; nunca una marca del cliente |
| `sal.proforma` | PF-, una por conduce; fecha de entrega, vencimiento, neto, ITBIS, total, qué cobra (total o neto), asignado, estado OPEN → INVOICED (↔ OPEN si la factura se anula) / VOIDED; inmutable salvo estado y asignado |
| `sal.proforma_line` | Las líneas del conduce al precio del pedido, con su ITBIS; solo se insertan con la proforma |
| `fin.proforma_allocation` | Recibo → proforma, importe; el reverso es una fila inversa; mismo cliente |
| `fin.receipt.allocated_amount` | Lo asignado, nunca mayor que lo no aplicado; cero en un recibo devuelto o reversado |
| `tax.fiscal_authorization_proforma` | Las proformas que cita la certificación; solo mientras la autorización está en borrador; mismo cliente |

Permiso nuevo `proforma:void` (Facturación). Los demás pasos usan permisos existentes (`delivery:manage`, `receipt:apply`,
`invoice:create`, `invoice:issue`, `fiscal_authorization:register`) y los de la devolución, que llegan con su PR.

## 5. Pruebas de aceptación (propuestas)

| ID | Given | When | Then |
| --- | --- | --- | --- |
| PRF-01 | Pedido «exención en trámite, cobra con ITBIS» | Se entrega un conduce | PF-000001 OPEN: líneas del conduce, ITBIS de la regla vigente, vence a los días del cliente; sin asiento |
| PRF-02 | Pedido sin la marca | Se entrega | No hay proforma; el conduce se factura como siempre |
| PRF-03 | Proforma de 11,800.00 (cobra con ITBIS) | Recibo de 11,800.00 asignado | Saldo 0; el crédito usado baja; el recibo sigue como anticipo en el mayor |
| PRF-04 | Proforma que cobra sin ITBIS (neto 10,000.00) | Se asignan 10,500.00 | Rechazado: el saldo es el neto |
| PRF-05 | Proforma vencida con saldo | Pedido nuevo del cliente a crédito | Cuenta como atraso en la evaluación de crédito |
| PRF-06 | Dos proformas en una autorización ACTIVE | Factura desde las dos | e-CF 44; lo asignado se aplica hasta el total; el ITBIS adelantado queda a favor |
| PRF-07 | Proforma sin certificación, cobrada con ITBIS | Factura con ITBIS | e-CF 31 pagada: lo asignado cubre neto e ITBIS |
| PRF-08 | Proforma cobrada sin ITBIS | Factura con ITBIS | e-CF 31 con el ITBIS como saldo |
| PRF-09 | Saldo a favor tras una e-CF 44 | Cobros prepara la devolución y el Controller la libera (E-FIS1b-05-1); Tesorería la empareja con el extracto | DEV-000001; el mismo usuario no hace ambas; baja el anticipo contra banco |
| PRF-10 | Cheque asignado a una proforma | El cheque se devuelve | La asignación se deshace; la proforma recupera su saldo |
| PRF-11 | Conduce con proforma | Factura por la vía de conduces | Rechazado: se factura desde la proforma |
| PRF-12 | Proforma sin cobros ni factura | Anular con motivo | VOIDED; el conduce vuelve a la facturación normal |
| PRF-13 | Mes con proformas | Cierre de CxC | PROFORMA-ASIG y las conciliaciones existentes MATCHED |
| E2E-P1 | Flujo completo | API y UI | Pedido marcado → entrega → proforma → cobro con ITBIS → certificación con dos proformas → e-CF 44 → devolución |

## 6. Dependencias externas

| # | Qué | Quién | Bloquea |
| --- | --- | --- | --- |
| X-1 | Confirmar que el ITBIS cobrado contra una proforma se trata como depósito del cliente hasta el e-CF, y el momento en que nace el ITBIS cuando se entrega y cobra antes del comprobante | Contador | Datos reales |
| X-2 | Muestra real de una certificación que cite proformas | Alexander | Datos reales |

## 7. Plan de PRs

| PR | Contenido |
| --- | --- |
| FIS1b-01 | Esquema (migración 0066), permiso, este documento |
| FIS1b-02 | Marca en el pedido; proforma en la entrega; consultas e impresión |
| FIS1b-03 | Asignación de cobros; crédito, antigüedad y estado de cuenta |
| FIS1b-04 | Autorización con proformas; factura desde proformas que hereda lo cobrado; anulación |
| FIS1b-05 | Devolución al cliente y su conciliación con el extracto |
| FIS1b-06 | Conciliaciones |
| FIS1b-07 | Pantallas, impresión, Playwright y E2E-P1 |

## 8. Decisiones aprobadas

| # | Decisión |
| --- | --- |
| E-FIS1b-1 | Documento «Proforma», PF-000001 por empresa, una por conduce entregado, con líneas, neto, ITBIS calculado y total. No es comprobante fiscal ni genera asiento |
| E-FIS1b-2 | El pedido se marca «exención en trámite»; cada entrega suya genera su proforma. Nunca es una marca del cliente |
| E-FIS1b-3 | La proforma vence según los términos del cliente desde la entrega y entra a la antigüedad y al estado de cuenta en columna aparte |
| E-FIS1b-4 | Los recibos se asignan a proformas; lo que exceda el neto es depósito del cliente ligado a la proforma |
| E-FIS1b-5 | La autorización lista las proformas que cubre; el alcance se arma con sus líneas |
| E-FIS1b-6 | Se factura desde proformas: e-CF 44 con su autorización, 31 / 32 con ITBIS sin ella; toda exenta o toda gravada |
| E-FIS1b-7 | La factura hereda lo cobrado; con e-CF 44 el depósito de ITBIS queda como saldo a favor |
| E-FIS1b-8 | «Devolución al cliente» sobre su saldo a favor: la prepara Cobros, la libera el Controller (E-FIS1b-05-1, que enmienda «la libera Tesorería») |
| E-FIS1b-9 | Vista de proformas por cliente y aviso de proformas viejas sin e-CF (parámetro de política) |
| E-FIS1b-10 | Una proforma sin cobros ni factura se anula con motivo |
| E-FIS1b-11 | Conciliaciones que bloquean el cierre de CxC |
| E-FIS1b-01-1…14, E-FIS1b-05-1 | Ver `errata.md` |
