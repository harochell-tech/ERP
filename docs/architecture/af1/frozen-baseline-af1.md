# AF-1 — Activos fijos: registro y depreciación

Aprobado por Alexander Rochell el 2026-10-06 (erratas E-AF-1…12 en `../errata.md`). Se construye sobre GAS-1 (categorías de gasto y
facturas de gastos), USD-1 (categorías de activo fijo con tipo 606 «04», facturas del exterior y liquidaciones de importación) y FIN-1
(catálogo, estados y cierre).

## 1. Por qué

Desde USD-1 los camiones, montacargas y equipos se capitalizan: la factura y la liquidación de importación llevan su costo a una cuenta de
activo. Pero no hay registro de cada activo, ni depreciación, ni bajas: el balance muestra el costo sin desgaste y el estado de resultados
no carga la depreciación. Tampoco hay forma de cargar los activos que la empresa ya tiene.

## 2. Alcance

- **Depreciación contable** (E-AF-1): registro de activos, depreciación lineal mensual, puesta en servicio, traslados entre plantas, bajas y
  ventas, y carga de los activos existentes. La depreciación fiscal de la DGII (categorías del art. 287) queda fuera: será un informe aparte
  cuando el contador la defina (X-1).
- **Clases de activo** (E-AF-2): cada categoría de gasto de activo fijo (tipo 04) se completa con vida útil en meses, porcentaje de valor
  residual, cuenta de depreciación acumulada y cuenta de gasto de depreciación. La prepara el Contador y la aprueba el Controller.
- **Alta** (E-AF-3): la ficha nace al contabilizar una factura (local o del exterior) con una línea en una categoría de activo fijo; una ficha
  por línea (con cantidad mayor que 1 agrupa esas unidades).
- **Costos posteriores** (E-AF-4): lo que una liquidación de importación agrega a la línea se suma al costo de la ficha; si ya se deprecia,
  el costo nuevo se reparte en la vida restante; la reversa de la liquidación lo quita.
- **Puesta en servicio** (E-AF-5): el Contador registra fecha, planta y responsable; la depreciación empieza el mes siguiente. Antes, la ficha
  está «en espera» y no se deprecia.
- **Depreciación del mes** (E-AF-6): el Contador la registra una vez por mes para todos los activos en servicio. Cuota = (costo − residual −
  acumulada) ÷ meses restantes, a 2 decimales; el último mes cierra exacto. Se puede deshacer mientras el período esté abierto.
- **Baja y venta** (E-AF-7): la prepara el Contador y la aprueba el Controller (otra persona). Desecho: el valor en libros a «Pérdida en baja
  de activos». Venta: el precio a la cuenta puente «Venta de activos por cobrar»; la diferencia con el valor en libros, a ganancia o pérdida.
  La factura al comprador se emite como cualquier factura (e-CF).
- **Activos existentes** (E-AF-8): carga inicial por CSV (código, descripción, clase, planta, fecha de compra, costo y depreciación acumulada a
  la fecha de corte) contra la contrapartida de saldos de apertura; la prepara el Contador y la aprueba el Controller.
- **Traslado entre plantas** (E-AF-9): cambia la planta desde una fecha, sin asiento; la depreciación siguiente se carga a la planta nueva.
- **Conciliación FA-GL** (E-AF-10): por clase, Σ costo de las fichas = cuenta de activo y Σ depreciación acumulada = su cuenta; avisa de meses
  sin depreciación. Componente de cierre nuevo «FA-REC».
- **Permisos** (E-AF-11): `fixed_asset:manage` (Contador), `fixed_asset:approve` (Controller), separados; lectura con `ledger:read`.
- **Pantallas** (E-AF-12): Contabilidad › Activos fijos (lista, ficha con historial, puesta en servicio, traslado, baja y venta, depreciación
  del mes, carga inicial); en Inicio «Activos por poner en servicio», «Depreciación del mes pendiente» y «Bajas por aprobar».

Fuera de alcance: depreciación fiscal (art. 287), revaluación de activos, deterioro, activos en construcción, arrendamientos y mantenimiento.

## 3. Contabilidad

- La ficha no crea asiento al nacer: el costo ya está en la cuenta de activo de su categoría (P-37, P-38, P-40).
- **P-44 Depreciación**: débito al gasto de depreciación de la clase (dimensión planta), crédito a la depreciación acumulada de la clase.
- **P-45 Baja o venta**: débito a la depreciación acumulada, débito a «Venta de activos por cobrar» (precio) o a «Pérdida en baja de activos»,
  crédito a la cuenta de activo por el costo; la diferencia a «Ganancia en venta de activos» o «Pérdida en baja de activos».
