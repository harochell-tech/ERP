# CF-1 — Venta de contado a consumidor final (e-CF 32)

Aprobado por Alexander Rochell el 2026-10-02 (erratas E-CF1-1…14 y E-CF1-01-1…9 en `../errata.md`). Se construye sobre VS#3
(ventas, despacho y cobros) y FIS-1b (cobro asignado antes de la factura).

## 1. Por qué

Block Rochell vende a compradores sin RNC ni cédula, siempre de contado. Hasta hoy toda venta exige un cliente con RNC o cédula,
términos de crédito aprobados y evaluación de crédito, y la factura se cobra después de la entrega. Desde el 2026-11-01 solo se
emite e-CF: estas ventas se facturan como e-CF 32 (consumo).

## 2. Alcance

- Un cliente único por empresa, «Consumidor final», sin RNC, creado por el sistema; no se edita ni tiene términos de crédito.
- En cada venta se escribe, opcional, quién compra: nombre, teléfono e identificación (cédula, RNC o pasaporte). La identificación
  es obligatoria desde el monto que fije la regla fiscal `CONSUMER_ID_THRESHOLD`, con su fuente oficial. Sin esa regla vigente no
  se confirma ninguna venta a consumidor final.
- El pedido de contado no pasa por crédito: BORRADOR → PENDIENTE DE PAGO → CONFIRMADO cuando los recibos asignados cubren el neto
  más el ITBIS de la regla vigente el día en que se envió a pago. El efectivo y la transferencia cuentan al registrarse; el cheque,
  solo cuando su depósito está conciliado con el extracto del banco.
- El cliente paga el pedido completo y puede retirar en varios conduces, en planta o entregado en camión propio. Cada conduce se
  factura como e-CF 32 y el cobro asignado al pedido se aplica a cada factura al emitirla.
- Nota de crédito e-CF 34 sobre la E32; el dinero se devuelve con la devolución al cliente de FIS-1b (prepara Cobros, libera el
  Controller; siempre por banco, nunca en efectivo). Cancelar un pedido ya pagado libera sus asignaciones y deja el dinero para devolver.
- Sin exención CONFOTUR, autorización fiscal ni proforma para consumidor final.
- Rol «Caja»: hace la venta de contado y registra y asigna el cobro. El depósito y la conciliación con el banco siguen en Cobros y
  Tesorería.

Fuera de alcance: el resumen de e-CF 32 de bajo monto ante la DGII (VS#4, proveedor de e-CF), el servicio de transporte (SRV-1),
las listas de precios por cliente (PRC-1) y el arqueo de caja.

## 3. Contabilidad

Nada nuevo se contabiliza. El recibo sigue P-23 (banco o efectivo en tránsito contra cobros no aplicados); asignarlo a un pedido
es un hecho del auxiliar, sin asiento. La entrega reconoce ingreso y activo de contrato (P-16); la factura, P-18; al emitirla, lo
asignado al pedido se aplica con P-25. La devolución es P-36.

## 4. Esquema (migración 0071)

- `md.party.party_kind = 'CONSUMER'`: uno por empresa, sin RNC, cliente y nunca proveedor, sin datos de contacto, sin términos.
- `sal.sales_order`: `cash_sale` (verdadero solo y siempre para el consumidor final), `buyer_name`, `buyer_phone`,
  `buyer_id_kind` (CEDULA, RNC, PASAPORTE), `buyer_id`, `payment_total`, `allocated_amount`; estado `PENDING_PAYMENT`.
- `fin.order_allocation`: recibo asignado a un pedido de contado; liberar es una fila inversa. `fin.receipt.allocated_amount`
  cuenta proformas y pedidos.
- `sal.invoice`: `buyer_name`, `buyer_id_kind`, `buyer_id` (solo e-CF 32).
- `tax.external_fiscal_record`: `receiver_rnc` opcional y `receiver_passport`, solo para la E32 del consumidor final.
- Regla fiscal `CONSUMER_ID_THRESHOLD`; permiso `cash_sale:create` (Vendedor, Caja); rol `CAJA`.

## 5. Pruebas de aceptación

| ID | Dado | Cuando | Entonces |
| --- | --- | --- | --- |
| CF-01 | Empresa sin consumidor final | Primera venta de contado | Se crea «Consumidor final» una sola vez; el pedido queda en borrador con su comprador |
| CF-02 | Pedido de contado de 5,000.00 neto | Se envía a pago | PENDIENTE DE PAGO por 5,900.00 (ITBIS del día); no pasa por crédito |
| CF-03 | Pedido pendiente de pago | Recibo en efectivo de 5,900.00 asignado | CONFIRMADO; sin asiento más allá del recibo |
| CF-04 | Pedido pendiente de pago | Cheque asignado, sin depositar o sin conciliar | Sigue pendiente; se confirma al conciliar el depósito con el extracto |
| CF-05 | Pedido de contado sin pagar completo | Planificar conduce | Rechazado |
| CF-06 | Venta sobre el monto de la regla, sin identificación | Enviar a pago | Rechazado: falta la identificación del comprador |
| CF-07 | Sin regla `CONSUMER_ID_THRESHOLD` vigente | Enviar a pago | Rechazado con un mensaje claro |
| CF-08 | Pedido pagado, retirado en dos conduces | Factura de cada conduce | Dos e-CF 32 con el comprador; cada una nace cobrada con lo asignado al pedido |
| CF-09 | Factura E32 sin identificación | Registrar el e-CF | Se acepta sin receptor; con pasaporte, se guarda el pasaporte |
| CF-10 | Factura E32 cobrada | Nota de crédito y devolución | e-CF 34; el dinero se devuelve con DEV-… liberada por el Controller |
| CF-11 | Pedido pagado sin entregas | Cancelar | Las asignaciones se liberan; el recibo queda disponible para devolver |
| CF-12 | Mes con ventas de contado | Cierre de CxC | La conciliación de ventas de contado y las existentes MATCHED; una entrega sin cobro completo bloquea |
| E2E-C1 | Flujo completo | API y UI | Venta de contado → cobro → conduce → e-CF 32 → cierre |

## 6. Dependencias externas

- **X-1** — el contador confirma el monto desde el cual la identificación del comprador es obligatoria y la norma que lo fija.
- **VS#4** — el proveedor de e-CF y el envío resumido de las E32 de bajo monto.
- **A-01** — el Controller no aprueba reglas nuevas: CF-1 no agrega reglas de contabilización.

## 7. Plan de PRs

| PR | Contenido |
| --- | --- |
| CF1-01 | Esquema (migración 0071) |
| CF1-02 | Venta de contado: consumidor final, pedido, envío a pago, asignación de recibos, confirmación |
| CF1-03 | Factura e-CF 32 con comprador, aplicación de lo asignado, registro del e-CF sin receptor, nota de crédito, cancelación y devolución |
| CF1-04 | Conciliaciones y cierre |
| CF1-05 | Pantalla «Venta de contado», rol Caja, Playwright, E2E-C1 y matriz de aceptación |

## 8. Decisiones aprobadas

E-CF1-1…14 y E-CF1-01-1…9 en `../errata.md`.
