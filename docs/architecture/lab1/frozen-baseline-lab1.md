# LAB-1 — Laboratorio de calidad y trazabilidad de lotes

**Estado: CONGELADO — aprobado por Alexander Rochell el 2026-10-08**, con las decisiones D-01…D-10 de la sección 13 tal como se
recomiendan (errata E-LAB1-1…10 en `../errata.md`). Es la especificación; cualquier cambio sigue la regla de congelamiento.
Matriz de aceptación: `../../acceptance/lab1.md`.

Fuentes: handoff [`LAB-1_handoff_trazabilidad_lotes_calidad.md`](LAB-1_handoff_trazabilidad_lotes_calidad.md); Excel validado
[`reference/Registro_Ensayos_Compresion_Lotes.xlsx`](reference/Registro_Ensayos_Compresion_Lotes.xlsx) (fórmulas y campos) y su
histórico en [`reference/historico_ensayos.csv`](reference/historico_ensayos.csv); MFG-1 §1, D-07, D-08 (E-MFG1-4, 7, 8;
E-MFG1-03-7; E-MFG1-04-1…7); MFG-2 (E-MFG2-3, 11, 13); VS#3 (E-VS3-04-5); ENT-1 (migración 0101); el código en `main` al
2026-10-08 (migraciones hasta 0101).

## 1. Por qué

Hoy un lote se libera con inspección visual y eso es todo lo que Core sabe de su calidad. Las roturas a compresión viven en un
Excel aparte, con un código de lote que Core no conoce. Si una rotura sale baja, no hay forma de saber desde el sistema qué
conduces y qué clientes recibieron ese lote, ni de frenar lo que queda en el patio.

LAB-1 trae el laboratorio a Core: cada bloque despachado se rastrea hasta su lote, máquina, día, turno, mezcla y ensayos; el
laboratorio decide la liberación final; un resultado malo bloquea el lote y lista de inmediato conduces y clientes.

## 2. Alcance y regla de congelamiento

| Dentro de LAB-1 | Fuera de LAB-1 |
| --- | --- |
| Ensayo a compresión por probeta (ASTM C140) con sus cálculos | Adoquín y ASTM C936 (D-08) |
| Absorción y densidad por bloque (ASTM C140), clase y límite ASTM C90 | Granulometría, humedad de agregados, ensayos de materia prima |
| Requisitos a 28 d por ítem y parámetros de control | Corrección de receta por resultados (queda en Producción) |
| Evaluación por lote: 28 d real o estimado, veredicto, alertas | Lotes sobre `portal/entregas` o ADM Cloud: nada (se retiran en el cut-over del 2026-11-01) |
| Curva de edad (factores iniciales y propios) | COPQ, costo de la no calidad, notas de crédito por reclamo |
| Liberación final `FINAL_RELEASED` y bloqueo automático con NO CUMPLE | Calibración del equipo como proceso (solo sus datos en el certificado) |
| Código de campo del lote, código corto de máquina | |
| Recall hacia adelante y hacia atrás | |
| Certificado PDF con QR de verificación pública; etiqueta de rack con QR | |
| Gráfico de control, comparación de máquinas | |
| Importación del histórico (42 lotes, 216 probetas), solo lectura | |
| Selección de lote por escaneo en el gate-out | |

Regla de congelamiento idéntica a los demás baselines: toda ambigüedad se detiene y se propone como errata numerada.

## 3. Lo que ya existe (verificado en el código, no se rehace)