- **Carga inicial**: débito a la cuenta de activo por el costo, crédito a la depreciación acumulada, contra MIGRATION_CLEARING.
- Roles nuevos: depreciación acumulada y gasto de depreciación por clase (cuentas de la clase, no del mapa), «Venta de activos por cobrar»,
  «Ganancia en venta de activos», «Pérdida en baja de activos». Reglas en DRAFT hasta que el Controller las apruebe (A-01).

## 4. Pruebas de aceptación

Ejemplo: el montacargas de USD-1 cuesta 564,000.00 (480,000.00 de la factura + 84,000.00 de la liquidación); clase «Montacargas y equipos»
con vida útil de 60 meses y 10 % de valor residual (56,400.00): cuota mensual 8,460.00.

| ID | Dado | Cuando | Entonces |
| --- | --- | --- | --- |
| AF-01 | Clase preparada por el Contador | La aprueba el Controller | Vigente; quien la preparó no puede aprobarla; una clase sin vida útil o sin cuentas no se aprueba |
| AF-02 | Factura con una línea de activo fijo | Se contabiliza | Nace la ficha «en espera» con el costo de la línea; una línea de gasto no crea ficha |
| AF-03 | Liquidación que agrega 84,000.00 a la línea | Se contabiliza / se reversa | La ficha pasa a 564,000.00 / vuelve a 480,000.00 |
| AF-04 | Ficha en espera | Puesta en servicio el 15 del mes | Primera depreciación el mes siguiente; el mes de la puesta no deprecia |
| AF-05 | Montacargas en servicio | Depreciación del mes | 8,460.00 a gasto de depreciación (planta) contra depreciación acumulada; una sola vez por mes; se puede deshacer |
| AF-06 | Costo agregado con la ficha ya depreciándose | Siguiente depreciación | El costo nuevo se reparte en los meses restantes |
| AF-07 | Último mes de vida | Depreciación | La acumulada llega exacta a costo − residual |
| AF-08 | Montacargas con 12 meses depreciados (101,520.00) | Venta en 450,000.00 aprobada por el Controller | Valor en libros 462,480.00; pérdida 12,480.00; la ficha queda dada de baja y no se deprecia más |
| AF-09 | Camión existente: costo 2,400,000.00, acumulada 960,000.00, 96 meses de vida, comprado hace 36 | Carga inicial aprobada | Ficha en servicio; cuota 20,000.00 por los 60 meses restantes; asiento contra la contrapartida de apertura |
| AF-10 | Traslado a otra planta | Depreciación siguiente | Se carga a la planta nueva; sin asiento por el traslado |
| AF-11 | Mes con activos | Conciliación FA-GL | Costo y acumulada por clase cuadran con el mayor; un mes sin depreciación avisa; FA-REC bloquea el cierre con diferencias |
| E2E-AF1 | Flujo completo | API y UI | Clase → factura con activo → liquidación → puesta en servicio → depreciación → venta → conciliación |

## 5. Dependencias externas

- **X-1**: el contador define la depreciación fiscal (art. 287) y si la venta de activos lleva ITBIS en la factura.
- **A-01**: el Controller aprueba las clases (vidas útiles, residuales, cuentas), las cuentas y mapas nuevos y las reglas P-44 / P-45.
- **Dueño**: la lista de activos existentes con su costo y depreciación a la fecha de corte para la carga inicial.

## 6. Plan de PRs

| PR | Contenido |
| --- | --- |
| AF1-01 | Esquema: clase de activo (en la categoría), ficha, movimientos (alta, costo, servicio, traslado, depreciación, baja), corridas de depreciación, carga inicial; roles, permisos, reglas P-44 / P-45 en DRAFT |
| AF1-02 | Clases y alta: completar y aprobar la clase; la ficha nace al contabilizar la factura; costos de la liquidación; puesta en servicio y traslado |
| AF1-03 | Depreciación mensual (P-44), deshacer; baja y venta (P-45) con aprobación |
| AF1-04 | Carga inicial por CSV; conciliación FA-GL y componente FA-REC; consultas |
| AF1-05 | Pantallas, Inicio, Playwright, E2E-AF1 y matriz de aceptación |

## 7. Decisiones aprobadas

E-AF-1…12 en `../errata.md`.
