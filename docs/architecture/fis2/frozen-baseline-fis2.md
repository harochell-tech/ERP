# Fiscal #2 — Reporte 606 y resúmenes para el IT-1 y el IR-17

**Estado: CONGELADO — aprobado por Alexander Rochell el 2026-09-29**, con las decisiones D-01…D-14 tal como se recomiendan
(errata E-FIS2-1…14). Es la especificación; cualquier cambio sigue la regla de congelamiento.

Fuentes (consultadas el 2026-09-29):
- NG 07-2018 (modificada por NG 10-18 y 05-2019): 606 compras, 607 ventas, 608 anulados, 609 pagos al exterior, a más tardar el
  día 15 del mes siguiente (expediente A-02, fila 16).
- DGII, *Instructivo Llenado y Remisión del Formato de Envío de Compras de Bienes y Servicios (606)*, actualizado 2026-02-12
  (dgii.gov.do › Formatos de Envío de Datos): 23 campos, códigos de tipo de bien o servicio (1–11), de retención ISR (1–9) y de forma
  de pago (1–7); la herramienta de Excel valida y genera el TXT `DGII_F_606_<RNC>_<AAAAMM>.TXT`, que se remite por la Oficina Virtual.
- DGII, Comunidad de Ayuda (respuestas oficiales, 2025–2026): un emisor **100 % electrónico no remite el 607 ni el 608** (las
  ventas e-CF y las anulaciones — notas de crédito, servicio de anulación — ya las recibe la DGII); **sí remite el 606** con NCF de
  serie B y E; los emisores electrónicos no envían formatos en cero.
- E-A02-2: la empresa ya es emisor electrónico; e-CF solamente desde el 2026-11-01. VS#3 D-04 / E-FIS1-15 dejaron 606/607/608 para
  este slice. Architecture v2 §245–246, §993–994 (606 ↔ AP, IT-1 / IR-17 desde las mismas líneas fiscales).

## 1. Alcance

| Dentro de FIS-2 | Fuera de FIS-2 |
| --- | --- |
| Reporte 606 del mes con las facturas de proveedor registradas en el sistema, en el orden de columnas de la herramienta de la DGII | 607 y 608 (no aplican a un emisor 100 % electrónico, fuente DGII); 609 (sin pagos al exterior sin comprobante) |
| Resumen del mes para el IT-1 (ventas por tipo de e-CF, ITBIS facturado, notas de crédito, ITBIS de compras, retenciones) | El formulario IT-1 / IR-17 oficial y su presentación (la hace el contador en la Oficina Virtual) |
| Resumen para el IR-17 (retenciones de ISR e ITBIS practicadas a proveedores) | Remisión automática a la DGII (no hay servicio público) |
| Conciliación 606 ↔ AP / GL del ITBIS de compras | Gastos que no pasan por el sistema (servicios, gastos menores e-CF 43, compras e-CF 41) hasta que existan sus procesos |

E-VS1-2 aplica: **ningún dato real** hasta cerrar B-02 o un paralelo conciliado.

## 2. Datos y reglas del 606 (propuesta)

| Campo 606 | De dónde sale |
| --- | --- |
| RNC o Cédula, Tipo Id | Proveedor (`md.party`): 1 = RNC (9 dígitos), 2 = cédula (11) |
| Tipo de Bienes y Servicios Comprados | Por categoría de ítem, mapa configurable por la empresa (valor inicial: materias primas → 09 «costo de venta») |
| NCF, NCF o Documento Modificado | `supplier_fiscal_number`; documento modificado para notas de crédito / débito de proveedor (hoy no existen en el sistema) |
| Fecha Comprobante, Fecha Pago | Fecha del NCF; fecha del pago aplicado (vacía si no está pagada) |
| Monto Facturado en Servicios / Bienes | Líneas de la factura: hoy todas son de inventario → bienes; servicios = 0 |
| ITBIS Facturado, ITBIS llevado al Costo | Determinación fiscal: ITBIS total; la parte NON_RECOVERABLE_INPUT va al costo |
| ITBIS sujeto a Proporcionalidad | 0 (todas las ventas cuentan como gravadas; las exentas CONFOTUR por destino también, DGII CA3833, E-FIS1-15) |
| ITBIS Retenido, Tipo de Retención en ISR, Monto Retención Renta | Determinación (efecto WITHHOLDING, ITBIS o ISR); el código de tipo de retención (1–9) se agrega a la definición de la regla PURCHASE_WITHHOLDING |
| ITBIS / ISR percibido, Selectivo, Otros impuestos, Propina | 0 (no aplican a las compras del sistema; los percibidos no están habilitados por la DGII) |
| Forma de Pago | Pagos aplicados: transferencia / cheque → 2; varios métodos → 7; sin pagar → 4 (compra a crédito) |

