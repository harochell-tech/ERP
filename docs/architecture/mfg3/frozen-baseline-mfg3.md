# MFG-3 — Reporte diario, eficiencia de las máquinas y mantenimiento preventivo

Aprobado por Alexander Rochell el 2026-10-08 (erratas E-MFG3-1…11 en `../errata.md`). Se construye sobre MFG-2 (lectura del
portal de máquinas, borradores de turno, consumo de la dosificadora) y sobre los módulos del portal (`industriasrochell.com.do`):
checklist y reporte diario de planta (`produccion/`), reporte de paros (`data/reporte_paros.php`, `paros_planta`) y mantenimientos
(`data/mantenimientos.php`, MFG2-00).

## 1. Por qué

El supervisor vuelve a escribir en el reporte diario del portal lo que las máquinas ya miden (producción por tamaño, consumo), y los
rotos se escriben otra vez en Core. Los paros y los mantenimientos ya llegan al portal, pero nadie los convierte en disponibilidad ni
en costo. El mantenimiento preventivo se hace de memoria, aunque el portal cuenta cada ciclo de cada máquina.

## 2. Alcance

### Reporte diario
- **Prellenado** (E-MFG3-1): el reporte del portal trae bloqueados la producción por tamaño (ciclos × molde, sin ciclos en
  mantenimiento) y, para plantas 2 y 3, el consumo de la dosificadora 2. El supervisor escribe solo rotos, calidad, horas y
  observaciones. En planta1 el consumo sigue a mano.
- **Rotos una sola vez** (E-MFG3-2): los «Bloques rechazados / dañados» del reporte pasan como merma en fresco al borrador del turno
  en Core, si nadie lo ha cambiado en Core. Con un solo turno va directo; con dos turnos se reparte a mano en Core.

### Eficiencia
- **Datos** (E-MFG3-3): la exportación del portal añade los paros (inicio, duración, razón) y los mantenimientos (inicio, fin);
  Core los guarda inmutables, como las lecturas.
- **Fórmulas** (E-MFG3-4), por máquina y turno:
  - Tiempo planificado = duración del turno − mantenimiento programado.
  - Tiempo en marcha = tiempo planificado − paros.
  - Disponibilidad = tiempo en marcha ÷ tiempo planificado.
  - Rendimiento = ciclos × ciclo ideal ÷ tiempo en marcha.
  - Calidad = buenas ÷ (buenas + merma de mezcla + merma en fresco).
  - Eficiencia general = disponibilidad × rendimiento × calidad.
- **Ciclo ideal** (E-MFG3-5): segundos por ciclo por máquina y molde, en Core, fijados por el Gerente de planta. Sin ese dato no se
  calcula el rendimiento y se avisa. Propuesta inicial: el promedio de los ciclos de las mejores horas del último mes, que el dueño
  confirma.
- **Costo del paro** (E-MFG3-6): unidades no producidas = paro × capacidad ideal (bloques por ciclo ÷ ciclo ideal); valor = unidades
  × costo estándar del producto. Solo informativo: ningún asiento.
- **Dónde se ve** (E-MFG3-7): columnas en Producción del día por máquina y turno; informe semanal por máquina con razones de paro.
  Los paros sin razón se muestran «Sin razón» y cuentan en Inicio; la razón se sigue escribiendo en el portal.

### Mantenimiento preventivo
- **Planes** (E-MFG3-8): en Core, Producción › Mantenimiento preventivo, el Gerente de planta define por máquina tareas con
  frecuencia cada N ciclos, cada N horas en marcha o cada N días.
- **Hecho** (E-MFG3-9): el mecánico lo marca en el portal, en Mantenimientos, eligiendo la tarea; Core publica al portal la lista de
  tareas y lee de vuelta las hechas. Al hacerse, el contador de la tarea vuelve a cero.
- **Avisos** (E-MFG3-10): «Por vencer» al 90 %, «Vencida» al 100 %; contador en Inicio de Core y notificación push del portal a los
  suscritos con el rol Mantenimiento.
- **Repuestos** (E-MFG3-11): fuera de alcance por ahora.

## 3. Plan de PRs

| PR | Contenido |
| --- | --- |
| MFG3-00 (portal) | Reporte diario prellenado y bloqueado; exportación con rotos del reporte, paros y mantenimientos; `data/planes.php` (Core publica las tareas con su clave) y tarea elegida al terminar un mantenimiento; push «por vencer / vencida». |
| MFG3-01 | Esquema: paros y ventanas de mantenimiento leídos del portal, rotos del reporte, ciclo ideal por máquina y molde, planes y tareas de mantenimiento, tareas hechas; permisos `maintenance_plan:manage`. |
| MFG3-02 | Lectura de paros, mantenimientos y rotos; rotos al borrador (E-MFG3-2); `SetIdealCycle`; consulta de eficiencia por máquina y turno y semanal, con costo del paro. |
| MFG3-03 | Planes y tareas (`DefineMaintenanceTask`, cambiar, desactivar), publicación al portal, lectura de las hechas, estado por tarea (ciclos / horas / días desde la última), contadores. |
| MFG3-04 | Pantallas: columnas de eficiencia en Producción del día, informe semanal, ciclo ideal en Máquinas y turnos, Mantenimiento preventivo, contadores en Inicio; Playwright; matriz `docs/acceptance/mfg3.md`. |

## 4. Lo que hace el dueño

- Confirmar el ciclo ideal de cada máquina y molde (Core propone el promedio de las mejores horas).
- Dar las tareas de mantenimiento preventivo con su frecuencia (por ejemplo, zapatas cada 40,000 ciclos).
- Pedir a los mecánicos que elijan la tarea al terminar un mantenimiento en el portal.
