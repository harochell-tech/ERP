# GAS-1 — Compras de gastos y servicios, con tipo de impuesto por línea

Aprobado por Alexander Rochell el 2026-10-02 (erratas E-GAS-1…12 en `../errata.md`). Se construye sobre VS#1 (compras, cuentas
por pagar), VS#2 (pagos), FIS-2 (606) y FIN-1 (catálogo de cuentas).

## 1. Por qué

Hoy el sistema solo compra materias primas registradas: cada línea de la orden y de la factura apunta a un artículo, pasa por
almacén y se coteja contra la recepción; el ITBIS sale de una única regla según la categoría del artículo. Block Rochell también
compra combustible, mantenimiento, teléfono, seguros, honorarios y fletes: nada de eso entra a inventario y lleva impuestos
distintos (ITBIS 18 % o 16 %, exento, selectivo al consumo, CDT, propina legal). Esas facturas hoy no se pueden registrar.

## 2. Alcance

- **Línea de gasto** en órdenes de compra y facturas de proveedor: descripción libre, cantidad, precio, una **categoría de gasto**
  y un **tipo de impuesto**. Sin artículo, sin recepción de almacén, sin lote ni costo de inventario.
- Las materias primas siguen como hoy: artículo registrado, recepción, cotejo a tres vías e ITBIS automático por regla.
- Un documento es de inventario o de gastos; no mezcla líneas de las dos clases.
- **Categoría de gasto**: catálogo de la empresa. Cada categoría tiene su cuenta contable de gasto (no de control), su tipo de
  bienes y servicios del 606 y si es bien o servicio. La prepara una persona y la aprueba el Controller, como los mapas de cuentas.
  Quien registra una factura elige la categoría, nunca la cuenta.
- **Tipo de impuesto**: una regla fiscal por tipo, con su fuente oficial, activada por el Especialista fiscal. Varias vigentes a
  la vez; el desplegable muestra las vigentes en la fecha de la factura. Tipos iniciales: ITBIS 18 %, ITBIS 16 %, Exento,
  Telecomunicaciones (ITBIS 18 % + selectivo 10 % + CDT 2 %), Seguros (selectivo 16 %, sin ITBIS), Consumo con propina
  (ITBIS 18 % + propina 10 %). Ninguna tasa vive en el código.
- **Retenciones**: por regla fiscal y tipo de proveedor, como hoy; una regla de retención puede limitarse a líneas de gasto que son
  servicio (ISR a personas físicas por servicios, ITBIS retenido).
- **Factura con orden de compra de gastos**: se coteja contra la orden (cantidad pendiente de facturar y precio, con las
  tolerancias de la política de compras); no hay recepción.
- **Factura sin orden** (luz, teléfono, peajes): permitida solo para gastos. Desde el monto de la política de compras necesita la
  aprobación de otra persona (el Controller) antes de contabilizarse.
- La factura de gastos lleva su **planta**; genera su documento de cuentas por pagar y se paga, concilia y reversa como cualquier
  otra.
- **606**: la factura de gastos sale con su tipo de bienes y servicios, monto en servicios o en bienes, ITBIS facturado, selectivo
  (columna 20), otros impuestos y tasas (columna 21) y propina legal (columna 22).

Fuera de alcance: activos fijos y su depreciación, caja chica y reembolsos, compras en dólares e importaciones, comprobantes de
gastos menores y de consumo sin valor fiscal (B13, B02), ITBIS llevado al costo y proporcionalidad, anticipos a proveedores.

## 3. Contabilidad

Regla nueva **P-37** (componente de cierre AP-REC), aprobada por el Controller, para la factura de gastos:

| Lado | Cuenta | Monto |
| --- | --- | --- |
| Débito | La cuenta de la categoría de cada línea | Neto de la línea |
| Débito | ITBIS adelantado en compras (`ITBIS_RECOVERABLE`) | ITBIS de las líneas |
| Débito | Gasto de impuesto selectivo (`SELECTIVE_TAX_EXPENSE`) | Selectivo al consumo |
| Débito | Gasto por otros impuestos (`OTHER_TAX_EXPENSE`) | CDT y otras tasas |
| Débito | Propinas (`LEGAL_TIP_EXPENSE`) | Propina legal |
| Crédito | Cuentas por pagar proveedores (`AP_CONTROL`) | Neto + impuestos − retenciones |
| Crédito | Retenciones por pagar (`WITHHOLDING_PAYABLE`) | Retenciones |

La reversa (**P-37R**) es el asiento inverso exacto, por el Controller, con el documento de cuentas por pagar todavía abierto. La
orden de compra de gastos no contabiliza nada. Las facturas de inventario siguen con R-04 / R-05 / R-07.

Cuentas del catálogo de Block Rochell (A-01, las aprueba el Controller): selectivo → 63950, propina → 63900; faltan y se crean antes
de usar el módulo: ITBIS adelantado en compras (14400), otros impuestos y tasas (63960), peajes (63550) y las cuentas de planta y
flota (66xxx, lista en `../../engineering/expenses.md` cuando se cargue).

## 4. Esquema (primer PR)

- `pur.expense_category`: código, nombre, cuenta, tipo 606 ("01"…"11"), clase (bien o servicio), estado, quién preparó y aprobó.
- `pur.purchase_order` y `pur.supplier_invoice`: clase del documento (inventario o gastos); la factura de gastos, su planta.
- `pur.purchase_order_line` y `pur.supplier_invoice_line`: clase de línea `EXPENSE` con descripción, categoría y tipo de impuesto;
  artículo y línea de orden pasan a opcionales según la clase.
