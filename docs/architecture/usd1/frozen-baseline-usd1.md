# USD-1 — Importaciones y proveedores extranjeros en dólares

Aprobado por Alexander Rochell el 2026-10-05 (erratas E-USD-1…15 en `../errata.md`; E-USD-10…15 ajustan el alcance tras las
respuestas del dueño: se importan piezas, repuestos, camiones y montacargas; el agente aduanal factura aparte; hay cuenta en USD; las
ventas en USD van en USD-2). Se construye sobre VS#1 (compras, recepción,
cuentas por pagar), VS#2 (pagos y bancos), GAS-1 (facturas de gastos), FIS-2 (606) y FIN-1 (catálogo y estados).

## 1. Por qué

Todo el sistema trabaja hoy en pesos: el mayor, las cuentas de banco, los pagos y las cuentas por pagar solo admiten DOP. Block Rochell
compra en el exterior (aditivos, repuestos, equipos) con facturas en dólares, paga fletes y seguros internacionales, aranceles y el ITBIS de
aduana, y paga a sus proveedores desde cuentas en pesos o en dólares. Hoy nada de eso se puede registrar y el costo de lo importado no
incluye lo que costó traerlo.

## 2. Alcance

- **Monedas** (E-USD-1): DOP y USD. La contabilidad sigue en pesos; cada documento en dólares guarda el monto en USD, la tasa y el
  equivalente en pesos.
- **Tasas** (E-USD-2): la tasa de venta del Banco Central por día, registrada por Tesorería o el Contador y aprobada por el Controller. Sin
  tasa del día no se registra un documento en dólares.
- **Qué se importa** (E-USD-10, E-USD-11): sobre todo piezas, repuestos, camiones y montacargas. Una importación es una compra de gastos
  (GAS-1) en USD: cada línea con su categoría — los repuestos a su categoría de gasto, camiones y montacargas a una categoría de activo fijo
  (cuenta del grupo de activos, tipo 606 «04»). Solo se capitaliza el costo; el registro de activos y su depreciación van en un módulo de
  activos fijos aparte. La materia prima importada, si la hay, sigue la compra de inventario.
- **Proveedor extranjero** (ya existe el tipo FOREIGN, sin RNC): órdenes de compra y facturas en USD, sin NCF, ITBIS ni retenciones; cuenta
  por pagar en USD con su equivalente en pesos a la tasa de la factura (E-USD-3).
- **Recepción** (E-USD-4): solo para materia prima, como hoy; el costo se completa con la liquidación.
- **Liquidación de importación** (E-USD-5, E-USD-12): por embarque, reúne la factura del proveedor, flete internacional, seguro, aranceles
  del DUA, honorarios del agente aduanal (factura local en pesos con NCF e ITBIS, categoría «Gestión aduanal», E-USD-13) y transporte local;
  los reparte por valor entre las líneas del embarque y suma cada parte a la cuenta de la categoría de la línea (gasto o activo) o al
  inventario si es materia prima.
- **DUA** (E-USD-6): el ITBIS pagado en la DGA se registra con su Declaración Única Aduanera como ITBIS adelantado y va al 606 como manda
  el instructivo (el contador confirma, X-1).
- **Pagos** (E-USD-7): desde una cuenta en USD o en pesos (con la tasa del banco ese día); la diferencia entre la tasa de la factura y la del
  pago va a diferencia cambiaria realizada.
- **Cierre de mes** (E-USD-8): los saldos abiertos en USD (cuentas por pagar y bancos en USD) se revalúan a la tasa del último día y se
  reversan el día siguiente.
- **Cuentas bancarias en USD** (E-USD-9), con su cuenta contable y extractos en USD.

Fuera de alcance: ventas en dólares, que tendrán su propia línea base (USD-2) por la factura electrónica en USD, los cobros y las cuentas
por cobrar en USD (E-USD-15).

## 3. Contabilidad

