# PRS-1 — Listas de precios por cliente (PRC-1) y flete de entrega (SRV-1)

Aprobado por Alexander Rochell el 2026-10-05 (erratas E-PRC1-1…11 y E-SRV1-1…19 en `../errata.md`). Se construye sobre VS#3
(pedidos, entregas, facturación, cobros), QUO-1 (cotizaciones), FIS-1 / FIS-1b (CONFOTUR, proformas) y CF-1 (venta de contado).

## 1. Por qué

Hoy hay una sola lista de precios para toda la empresa: todos los clientes compran al mismo precio y lo distinto se negocia en una
cotización. Block Rochell tiene clientes con precios propios (hoteles, contratistas, CONFOTUR). Además, cuando entrega con su propio
camión cobra un flete que depende del producto y de la zona (Bávaro, Cap Cana, Miches…), que hoy no se puede facturar: cada línea de
pedido, conduce y factura es un producto que sale del inventario.

## 2. Alcance

**PRC-1 — listas por cliente**
- Listas con nombre («General», «CONFOTUR», «Hoteles»…), cada una con sus versiones; se preparan (Controller) y aprueban (Aprobador
  de políticas) como hoy; una versión vigente por lista (E-PRC1-1, E-PRC1-5).
- La lista actual pasa a ser «General» y todos los clientes empiezan en ella (E-PRC1-4).
- La lista del cliente forma parte de sus condiciones comerciales: Crédito la prepara y el Controller la aprueba junto con días y
  límite de crédito (E-PRC1-2, E-PRC1-6).
- Pedidos y cotizaciones toman el precio de la lista del cliente vigente al crearse o modificarse; lo que falta en ella toma el de
  «General»; si tampoco está, se rechaza. El precio queda fijo en el documento (E-PRC1-3, E-PRC1-7, E-PRC1-8).
- La aprobación de precio especial de las cotizaciones compara contra ese precio (E-PRC1-9). La venta de contado usa «General»
  (E-PRC1-10).

**SRV-1 — flete**
- Tipo de artículo «Servicio», categoría «Transporte»: sin inventario, sin costo estándar, sin receta; un artículo de flete por
  empresa («Transporte de blocks») (E-SRV1-1, E-SRV1-8).
- Maestro de zonas de entrega, mantenido en pantalla por Controller o Crédito sin aprobación (E-SRV1-2, E-SRV1-9).
- Cada versión de lista lleva su tabla de flete: producto, unidad, zona → precio por unidad del producto (E-SRV1-10).
- El pedido con nuestro camión elige zona (obligatoria); en recogida no hay zona ni flete (E-SRV1-11).
- El sistema agrega una línea de flete por cada producto con precio de flete en la lista **propia** del cliente para esa zona, sin
  recurrir a «General»; sin precio, no hay línea (el flete va incluido en el precio del block) (E-SRV1-3, E-SRV1-12).
- Un pedido con exención en trámite o con autorización fiscal nunca lleva flete (E-SRV1-6, E-SRV1-17).
- El conduce muestra el flete junto a cada producto con los blocks entregados; el flete no mueve inventario (E-SRV1-4, E-SRV1-13).
- La cotización también elige zona y muestra el flete; al convertirla el pedido lo conserva (E-SRV1-19).

## 3. Contabilidad y fiscal

- Ingreso del flete reconocido al traspasar el control de la entrega (P-16): blocks entregados × precio del flete, contra el mismo
  activo contractual o cuenta por cobrar no facturada, en el rol nuevo `FREIGHT_REVENUE`; sin costo de ventas (E-SRV1-7, E-SRV1-14).
- Cuenta nueva 40500 «Ingresos por transporte», mapeada a `FREIGHT_REVENUE`; la aprueba el Controller (A-01) (E-SRV1-18).
- La factura de cada conduce lleva la línea de flete aparte, exenta de ITBIS; P-18 la liquida contra el activo contractual como a
  las demás. Proformas y ventas de contado la incluyen (ITBIS 0); una nota de crédito puede rebajarla (P-22) (E-SRV1-15).
- La exención es la regla fiscal de ITBIS de ventas con «Transporte» entre las categorías exentas y su fuente oficial; el contador
  la confirma (X-1). Nada de tasas en el código (E-SRV1-5, E-SRV1-16).
- Las conciliaciones CONTRACT-ASSET, CASH-SALE, EXEMPT-WITHOUT-AUTH y PROFORMA-ASIG cuentan el flete.

## 4. Pruebas de aceptación

Ejemplo: lista «General» con BLOQUE-6 a 45.00 y BLOQUE-8 a 60.00; lista «Hoteles» con BLOQUE-6 a 42.00 y flete de BLOQUE-6 a
Bávaro 3.00 por block; ITBIS de ventas 18 % con «Transporte» exento.