- Regla fiscal de clase `PURCHASE_TAX_TYPE` (varias activas, una por código) con sus componentes; efectos nuevos para selectivo,
  otros impuestos y propina.
- Roles de cuenta `SELECTIVE_TAX_EXPENSE`, `OTHER_TAX_EXPENSE`, `LEGAL_TIP_EXPENSE` y el rol técnico `PURCHASE_EXPENSE` (la cuenta
  la da la categoría, no un mapa).
- Reglas P-37 y P-37R en borrador; parámetro de política `expense_invoice_approval_threshold` (compras).
- Permisos `expense_category:prepare` y `expense_category:approve`.

## 5. Pruebas de aceptación

| ID | Dado | Cuando | Entonces |
| --- | --- | --- | --- |
| GAS-01 | Categoría «Reparaciones» preparada | La aprueba otra persona | ACTIVA; la misma persona no puede aprobarla; una cuenta de control se rechaza |
| GAS-02 | Tipos ITBIS 18 % y 16 % activos a la vez | Factura con una línea de cada uno | Cada línea lleva solo su tipo; el ITBIS es la suma de ambos |
| GAS-03 | Factura sin orden, 10,000.00 de reparaciones con ITBIS 18 %, bajo el monto de política | Se contabiliza | P-37: gasto 10,000.00, ITBIS adelantado 1,800.00, cuentas por pagar 11,800.00 |
| GAS-04 | Factura sin orden sobre el monto de política | Se verifica | Queda pendiente de aprobación; la aprueba el Controller, no quien la registró |
| GAS-05 | Línea de teléfono 5,000.00, tipo Telecomunicaciones | Se contabiliza | ITBIS 900.00 adelantado, selectivo 500.00 y CDT 100.00 a sus cuentas de gasto; por pagar 6,500.00 |
| GAS-06 | Línea de seguros 20,000.00, tipo Seguros | Se contabiliza | Selectivo 3,200.00 a gasto, sin ITBIS; por pagar 23,200.00 |
| GAS-07 | Consumo 2,000.00, tipo Consumo con propina | Se contabiliza | ITBIS 360.00 adelantado, propina 200.00 a gasto; por pagar 2,560.00 |
| GAS-08 | Línea de combustible, tipo Exento | Se contabiliza | Sin impuestos; por pagar igual al neto |
| GAS-09 | Proveedor persona física, servicio, reglas de retención activas | Se contabiliza | Retenciones a su cuenta; por pagar neto + impuestos − retenciones; no se retiene en una línea que es bien |
| GAS-10 | Orden de compra de gastos aprobada, 10 servicios a 1,000.00 | Factura de 6 y luego de 5 | La primera coteja y contabiliza; la segunda excede lo pendiente y se rechaza; no hay recepción |
| GAS-11 | Orden de compra de gastos | Recibir en almacén | No aparece en «Por recibir»; recibirla se rechaza |
| GAS-12 | Documento con líneas de inventario y de gasto | Crear | Rechazado |
| GAS-13 | Factura de gastos contabilizada y sin pagos | Reversa | P-37R: todo en cero; el NCF queda libre |
| GAS-14 | Mes con facturas de gastos | 606 | Tipo de bienes y servicios de la categoría, monto en servicios o bienes, ITBIS facturado, selectivo, otros impuestos y propina en sus columnas; TAX-606 cuadra |
| GAS-15 | Factura de gastos contabilizada | Pago y conciliación con el banco | Se paga y concilia como una de inventario; AP-GL cuadra |
| E2E-G1 | Flujo completo | API y UI | Categoría → factura de teléfono sin orden → aprobación → contabilización → pago → 606 |

## 6. Dependencias externas

- **X-1** — el contador confirma, con su norma: las tasas de cada tipo de impuesto y su base (si el ITBIS de telecomunicaciones se
  calcula sobre el servicio o sobre el servicio más el selectivo), que los seguros no llevan ITBIS, el tipo 606 de cada categoría y
  las retenciones aplicables a servicios.
- **A-01** — el Controller aprueba las categorías, las cuentas nuevas, P-37 / P-37R y el monto de aprobación de facturas sin orden.
- **B-02** — segundo revisor de los PR de contabilidad.

## 7. Plan de PRs

| PR | Contenido |
| --- | --- |
| GAS1-01 | Esquema |
| GAS1-02 | Tipos de impuesto: clase de regla, cálculo por línea, alcance de las retenciones, carga de los seis tipos con sus fuentes |
| GAS1-03 | Categorías de gasto: comandos, consultas y carga del catálogo de Block Rochell |
| GAS1-04 | Factura de gastos sin orden: registro, verificación y aprobación, P-37, reversa P-37R, cuentas por pagar |
| GAS1-05 | Orden de compra de gastos y factura contra la orden |
| GAS1-06 | 606 y conciliaciones (TAX-606, AP-GL) |
| GAS1-07 | Pantallas (categorías, orden y factura de gastos, desplegable de impuesto), Playwright, E2E-G1 y matriz de aceptación |

## 8. Decisiones aprobadas

E-GAS-1…12 en `../errata.md`.
