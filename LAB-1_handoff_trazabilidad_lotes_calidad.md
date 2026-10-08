# Handoff para Claude Code — LAB-1: Laboratorio de calidad y trazabilidad de lotes en Rochell Core

**Repos:** `harochell-tech/ERP` (Rochell Core, principal) · `harochell-tech/portal` (solo si un cambio lo exige)
**Adjunto de referencia:** `Registro_Ensayos_Compresion_Lotes.xlsx` (modelo funcional + histórico de 42 lotes / 216 probetas, sep-2024 → nov-2025)

---

## 0. Instrucción para Code

1. Trabaja en `harochell-tech/ERP`. Lee primero `CLAUDE.md` y `docs/architecture/README.md` y respeta la precedencia (errata > patches > baseline).
2. **Regla de congelamiento:** no escribas código todavía. El primer entregable es
   - `docs/architecture/lab1/frozen-baseline-lab1.md` en borrador (decisiones D-xx + plan de PRs), y
   - la tabla de errata propuesta `E-LAB1-n` (sección 5 de este documento) para que Alexander apruebe.
3. ADM Cloud **se elimina** con el cut-over a Core (staging → producción el 2026-11-01). **No se construye nada de lotes sobre `portal/entregas` ni sobre ADM.** Todo el despacho por lote va en Core.
4. Copia el Excel adjunto a `docs/architecture/lab1/reference/` como especificación funcional (fórmulas y campos).

---

## 1. Objetivo

Que cada bloque despachado se pueda rastrear hasta su lote, su máquina, su día y turno de producción, su mezcla y sus ensayos. El laboratorio (rotura a compresión ASTM C140, absorción y densidad) vive en Core y decide la liberación final del lote. Un resultado malo debe bloquear el lote y decir de inmediato qué conduces y qué clientes lo recibieron.

---

## 2. Lo que YA existe y no se rehace

| Pieza | Dónde |
| --- | --- |
| Lote de producto terminado `mfg.fg_lot` (mig. 0050), 1 lote por run (E-MFG1-7), código `PT-<item>-<yyyyMMdd>-<shift>` | `src/Rochell.Manufacturing/Runs/PostingHandlers.cs:318` |
| Estados `CURING → RELEASED / BLOCKED / SCRAPPED / VOIDED`; `ReleaseLot`, `BlockLot`, `UnblockLot` (rol CALIDAD, `fg_lot:release`), `ScrapLot`; liberación hoy "preliminar" por inspección visual | `src/Rochell.Manufacturing/Lots/LotCommands.cs` |
| Inventario por lote `inv.lot` (mig. 0008), saldo por ubicación × ítem × lote; CURADO nunca se ofrece a despacho | `docs/engineering/inventory.md` |
| Vínculo lote → conduce: gate-out toma lotes FIFO y los guarda en `log.delivery_line_lot` (E-VS3-04-5, mig. 0041) | `docs/engineering/sales.md` |
| Conduce, factura, pedidos, POD, conduce imprimible `GetDeliveryPrint`; confirmación del chofer por QR (ENT-1, mig. 0101) | VS#3, ENT-1 |
| Lectura del portal cada 15 min (`data/exportar.php`, header `X-Core-Token`): turnos, ciclos, molde 4/6/8 → ítem (`mfg.portal_mould`), consumos de dosificadora, paros, mantenimientos | MFG-2, `src/Rochell.Manufacturing/Portal/`, mig. 0099/0100 |
| Laboratorio: **diferido y sin dueño.** El baseline MFG-1 §1/D-08 lo dejó fuera ("resistencia, granulometría, FINAL_RELEASED, recall") con la etiqueta "MFG-2", pero MFG-2 se usó para el portal. No existe esquema `qa`. | `docs/architecture/mfg1/frozen-baseline-mfg1.md` |