## 3. Pruebas de aceptación (propuestas)

| ID | Given | When | Then |
| --- | --- | --- | --- |
| F2-01 | Mes con 2 facturas de proveedor (una pagada por transferencia con retención, una a crédito) | 606 del mes | 2 registros con los 23 campos, códigos correctos, totales = facturas del mes |
| F2-02 | Factura revertida en el mes | 606 | No aparece |
| F2-03 | 606 del mes | CSV | Columnas en el orden de la herramienta DGII, montos con punto decimal, sin separador de miles |
| F2-04 | Mes con ventas e-CF 31/32/44 y notas de crédito | Resumen IT-1 | Ventas gravadas y exentas por tipo, ITBIS facturado = GL ITBIS_PAYABLE del mes |
| F2-05 | Retenciones practicadas en el mes | Resumen IR-17 | Totales por tipo = determinaciones de retención del mes |
| F2-06 | ITBIS del 606 ≠ ITBIS de compras en el GL | Conciliación TAX-606 | Excepción (advertencia) |
| E2E-F2 | Flujo completo | API y UI | Compra → factura → pago con retención → 606 y resúmenes → CSV |

## 4. Plan de PRs (propuesto)

| PR | Contenido | Pruebas |
| --- | --- | --- |
| FIS2-01 | Mapa de tipo de bien o servicio, código de retención en la regla, permiso, migración | Esquema |
| FIS2-02 | Consulta 606, CSV, resumen IT-1, resumen IR-17, conciliación TAX-606 | F2-01…06 |
| FIS2-03 | Pantallas (Fiscal › Reportes), E2E-F2 por API y Playwright, matriz de aceptación | E2E-F2 |

## 5. Decisiones (aprobadas como E-FIS2-1…14)

| # | Decisión | Recomendación |
| --- | --- | --- |
| D-01 | 607 y 608 | **No se generan**: la empresa es emisor electrónico y factura solo con e-CF desde el sistema (DGII). Si alguna vez vuelve a emitir serie B (contingencia fuera del sistema), el contador la reporta con la herramienta de la DGII. Confirmación del contador en X-1 |
| D-02 | Qué entra al 606 | Facturas de proveedor contabilizadas cuyo NCF es del mes; las revertidas no entran |
| D-03 | Período y pagos | Un registro por factura en el mes de su NCF, con la fecha de pago si ya está pagada al generar. Una retención pagada en un mes posterior se reporta en el mes del pago con su fecha de pago (el instructivo lo permite para NCF anteriores). **Confirmar con el contador (X-1)** |
| D-04 | Tipo de bien o servicio | Mapa por categoría de ítem que configura el Analista fiscal y activa el Especialista (como las reglas); valor inicial materias primas → 09 |
| D-05 | Bienes / servicios | Por tipo de línea: hoy todas las líneas son de inventario → bienes |
| D-06 | ITBIS al costo | La parte no recuperable de la determinación (efecto NON_RECOVERABLE_INPUT) |
| D-07 | Proporcionalidad | 0: todas las ventas cuentan como gravadas (incluidas las exentas CONFOTUR por destino, CA3833) |
| D-08 | Código de retención ISR | Nuevo campo `isr_withholding_type` (1–9) en la definición de las reglas PURCHASE_WITHHOLDING de ISR; lo configura el Analista y lo activa el Especialista (nueva versión de la regla) |
| D-09 | Forma de pago | De los pagos aplicados: transferencia / cheque → 2; más de un método → 7; sin pagar → 4 |
| D-10 | Formato de salida | Pantalla y **CSV en el orden de columnas de la herramienta de Excel de la DGII**, para pegarlo, validarlo y generar el TXT con la herramienta oficial. No generamos el TXT directamente hasta tener la especificación oficial del archivo |
| D-11 | Gastos fuera del sistema | El 606 del sistema cubre solo las compras registradas; la pantalla lo advierte y el contador agrega el resto en la herramienta de la DGII |
| D-12 | IT-1 e IR-17 | Resúmenes informativos del mes (no el formulario): ventas gravadas / exentas por tipo de e-CF, ITBIS facturado y de notas de crédito, ITBIS de compras (606), retenciones practicadas por tipo |
| D-13 | Conciliación | Nueva TAX-606 (advertencia): ITBIS del 606 del mes = ITBIS de compras en el GL; bloquea nada |
| D-14 | Permisos | Nuevo `fiscal_report:read` (READ) para Especialista fiscal, Analista fiscal, Contador, Controller, Auditor y Director |
