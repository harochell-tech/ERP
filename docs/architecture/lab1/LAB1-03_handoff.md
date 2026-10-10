# Handoff — LAB1-03: certificado, etiqueta de rack y escaneo en el gate-out

**Estado al 2026-10-10.** Escrito para que otra sesión retome LAB-1 sin la conversación anterior.

## Dónde está LAB-1

| PR | Estado |
| --- | --- |
| LAB1-00 (#188) | Cerrado: su contenido entró con el #190 |
| LAB1-01 (#190) | Fusionado en `main` (migración 0105, E-LAB1-01-1…17) |
| LAB1-02 (#191) | Fusionado en `main` el 2026-10-10 (migración 0114, E-LAB1-02-1…15) |
| LAB1-03 | **En espera de la aprobación de las errata de abajo. No hay código escrito.** Rama `lab1-03-certificate` (solo este documento) |
| LAB1-04, LAB1-05 | Sin empezar |

Lo construido está descrito en `docs/engineering/quality-lab.md`; la matriz de aceptación, en `docs/acceptance/lab1.md`.
Pruebas pendientes de LAB-1: LAB-13 y LAB-15 (LAB1-03), LAB-16 y E2E-L1 (LAB1-04) — la lista `Pending` de
`tests/Rochell.ArchitectureTests/AcceptanceLab1TraceabilityTests.cs`.

## Qué falta para empezar LAB1-03

1. Que Alexander responda «apruebo E-LAB1-03-1 a 14» (o corrija). Tres filas piden su decisión expresa:
   - **E-LAB1-03-7**: qué logo va en el certificado (X-L3).
   - **E-LAB1-03-9**: el tamaño real de la etiqueta que imprimen en planta.
   - **E-LAB1-03-8**: si el cliente debe quedar fuera de la página pública de verificación.
2. Con la aprobación: agregar las filas a `docs/architecture/errata.md` (en inglés, como las demás) y solo entonces codificar.

## Errata propuestas (pendientes de aprobación)

| # | Problema | Propuesta |
| --- | --- | --- |
| E-LAB1-03-1 | La impresión vive en Ventas (`Sales/Printing`) y Manufactura no puede depender de Ventas. | Certificado y etiqueta son dos tipos nuevos de documento imprimible, con formato editable como los demás (PRT-1). Los datos los arma el laboratorio; el grafo de módulos no cambia. |
| E-LAB1-03-2 | §14-3: un lote puede tener varias fechas de rotura. | Un certificado por lote y fecha de rotura, con las probetas válidas de esa fecha. Exige que el lote tenga código de campo. |
| E-LAB1-03-3 | §14-6: el lote fue a varios conduces y clientes. | Al emitir se elige un conduce del lote (cliente, obra y conduce salen de él) o ninguno. El primero es `CR-<código>-<DDMMAA>`; los siguientes del mismo lote y fecha llevan `-2`, `-3`. |
| E-LAB1-03-4 | Qué se puede certificar. | Cualquier fecha con al menos una probeta válida, sea cual sea el veredicto. Muestra resultados ensayados, nunca la estimación a 28 d ni los factores iniciales. |
| E-LAB1-03-5 | El certificado frente a cambios posteriores. | Es una foto: guarda sus datos al emitirse. Si después se anula una de sus probetas, queda ANULADO solo y la verificación pública lo dice. Calidad también puede anularlo con motivo. |
| E-LAB1-03-6 | Quién emite y quién firma. | Emite Calidad (`fg_lot:final_release`, con step-up). El firmante impreso es un parámetro del laboratorio (nombre y cargo, inicia «Ing. Alexander Rochell»); queda registrado además quién lo emitió. |
| E-LAB1-03-7 | X-L3: logo del certificado. | El mismo logo de empresa que usan los demás documentos (Configuración › Formatos de impresión), salvo que el dueño pida otro. |
| E-LAB1-03-8 | Verificación pública por QR: qué muestra sin iniciar sesión. | Número, producto, lote, fecha de rotura, número de probetas, promedio, mínimo, CV, fecha de emisión y estado (vigente o anulado). No muestra cliente ni obra. |
| E-LAB1-03-9 | Etiqueta de rack. | Una por rack: código de campo en grande, producto, fecha, máquina, turno, «rack n de N», unidades y QR. Tamaño 100 × 150 mm. La imprime quien tenga `production:read`. |
| E-LAB1-03-10 | Qué lleva el QR de la etiqueta. | La dirección del lote en Core con su rack. Con la cámara del teléfono abre el lote (pide sesión); el escáner de Despacho saca de ahí el código. |
| E-LAB1-03-11 | En qué paso se escanea. | En «Confirmar carga», por línea, con la cámara (el mismo lector `jsQR` que ya usa Compras). El lote escaneado debe tener saldo en la ubicación de carga y no estar bloqueado. |
| E-LAB1-03-12 | Cuánto se toma del lote escaneado. | El gate-out toma primero de los lotes escaneados, en el orden escaneado y hasta su saldo; lo que falte, por FIFO. Sin escaneo, FIFO como hoy. |
| E-LAB1-03-13 | §14-7: qué código muestra el conduce impreso. | El código de campo cuando el lote lo tiene; si no, el interno. |
| E-LAB1-03-14 | Envío del certificado por correo. | Fuera de LAB1-03: se imprime o se guarda como PDF desde la pantalla, como el conduce. |

## Lo que ya se sabe del código (para no releerlo)

- El FIFO del gate-out es `Deliveries.FifoAsync` en `src/Rochell.Sales/Deliveries/DeliveryHandlers.cs`; corre en `RecordGateOut`, no en
  `ConfirmLoaded`, y ya salta los lotes BLOQUEADOS leyendo `mfg.fg_lot` por SQL (E-LAB1-02-11).
- El patrón del QR público es el de ENT-1: `src/Rochell.Api/Deliveries/DriverPages.cs` (`/api/v1/public/deliveries`), migración 0101.
- La impresión: `docs/engineering/printing.md` (`PrintRenderer`, `PrintDocuments`, formatos editables, QR en SVG con QRCoder).
- Racks: `mfg.rack` (lote, número, unidades, estado). Los racks conocen CURING / RELEASED / BLOCKED / SCRAPPED / VOIDED.
- La siguiente migración libre era la 0115 al escribir esto: comprobar `db/migrations` antes de numerar.

## Lecturas de LAB1-01 y LAB1-02 que el dueño dio por buenas sin numerarlas

- El turno cuyo código es exactamente `T1` no lleva sufijo en el código de campo; cualquier otro sí (`…P1-DIA`).
- Parámetros y tipos de falla parten de valores compartidos (los del Excel) y cada empresa guarda encima los suyos.
- El código corto de una máquina se cambia pero no se borra.
- E-LAB1-02-12 no cambió nada: Ventas no tiene hoy consultas de disponibilidad.
- El recall hacia atrás (desde un conduce) existe como consulta, sin pantalla propia: queda ofrecido para LAB1-03.

## Abierto fuera del código

X-L1 (requisitos a 28 d y % de área neta por producto: sin ellos todo sale «Sin requisito»), X-L2 (edición de ASTM C90),
X-L3 (logo), X-L4 (medidas nominales de 4"), X-L5 (códigos cortos de las máquinas en staging).