| Pieza | Dónde | Observación para LAB-1 |
| --- | --- | --- |
| Lote PT `mfg.fg_lot`, 1 por resumen de turno (corrida) | mig. 0050, `Runs/PostingHandlers.cs` | El código `PT-<ítem>-<yyyyMMdd>-<turno>` **no lleva la máquina**: dos máquinas con el mismo producto y turno el mismo día reciben `…-T1` y `…-T1-2`, el mismo sufijo que usa una corrida rehecha tras un reverso (E-MFG1-03-7). Refuerza D-01 |
| Estados `CURING`, `RELEASED`, `BLOCKED`, `SCRAPPED`, `VOIDED`, con guarda en la base | mig. 0050 / 0051 | `BlockLot` solo acepta `CURING → BLOCKED` y `UnblockLot` devuelve a `CURING` (E-MFG1-04-3). **Un lote ya liberado no se puede bloquear hoy.** Ver D-04 |
| `ReleaseLot` / `BlockLot` / `UnblockLot` (CALIDAD, `fg_lot:release`), `ScrapLot` | `Lots/LotCommands.cs` | La liberación actual pasa a ser la preliminar |
| Despacho FIFO, vínculo lote → conduce en `log.delivery_line_lot` | mig. 0041, `Sales/Deliveries/DeliveryHandlers.cs` | El FIFO ordena por `lot_code` entre los lotes con saldo en la ubicación; excluye la **ubicación** CURADO, no mira el **estado** del lote. Un lote bloqueado en PATIO se despacharía. Ver D-04 |
| Máquinas `md.machine` (código libre) y pareja portal → máquina `mfg.portal_machine` | mig. 0048, 0099 | `planta1..3` ya son máquinas de MATILLA (E-MFG2-3); D-04 de MFG-1 ("una línea por planta") quedó superado en la práctica. Ver D-03 |
| Turnos T1 y T2 de MATILLA | E-MFG2-13 | Base del sufijo de turno en D-01 |
| Consumo de dosificadora por turno, repartido por receta | E-MFG2-5, 11 | Es la "mezcla y cemento" del recall hacia atrás; planta1 lo registra a mano |
| Confirmación pública por QR `/api/v1/public/deliveries/{c}/{d}` | ENT-1, mig. 0101 | Patrón del QR del certificado |
| Esquema `qa` | — | No existe. MFG-1 §2 lo nombraba (`qa.fg_lot`) pero el lote se construyó en `mfg` |
| Códigos de permiso | mig. 0003 | El formato es `^[a-z_]+:[a-z_]+$`: **dos segmentos**. `qa:test:record` del handoff no es válido. Ver D-09 |

## 4. Especificación funcional (del Excel)

Entre paréntesis, la hoja y columna de donde sale cada regla. Los valores marcados *(parámetro)* se configuran; ninguno va en el
código (regla de arquitectura: sin literales decimales en `src/`). Esto incluye la constante 0.0980665 y los factores iniciales
de edad, que se siembran como datos.

### 4.1 Ensayo a compresión — una fila por probeta (ENSAYOS)

Entradas: lote, fecha de rotura, ancho / alto / largo (cm), peso (kg), carga (kg), condición del bloque (Seco al aire / Húmedo /
Saturado), tipo de falla (lista configurable: Cónica, Cono y corte, Corte / diagonal, Columnar / vertical, Desprendimiento de
cara, Aplastamiento local, Otra), observaciones, técnico. Cliente, obra y conduce ya no se escriben: salen del lote (§4.7).

| Cálculo | Fórmula | Origen |
| --- | --- | --- |
| Edad (d) | fecha de rotura − fecha de producción del lote | ENSAYOS G |
| Área bruta (cm²) | ancho × largo; si falta una medida, la nominal del ítem | ENSAYOS P |
| Resistencia bruta (kg/cm²) | carga ÷ área bruta | ENSAYOS Q |
| Resistencia (MPa) | kg/cm² × 0.0980665 | ENSAYOS R |
| Resistencia sobre área neta | resistencia bruta ÷ fracción de área neta del ítem, si está configurada | ENSAYOS S |

Una probeta cuenta para el lote cuando su resistencia es mayor que cero.

### 4.2 Evaluación por lote (LOTES)

| Indicador | Regla | Origen |
| --- | --- | --- |
| N° de probetas, edad mínima y máxima | sobre las probetas válidas del lote | M, N, O |
| Promedio, mínimo, máximo | de la resistencia bruta de **todas** las probetas | P, Q, R |
| Desviación estándar | muestral (n − 1); vacía con menos de 2 probetas | S |
| CV | desviación ÷ promedio | T |
| Edad que cuenta como 28 d | ≥ 26 d *(parámetro)* | CONFIG B27 |
| Promedio a edad temprana | si la edad mínima es < 26 d: promedio de las probetas **de la edad mínima** (no de todas las tempranas) | U |
| Promedio a 28 d | promedio de las probetas con edad ≥ 26 d, si hay | V |
| Factor usado | el de la curva (§4.3) para la edad mínima, acotada entre 1 y 28 | X |
| Resistencia a 28 d | **real** = promedio a 28 d si existe; si no, **estimada** = promedio temprano ÷ factor | Y |
| Mínimo a 28 d | real: mínimo de las probetas ≥ 26 d; estimado: mínimo de la edad mínima ÷ factor | Z |
| Veredicto | sin requisito del ítem → `Sin requisito`; `CUMPLE` si resistencia a 28 d ≥ mínimo del promedio **y** (no hay mínimo individual **o** mínimo a 28 d ≥ mínimo individual); si no, `NO CUMPLE`. Marca *estimado* cuando no hay dato real | AA, AB, AC |
| Alertas | sin ensayos; menos de 3 probetas *(parámetro)*; CV > 15 % *(parámetro)*; absorción promedio del lote por encima del límite de su clase | AG |