- Cuentas por pagar del exterior: rol de control propio (AP_FOREIGN) con el saldo en pesos; el subdiario lleva el USD.
- Mercancía recibida antes de liquidar: va a inventario al costo de la orden en pesos a la tasa de la recepción, contra «Importaciones por
  liquidar» (rol nuevo); la liquidación ajusta el valor del inventario (como la reliquidación de VS#1) y vacía esa cuenta.
- Diferencia cambiaria realizada y no realizada: roles FX_GAIN / FX_LOSS y FX_UNREALIZED; cuentas que aprueba el Controller (A-01).
- Reglas contables nuevas en DRAFT, aprobadas por el Controller (A-01).

## 4. Pruebas de aceptación

Ejemplo: tasa del día de la factura 60.00; un montacargas usado USD 8,000.00 (activo fijo) y repuestos USD 2,000.00 (gasto) =
USD 10,000.00.

| ID | Dado | Cuando | Entonces |
| --- | --- | --- | --- |
| USD-01 | Tasa del día preparada por Tesorería | La aprueba el Controller | Vigente; quien la preparó no puede aprobarla; sin tasa del día un documento en USD se rechaza |
| USD-02 | Proveedor extranjero | Orden de compra en USD | Aprobada como cualquier orden; montos en USD |
| USD-03 | Factura del proveedor USD 10,000.00 | Se contabiliza | Cuenta por pagar USD 10,000.00 = DOP 600,000.00 contra activo (480,000.00) y gasto de repuestos (120,000.00); sin NCF, ITBIS ni retención |
| USD-04 | Materia prima importada | Se recibe | Inventario a la tasa de la recepción contra «Importaciones por liquidar» |
| USD-05 | Flete USD 1,000.00, aranceles DOP 30,000.00, agente aduanal DOP 15,000.00 + ITBIS | Liquidación del embarque | DOP 105,000.00 repartidos por valor: 84,000.00 al activo y 21,000.00 a repuestos; costo total del montacargas 564,000.00 |
| USD-06 | DUA con ITBIS DOP 124,200.00 | Se registra | ITBIS adelantado; aparece en el 606 del mes |
| USD-07 | La factura de USD 10,000.00 | Pago desde cuenta en pesos a tasa 61.00 | Banco DOP 610,000.00; pérdida cambiaria realizada 10,000.00; cuenta por pagar en cero |
| USD-08 | Factura abierta USD 10,000.00, tasa de fin de mes 60.50 | Cierre | Pérdida no realizada 5,000.00, reversada el día siguiente |
| USD-09 | Cuenta bancaria en USD | Pago en USD y extracto en USD | Se paga y concilia en USD; el mayor en pesos cuadra |
| USD-10 | Mes con importaciones | Conciliaciones | AP-GL (pesos y USD), inventario, «Importaciones por liquidar» y bancos cuadran |
| E2E-U1 | Flujo completo | API y UI | Tasa → orden → factura → recepción → liquidación → DUA → pago con diferencia cambiaria → revaluación de cierre |

## 5. Dependencias externas

- **X-1**: el contador confirma el tratamiento del ITBIS de aduana en el 606 y la base de la diferencia cambiaria.
- **A-01**: el Controller aprueba cuentas, mapas y reglas contables nuevas.
- **Preguntas abiertas al dueño**: qué se importa y cuántos embarques al mes; si el agente aduanal factura aparte; si hay cuenta en USD;
  si hay ventas en dólares (fuera de alcance hasta que se pida).

## 6. Plan de PRs

| PR | Contenido |
| --- | --- |
| USD1-01 | Esquema: monedas, tasas, montos en USD en órdenes, facturas, cuentas por pagar, pagos y bancos; liquidación y DUA; roles nuevos |
| USD1-02 | Tasas de cambio: comandos, consultas, tasa del día |
| USD1-03 | Proveedor extranjero: orden y factura en USD, cuenta por pagar en USD, recepción contra «Importaciones por liquidar» |
| USD1-04 | Liquidación de importación (costo puesto en almacén) y DUA con ITBIS de aduana |
| USD1-05 | Pagos en USD o desde pesos con diferencia cambiaria; cuentas y extractos en USD |
| USD1-06 | Revaluación de cierre, conciliaciones y 606 de importaciones |
| USD1-07 | Pantallas, Playwright, E2E-U1 y matriz de aceptación |

## 7. Decisiones aprobadas

E-USD-1…9 en `../errata.md`.