**En el portal (solo contexto):**
- Las HMI postean cada ciclo a `data/registro.php` con `planta1/2/3` y `producto` 4/6/8.
- Los moldes están en `inc/moldes.php` (array: 4"→6/ciclo, 6"→4, 8"→3). **No existe molde de adoquín.**
- El checklist (SQLite) tiene `resultado_compresion` como texto libre. Ese campo queda obsoleto con LAB-1.
- `entregas/` (SQLite + ADM) se retira con el cut-over.

---

## 3. Alcance funcional de LAB-1, tomado del Excel ya validado

### 3.1 Ensayo a compresión (una fila por probeta)
- Datos de entrada: lote, fecha de rotura, ancho / alto / largo (cm; si faltan, se usan las medidas nominales del ítem), peso (kg), carga (kg), condición del bloque (seco al aire / húmedo / saturado), tipo de falla (lista configurable), observaciones, técnico.
- Cálculos:
  - Edad (días) = fecha de rotura − fecha de producción del lote.
  - Área bruta = ancho × largo.
  - Resistencia bruta (kg/cm²) = carga ÷ área.
  - MPa = kg/cm² × 0.0980665.
  - Resistencia sobre área neta, si el ítem tiene configurado el % de área neta.

### 3.2 Evaluación por lote
- Indicadores: número de probetas, edad mínima y máxima, promedio, mínimo, máximo, desviación estándar y CV.
- Edad que cuenta como "28 días": ≥ 26 días (configurable).
- Resistencia a 28 días:
  - Real, si hay roturas a ≥ 26 días.
  - Si no, estimada = promedio a la edad temprana ÷ factor de edad.
- Requisitos por ítem a 28 días: mínimo del promedio y mínimo individual.
- Veredicto: `CUMPLE` / `NO CUMPLE`, con marca *estimado* cuando sale de la estimación.
- Alertas: menos de 3 probetas (configurable); CV > 15 % (configurable); absorción por encima del límite.

### 3.3 Curva de edad (factor = resistencia a la edad d ÷ resistencia a 28 d)
- Factor por lote = promedio a la edad temprana ÷ promedio a 28 d. Solo aplica a lotes que tengan ambos.
- Factor por edad (1–28 d) = promedio de los factores de los lotes con ese dato. Cuando hay 2 o más lotes (configurable), reemplaza al factor inicial.
- Factores iniciales, sacados del histórico general de Block 8" (lotes distintos, se usan solo como alerta interna y nunca para certificar):

  | Edad (d) | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 14 | 21 | 28 |
  |---|---|---|---|---|---|---|---|---|---|---|
  | Factor | 0.70 | 0.77 | 0.84 | 0.87 | 0.89 | 0.90 | 0.91 | 0.96 | 0.98 | 1.00 |

  Entre esos puntos se interpola linealmente.

### 3.4 Absorción y densidad (ASTM C140), por bloque
- Entradas: Ws (saturado), Wi (sumergido), Wd (seco), en kg.
- Absorción (kg/m³) = (Ws − Wd) ÷ (Ws − Wi) × 1000.
- Absorción (%) = (Ws − Wd) ÷ Wd.
- Densidad (kg/m³) = Wd ÷ (Ws − Wi) × 1000.
- Clase según densidad: Liviano < 1680, Mediano 1680–2000, Normal ≥ 2000.
- Absorción máxima del promedio: 288 / 240 / 208 kg/m³. Son valores de ASTM C90; hay que confirmar la edición que se usa.

### 3.5 Gráfico de control
- Filtro por máquina y por ítem: últimos 30 lotes, media ± 2σ y línea del requisito.
- Comparación P1 vs P2 vs P3: número de lotes, promedio, mínimo, máximo, % NO CUMPLE y CV promedio.
- Lotes fuera de los límites marcados.

### 3.6 Certificado
- Formato ASTM C140, con número `CR-<lote>-<DDMMAA de rotura>`.
- Contenido: cliente y obra (desde el conduce del lote), máquina, tabla de probetas, promedio, mínimo, CV, absorción y densidad del lote, condición del bloque, tipo de falla, datos del equipo (TEST MARK CM-2500-iD, serie 220808) y firma (Ing. Alexander Rochell).
- QR de verificación pública, con el mismo patrón que ENT-1.

### 3.7 Trazabilidad / recall
- Hacia adelante: lote → conduces → clientes y obras, vía `log.delivery_line_lot`.
- Hacia atrás: conduce → lotes → run / turno / máquina → consumo de dosificadora (mezcla y cemento) → ensayos.

---

## 4. Plan de PRs sugerido (Code lo ajusta en el baseline)

| PR | Contenido |
| --- | --- |
| LAB1-00 | `frozen-baseline-lab1.md` + errata aprobadas + matriz de aceptación `docs/acceptance/lab1.md` |
| LAB1-01 | Esquema `qa` (requisitos por ítem, ensayos de compresión, ensayos de absorción, factores de edad, parámetros), comandos y API para registrar ensayos, pantalla web móvil para usar junto a la prensa |
| LAB1-02 | Evaluación por lote, 28 d real o estimado, alertas; `FINAL_RELEASED` y bloqueo automático con NO CUMPLE; consulta de recall |
| LAB1-03 | Certificado PDF + verificación pública por QR; etiqueta de rack o pallet con QR del lote |
| LAB1-04 | Dashboard de control (media ± 2σ, comparación de máquinas, curva de edad) + importación del histórico del Excel |
| LAB1-05 (portal) | Quitar o redirigir `resultado_compresion` del checklist; molde de adoquín, si se aprueba E-LAB1-8 |

---

## 5. Errata a proponer a Alexander antes de codificar

| # | Problema | Propuesta |
| --- | --- | --- |
| E-LAB1-1 | **Formato del código de lote.** Core genera `PT-<item>-<yyyyMMdd>-<shift>`; en planta y en el laboratorio se usa `[medida][DDMMAA][máquina]`, por ejemplo `8070325P1`. | Se mantiene el código interno y se agrega un `field_code` único que es el que se imprime en etiquetas, conduces y certificados: `8` + `070325` + `P1`, y `-T2` si hay un segundo turno ese día. |
| E-LAB1-2 | **Granularidad.** Core arma 1 lote por run (turno); el laboratorio lo pensó como 1 lote por máquina, día y producto. | Mantener 1 lote por run (E-MFG1-7). El sufijo `-T#` resuelve los casos de varios turnos. Un ensayo pertenece a un solo lote. |
| E-LAB1-3 | **Códigos de máquina.** P1/P2/P3 no existen en Core, el portal usa `planta1..3` y D-04 asume una línea Besser por planta. | Agregar `md.machine.short_code` (P1, P2, P3) mapeado a `planta1..3` y verificar D-04 contra las 3 máquinas de MATILLA. |
| E-LAB1-4 | **Doble liberación.** | `RELEASED` = preliminar (visual, como hoy; permite despachar). `FINAL_RELEASED` = laboratorio CUMPLE con dato real a ≥ 26 d. Un NO CUMPLE (real o estimado) ejecuta `BlockLot` automáticamente sobre el saldo restante y genera el listado de recall. |
| E-LAB1-5 | ¿El veredicto *estimado* puede bloquear? | Sí puede bloquear, como medida preventiva. No puede dar `FINAL_RELEASED`, eso solo con dato real. |
| E-LAB1-6 | **Histórico previo a Core** (42 lotes, 216 probetas; los códigos viejos B1/B2/B3 ya están normalizados a P1/P2/P3). | Importarlo a `qa` con `legacy_lot_code` y sin `fg_lot_id`, de solo lectura. Alimenta la curva de edad y el gráfico de control. |
| E-LAB1-7 | **Selección de lote en el gate-out.** Hoy es FIFO automático. | Permitir escanear la etiqueta del rack para escoger el lote; FIFO queda como valor por defecto. |
| E-LAB1-8 | **Adoquín:** no hay molde en el portal y su norma es otra (ASTM C936). | Dejarlo fuera de LAB-1 v1; los requisitos por ítem se dejan preparados para incluirlo después. |
| E-LAB1-9 | **Permisos.** | `qa:test:record` (técnico), `qa:lot:final_release` y `qa:spec:manage` (CALIDAD). El certificado lo firma el rol CALIDAD. |
| E-LAB1-10 | **Requisitos a 28 d por ítem** todavía no definidos. | Hasta que se carguen, el veredicto queda "Sin requisito" y no bloquea. |

---

## 6. Datos que pedirá Code a Alexander

- Requisito mínimo a 28 d (promedio e individual) y % de área neta para Block 4", 6" y 8".
- Si el certificado debe salir con el logo de Block Rochell o de Industrias Rochell.
- Confirmación de la edición de ASTM C90 para los límites de absorción.