Ejemplos del histórico (sirven de valores esperados de las pruebas):

- Lote `8160924P1`: 5 probetas a 3 d; promedio 77.1596, mínimo 57.1243, máximo 98.3660, desviación 14.8241, CV 19.21 % (alerta
  «CV alto»); factor 0.84; resistencia estimada a 28 d 91.8567; mínimo estimado 68.0051.
- Lote `8121124P1`: 6 probetas a 28 d; promedio real 80.3975; mínimo 71.7442; CV 9.74 %; sin estimación.

### 4.3 Curva de edad (CURVA EDAD)

- Factor del lote = promedio temprano ÷ promedio a 28 d; solo para lotes con ambos (LOTES W).
- Factor propio por edad (1–28 d) = promedio de los factores de los lotes cuya edad mínima es esa edad.
- Factor usado: 1 si la edad ≥ 26 d; el propio si hay ≥ 2 lotes *(parámetro)* con dato; si no, el inicial.
- Factores iniciales (histórico general de Block 8", lotes distintos — solo alerta interna, nunca para certificar). El Excel
  trae la tabla completa de 1 a 28 d, ya interpolada, y es la que se siembra:

  | d | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 |
  |---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
  | f | 0.70 | 0.77 | 0.84 | 0.87 | 0.89 | 0.90 | 0.91 | 0.917 | 0.924 | 0.931 | 0.939 | 0.946 | 0.953 | 0.96 |

  | d | 15 | 16 | 17 | 18 | 19 | 20 | 21 | 22 | 23 | 24 | 25 | 26 | 27 | 28 |
  |---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
  | f | 0.963 | 0.966 | 0.969 | 0.971 | 0.974 | 0.977 | 0.98 | 0.983 | 0.986 | 0.989 | 0.991 | 0.994 | 0.997 | 1.00 |

- La curva es una sola para todos los productos y máquinas, como en el Excel.

### 4.4 Absorción y densidad — una fila por bloque (ABSORCION)

Entradas: Ws (saturado), Wi (sumergido), Wd (seco), en kg; válidas si Ws > Wi y Wd > 0.

- Absorción (kg/m³) = (Ws − Wd) ÷ (Ws − Wi) × 1000. Absorción (%) = (Ws − Wd) ÷ Wd.
- Densidad (kg/m³) = Wd ÷ (Ws − Wi) × 1000.
- Clase por densidad y absorción máxima del promedio *(parámetros; ASTM C90, edición por confirmar)*: Liviano < 1680 → 288;
  Mediano 1680–2000 → 240; Normal ≥ 2000 → 208 kg/m³.
- Por bloque se marca OK / ALTA como guía; el límite de la norma aplica al **promedio del lote**, que es el que genera la alerta.

El histórico no trae ningún ensayo de absorción.

### 4.5 Gráfico de control (CONTROL)

Filtro por máquina (o todas) y por ítem; últimos 30 lotes con ensayos; punto = promedio ensayado del lote; media y desviación
entre lotes; límites media ± 2σ; línea del requisito; lote `FUERA` si cae fuera de los límites. Comparación de máquinas para el
ítem: N° de lotes, promedio, mínimo y máximo de lote, % NO CUMPLE y CV promedio.

### 4.6 Certificado (CERTIFICADO)

Formato ASTM C140, número `CR-<código de campo>-<DDMMAA de rotura>`, por lote y fecha de rotura. Contenido: cliente y obra,
procedencia (lote y máquina), encargado, equipo (TEST MARK, CM-2500-iD, serie 220808), tabla de probetas (conduce, medidas,
área, tipo de bloque, fechas, edad, peso, carga, kg/cm², MPa), N° de probetas, promedio (kg/cm² y MPa), mínimo, CV, absorción y
densidad del lote, condición del bloque y falla predominante, firma, y la nota «Resistencia calculada sobre área bruta.
Resultados válidos solo para las unidades ensayadas». QR de verificación pública con el patrón de ENT-1.

### 4.7 Trazabilidad y recall

- Hacia adelante: lote → `log.delivery_line_lot` → conduces → clientes y obras, con cantidades y saldo que queda por ubicación.
- Hacia atrás: conduce → lotes → corrida, turno, máquina → consumo del turno (mezcla y cemento) → ensayos.

## 5. Esquema (nuevo, conceptual)

```sql
CREATE SCHEMA qa;
ALTER TABLE md.machine  ADD short_code;                     -- P1, P2, P3 (D-03)
ALTER TABLE mfg.fg_lot  ADD field_code;                     -- 8070325P1 (D-01); estado nuevo FINAL_RELEASED (D-04)

CREATE TABLE qa.item_spec (            -- requisitos por ítem, versionados (D-10)
  item_id, version, lot_prefix,        -- '4', '6', '8'
  nominal_width_cm, nominal_height_cm, nominal_length_cm, net_area_fraction NULL,
  min_avg_28d NULL, min_individual_28d NULL, status, prepared_by, approved_by );
CREATE TABLE qa.parameter (            -- CV máximo, mínimo de probetas, edad de 28 d, lotes para factor propio,
  code, value );                       -- kg/cm² → MPa, clases de densidad y sus límites de absorción, datos del equipo
CREATE TABLE qa.age_factor ( age_days 1..28, initial_factor );
CREATE TABLE qa.failure_type ( code, name, status );
CREATE TABLE qa.legacy_lot (           -- histórico previo a Core, solo lectura (D-06)
  legacy_lot_id, field_code, legacy_lot_code NULL, item_id, machine_id, production_date );
CREATE TABLE qa.compression_test (     -- una fila por probeta; fg_lot_id XOR legacy_lot_id
  test_id, fg_lot_id NULL, legacy_lot_id NULL, break_date, width_cm, height_cm, length_cm, weight_kg, load_kg,
  gross_area_cm2, strength_kgcm2, block_condition, failure_type, notes, tested_by, status (RECORDED, VOIDED) );
CREATE TABLE qa.absorption_test ( test_id, fg_lot_id, test_date, ws_kg, wi_kg, wd_kg, notes, tested_by, status );
CREATE TABLE qa.lot_evaluation (       -- foto del veredicto cada vez que cambia; la vigente decide el estado del lote
  evaluation_id, fg_lot_id, tests, strength_28d, min_28d, basis (REAL, ESTIMATED), factor_used,
  verdict (COMPLIES, FAILS, NO_SPEC), alerts, spec_version );
CREATE TABLE qa.certificate ( certificate_id, certificate_no, fg_lot_id, break_date, public_code, issued_by, issued_at );
```

Cantidades `numeric(18,6)`; nada de `float` / `double` (la desviación estándar se calcula en PostgreSQL sobre `numeric`). Sin
asientos contables: LAB-1 no toca el mayor (el bloqueo y la liberación final son cambios de estado sin valor).

## 6. Agregados, comandos y eventos

| Agregado | Comandos | Eventos |
| --- | --- | --- |
| ItemSpec | PrepareItemSpec, ApproveItemSpec | ItemSpecApproved |
| Parámetros / tipos de falla | SetQualityParameter, DefineFailureType | … |
| CompressionTest | RecordCompressionTests (varias probetas de un lote y fecha), VoidCompressionTest (con motivo) | CompressionTestsRecorded, LotEvaluated |
| AbsorptionTest | RecordAbsorptionTests, VoidAbsorptionTest | AbsorptionTestsRecorded, LotEvaluated |
| FgLot (extendido) | FinalReleaseLot; `BlockLot` / `UnblockLot` extendidos a lotes liberados | LotFinalReleased, LotBlocked (con causa `LAB`) |
| Certificate | IssueCertificate | CertificateIssued |
| Machine (extendido) | SetMachineShortCode | … |
| Histórico | ImportLegacyTests (una vez, desde el CSV) | LegacyTestsImported |

Consultas: lotes con su evaluación, detalle del lote (ensayos, evaluación, conduces), recall hacia adelante y hacia atrás,
curva de edad, gráfico de control y comparación de máquinas, certificado (impresión y verificación pública).

## 7. Máquina de estados del lote (D-04 y D-05)

```
CURING ──ReleaseLot──▶ RELEASED ──FinalReleaseLot (CUMPLE real)──▶ FINAL_RELEASED
   │                      │                                              │
   └──BlockLot──▶ BLOCKED ◀──BlockLot / NO CUMPLE (real o estimado)──────┘
                     │
                     └──UnblockLot (motivo)──▶ vuelve al estado del que vino
```

- `RELEASED` (preliminar, visual) permite despachar, como hoy. `FINAL_RELEASED` exige veredicto `CUMPLE` con dato real.
- `NO CUMPLE` bloquea el lote en el acto, esté en `CURING`, `RELEASED` o `FINAL_RELEASED`, y deja listo el recall.
- Un lote `BLOCKED` no se despacha aunque su saldo esté en PATIO: el FIFO y el escaneo lo saltan.
- `CUMPLE` estimado no cambia el estado. `Sin requisito` no bloquea ni libera.

## 8. Permisos y segregación de funciones (D-09)

| Permiso | Quién | Para |
| --- | --- | --- |
| `lab_test:record` | Técnico de laboratorio (rol nuevo `LABORATORIO`), CALIDAD | Registrar y anular ensayos |
| `fg_lot:final_release` | CALIDAD | Liberación final; emitir y firmar el certificado |
| `lab_spec:manage` | CALIDAD | Requisitos por ítem, parámetros, tipos de falla, código corto de máquina |
| `lab:read` | LABORATORIO, CALIDAD, Gerente de planta, Supervisor, Ventas, Director, Auditor | Consultas, recall, gráficos |

SoD: producir (`shift_summary:record`) ≠ liberación final, igual que hoy con la preliminar. El bloqueo automático lo
ejecuta el sistema como consecuencia del ensayo registrado; queda en el historial con el ensayo que lo causó.

## 9. Histórico previo a Core

`reference/historico_ensayos.csv`: una fila por probeta con los datos de su lote; UTF-8, fechas ISO. Las columnas calculadas
(`age_days`, `gross_area_cm2`, `strength_kgcm2`, `strength_mpa`) son los valores del Excel y sirven para verificar la importación.

| Dato | Valor |
| --- | --- |
| Probetas / lotes | 216 / 42 (todas las probetas tienen lote; todos los lotes tienen ≥ 3 probetas) |
| Período | producción 2024-09-16 → 2025-11-10; última rotura 2025-11-13 |
| Por máquina y producto | P1 Block 8": 147 probetas, 30 lotes · P2 Block 6": 40, 8 · P2 Block 8": 25, 3 · P3 Block 6": 4, 1 |
| Cliente / obra | EQUINOCCIO BAVARO SA (con y sin «(CODELPA)») / LOPESAN CEIBA |
| Resistencia bruta | promedio 75.72, mínimo 52.05, máximo 127.39 kg/cm² |
| Código original distinto del normalizado | 113 probetas (19 códigos: `P18121124`, `8100525B1`, `140125P1`…) |

Calidad de los datos, a tener en cuenta en la importación (D-06):

1. **8 probetas con edad 0** (lotes `8100525P1` y `8050625P2`: fecha de rotura = fecha de producción). El Excel las estima con
   el factor de 1 día.
2. **Solo 4 lotes tienen rotura a ≥ 26 d**, y **ninguno tiene rotura temprana y a 28 d a la vez**: el histórico no aporta ningún
   factor propio; la curva arranca solo con los factores iniciales.
3. 8 probetas a 22 d (un lote): no cuentan como 28 d; se estiman con factor 0.983.
4. 1 probeta sin medidas (usa las nominales); 5 sin conduce; ninguna con peso, tipo de falla ni técnico; 7 marcadas «Húmedo».
5. 12 probetas con la nota «fecha de muestra distinta a la fecha del código de lote».
6. Sin datos de trazabilidad de producción (mezcla, cemento, curado, unidades) ni ensayos de absorción.
7. Todos los lotes salen «Sin requisito» (no hay requisitos cargados); 8 con alerta «CV alto».

## 10. Pruebas de aceptación

| ID | Given | When | Then |
| --- | --- | --- | --- |
| LAB-01 | Lote con fecha de producción | Registrar 5 probetas a 3 d (datos de `8160924P1`) | Edad, área, kg/cm² y MPa como en §4.1; evaluación con los valores de §4.2 |
| LAB-02 | Probeta sin medidas | Registrar | Usa las nominales del ítem |
| LAB-03 | Lote con probetas a 28 d (`8121124P1`) | Evaluar | 28 d real = 80.3975; sin marca *estimado* |
| LAB-04 | Ítem sin requisito | Evaluar | `Sin requisito`; el lote no se bloquea ni se libera |
| LAB-05 | Lote `RELEASED` con saldo y un conduce | Ensayo real `NO CUMPLE` | Lote `BLOCKED`; el gate-out ya no lo toma; recall con el conduce y el saldo |
| LAB-06 | Lote `RELEASED` | Ensayo temprano con estimado `NO CUMPLE` | `BLOCKED` preventivo; `FinalReleaseLot` rechazado |
| LAB-07 | Lote con `CUMPLE` estimado | `FinalReleaseLot` | Rechazado; con `CUMPLE` real, `FINAL_RELEASED` |
| LAB-08 | Lote bloqueado por estimado | Rotura real a 28 d `CUMPLE` y `UnblockLot` con motivo | Vuelve a su estado anterior; puede liberarse |
| LAB-09 | 2 probetas; CV > 15 % | Evaluar | Alertas «menos de 3 probetas» y «CV alto» |
| LAB-10 | 3 bloques de absorción | Registrar | Absorción, densidad, clase y límite de §4.4; alerta si el promedio supera el límite |
| LAB-11 | 2 lotes con rotura a 3 d y a 28 d | Curva | El factor de 3 d es el propio; con 1 lote, el inicial |
| LAB-12 | Corridas de P1 y P2, mismo producto, turno y día | Contabilizar | Códigos de campo distintos (`…P1`, `…P2`); T2 con sufijo |
| LAB-13 | Dos lotes en PATIO | Gate-out con escaneo de la etiqueta del más nuevo | Toma el escaneado; sin escaneo, FIFO |
| LAB-14 | Lote con conduces y consumo | Recall hacia adelante y hacia atrás | Conduces, clientes, obras; corrida, turno, máquina, consumo, ensayos |
| LAB-15 | Lote con ensayos | Emitir certificado | Número `CR-<código>-<DDMMAA>`; contenido de §4.6; QR público que lo verifica |
| LAB-16 | CSV del histórico | Importar | 42 lotes y 216 probetas de solo lectura; valores calculados iguales a los del CSV; segunda importación no duplica |
| LAB-17 | Técnico sin `fg_lot:final_release` | `FinalReleaseLot` | Rechazado |
| E2E-L1 | Flujo completo | Por la API y por la UI | Corrida → lote → liberación preliminar → conduce → ensayo temprano → 28 d → liberación final → certificado |

## 11. Dependencias externas

| # | Qué | Quién | Bloquea |
| --- | --- | --- | --- |
| X-L1 | Requisito mínimo a 28 d (promedio e individual) y % de área neta para Block 4", 6" y 8" | Alexander | Veredictos reales (sin ellos todo sale «Sin requisito») |
| X-L2 | Edición de ASTM C90 para los límites de absorción | Alexander | Alerta de absorción en datos reales |
| X-L3 | Logo del certificado: Block Rochell o Industrias Rochell | Alexander | LAB1-03 |
| X-L4 | Medidas nominales de Block 4" (el Excel las marca como supuestas) | Alexander | Datos reales de 4" |
| X-L5 | Códigos de las 3 máquinas en Core staging y su pareja `planta1..3` → P1, P2, P3 | Alexander / Gerente de planta | D-03 en staging |

## 12. Plan de PRs

| PR | Contenido | Pruebas |
| --- | --- | --- |
| LAB1-00 | Este baseline, errata aprobadas en `errata.md`, matriz `docs/acceptance/lab1.md`, fila en `docs/architecture/README.md` | — |
| LAB1-01 | Esquema `qa`, código de campo y código corto de máquina, permisos y rol; requisitos, parámetros; registrar y anular ensayos de compresión y absorción; pantalla móvil para usar junto a la prensa | LAB-01, 02, 10, 12, 17 |
| LAB1-02 | Evaluación por lote, 28 d real o estimado, alertas, curva de edad; `FINAL_RELEASED`; bloqueo de lotes liberados y automático con NO CUMPLE; el despacho salta lotes bloqueados; recall | LAB-03…09, 11, 14 |
| LAB1-03 | Certificado PDF y verificación pública por QR; etiqueta de rack con QR; escaneo en el gate-out | LAB-13, 15 |
| LAB1-04 | Gráfico de control, comparación de máquinas, curva de edad en pantalla; importación del histórico | LAB-16, E2E-L1 |
| LAB1-05 (portal) | Retirar o redirigir `resultado_compresion` del checklist | — |

## 13. Decisiones (aprobadas como E-LAB1-1…10)

| # | Decisión | Recomendación |
| --- | --- | --- |
| D-01 | Formato del código de lote | Se mantiene el código interno `PT-…` y se agrega `field_code` único, el que se imprime en etiquetas, conduces y certificados: `<prefijo del ítem><DDMMAA><código corto de máquina>`, p. ej. `8070325P1`. El primer turno (T1) no lleva sufijo; los demás llevan `-<turno>` siempre (`8070325P1-T2`), haya o no otro lote ese día |
| D-02 | Granularidad | 1 lote por corrida (E-MFG1-7). Un ensayo pertenece a un solo lote. Con dos turnos hay dos lotes ese día y cada uno necesita sus probetas |
| D-03 | Códigos de máquina | `md.machine.short_code` (P1, P2, P3), único por empresa. La relación con `planta1..3` ya existe en `mfg.portal_machine`. Se deja constancia de que D-04 de MFG-1 no aplica: MATILLA tiene 3 máquinas |
| D-04 | Doble liberación | `RELEASED` = preliminar (despacha). `FINAL_RELEASED` = CUMPLE con dato real a ≥ 26 d. `NO CUMPLE` bloquea el lote desde cualquier estado vivo; `BlockLot` / `UnblockLot` se extienden a lotes liberados y el despacho salta los bloqueados |
| D-05 | Veredicto estimado | Puede bloquear (preventivo, Calidad desbloquea con motivo). No puede dar `FINAL_RELEASED` |
| D-06 | Histórico | Se importa a `qa` como lotes heredados de solo lectura, sin `fg_lot_id`, con su código original. Alimenta el gráfico de control y la curva. Las probetas de edad 0 se importan marcadas y no entran en estimaciones |
| D-07 | Lote en el gate-out | Escanear la etiqueta del rack elige el lote; sin escaneo, FIFO |
| D-08 | Adoquín | Fuera de LAB-1 v1; los requisitos por ítem quedan preparados |
| D-09 | Permisos | `lab_test:record`, `fg_lot:final_release`, `lab_spec:manage`, `lab:read`; rol nuevo `LABORATORIO`; el certificado lo firma CALIDAD |
| D-10 | Ítem sin requisito | Veredicto `Sin requisito`: no bloquea y tampoco permite `FINAL_RELEASED` |

## 14. Puntos que quedan para las errata de cada PR

No son parte de E-LAB1-1…10; se listan para que no se pierdan y se resolverán como `E-LAB1-0n-m` antes del PR que toquen.

1. Dónde vive el código: módulo nuevo `Rochell.Quality` o dentro de `Rochell.Manufacturing`. El recall lee tablas de Ventas
   (`log.delivery_line_lot`) y el bloqueo es de Manufactura; el grafo de módulos (E-PR08-1) necesita una regla.
2. «Edad temprana» = solo la edad mínima del lote (así lo hace el Excel) o todas las roturas antes de 26 d.
3. Varias fechas de rotura en un lote: el certificado es por lote y fecha; la evaluación es por lote.
4. Un lote sin saldo (todo despachado) con NO CUMPLE: se marca `BLOCKED` igual o solo queda el recall.
5. Anular un ensayo que causó un bloqueo: si desbloquea solo o exige `UnblockLot`.
6. Cliente y obra del certificado cuando el lote fue a varios conduces: uno por cliente / obra, o elegir el conduce.
7. Qué muestra el conduce impreso: `field_code` en lugar del código interno, o ambos.