| ID | Dado | Cuando | Entonces |
| --- | --- | --- | --- |
| PRC-01 | La lista única de hoy y sus clientes | Se migra | Queda «General», con sus precios; todos los clientes en «General» |
| PRC-02 | Lista «Hoteles» preparada por el Controller | La aprueba el Aprobador de políticas | Vigente; quien la preparó no puede aprobarla |
| PRC-03 | Cliente pasado a «Hoteles» por Crédito | Antes y después de que el Controller lo apruebe | Antes sus pedidos usan «General»; después, «Hoteles» |
| PRC-04 | Cliente en «Hoteles» | Pedido de BLOQUE-6 y BLOQUE-8 | BLOQUE-6 a 42.00 (su lista), BLOQUE-8 a 60.00 («General») |
| PRC-05 | Producto que no está en ninguna lista | Pedido | Rechazado: falta el precio |
| PRC-06 | Pedido hecho con «Hoteles» | Se aprueba otra versión de «Hoteles» | El pedido conserva sus precios; uno nuevo toma los nuevos |
| PRC-07 | Cliente en «Hoteles» | Cotización de BLOQUE-6 a 41.00 | Precio especial: requiere aprobación (compara contra 42.00) |
| PRC-08 | Venta de contado | Se crea | Precios de «General» |
| SRV-01 | Artículo «Transporte de blocks» (Servicio) | Se intenta poner en receta, costo estándar o inventario inicial | Rechazado |
| SRV-02 | Zonas Bávaro y Miches; Miches desactivada | Pedido | Solo Bávaro se puede elegir |
| SRV-03 | Cliente en «Hoteles», nuestro camión, Bávaro | Pedido de 1,000 BLOQUE-6 | Línea de flete 1,000 × 3.00 = 3,000.00; total neto 45,000.00 |
| SRV-04 | «General» tiene flete a Bávaro y «Hoteles» no lo tiene para BLOQUE-8 | Pedido de BLOQUE-8 a Bávaro | Sin línea de flete |
| SRV-05 | Recogida en planta | Pedido | Sin zona ni flete |
| SRV-06 | Pedido con exención en trámite o con autorización CONFOTUR | Con nuestro camión a Bávaro | Sin flete |
| SRV-07 | El pedido de SRV-03 | Entrega de 600 con POD | P-16: ingreso de blocks 25,200.00 y de flete 1,800.00 (`FREIGHT_REVENUE`), costo solo de los blocks; el conduce muestra el flete 600 × 3.00 |
| SRV-08 | Esa entrega | Se factura | Línea de BLOQUE-6 25,200.00 + ITBIS 4,536.00; flete 1,800.00 exento; total 31,536.00; P-18 liquida 27,000.00 de activo contractual |
| SRV-09 | Venta de contado con nuestro camión a una zona con flete en «General» | Se envía a pago | El total incluye el flete sin ITBIS |
| SRV-10 | Factura con flete | Nota de crédito sobre la línea de flete | P-22 por el monto; ITBIS 0 |
| SRV-11 | Pedido con proformas (exención en trámite) | — | Sin flete (SRV-06); un pedido con proforma por ITBIS cobrado y nuestro camión lleva el flete en la proforma con ITBIS 0 |
| SRV-12 | Mes con entregas y facturas con flete | Conciliaciones | CONTRACT-ASSET, CASH-SALE, EXEMPT-WITHOUT-AUTH y AR-GL cuadran |
| SRV-13 | Cotización con zona Bávaro | Se convierte en pedido | El pedido conserva zona y flete cotizados |
| E2E-PR1 | Flujo completo | API y UI | Lista «Hoteles» → cliente en ella → pedido con flete → entrega → factura con flete exento |

## 5. Dependencias externas

- **X-1**: el contador confirma que el flete de carga va exento de ITBIS y con qué norma.
- **A-01**: el Controller aprueba la cuenta 40500 y su mapa, las listas y los precios de flete.

## 6. Plan de PRs

| PR | Contenido |
| --- | --- |
| PRS-01 | Esquema: listas con nombre («General» para todos), lista en las condiciones del cliente, zonas, tipo Servicio y categoría Transporte, precios de flete, flete en pedido, conduce, factura, proforma y cotización, rol `FREIGHT_REVENUE` |
| PRS-02 | Listas por cliente: comandos de listas, lista en condiciones comerciales, precios de pedidos, cotizaciones y contado |
| PRS-03 | Zonas, artículo de flete y precios de flete en las listas |
| PRS-04 | Flete en pedido → conduce → ingreso al entregar → factura, proforma y contado exentos, notas de crédito, conciliaciones |
| PRS-05 | Pantallas, Playwright, E2E-PR1 y matriz de aceptación |

## 7. Decisiones aprobadas

E-PRC1-1…11 y E-SRV1-1…19 en `../errata.md`.
