# MFG-2 — Producción automática desde el portal de máquinas

Aprobado por Alexander Rochell el 2026-10-08 (erratas E-MFG2-1…14 y E-MFG2-01-1…6 en `../errata.md`). Se construye sobre MFG-1 (máquinas, turnos,
recetas, corridas, resúmenes de turno, consumo P-08, USAGE-TOLERANCE) y sobre el portal de máquinas
(`industriasrochell.com.do`, PHP + MySQL en Hostinger), que pasa al repositorio privado `harochell-tech/portal`.

## 1. Por qué

Las HMI de las bloqueras ya envían cada ciclo al portal (planta, tiempo de ciclo, tiempo muerto acumulado, producto 4/6/8) y el
portal calcula las unidades por hora y por turno con el molde activo. Hoy esas unidades se vuelven a escribir a mano en Core. La
dosificadora (batch plant) enviará al portal el consumo de materia prima de cada turno. Con los dos datos, Core prepara solo el
resumen de turno y compara lo gastado contra lo que dice la receta.

## 2. Alcance

- **Máquinas** (respuesta del dueño): `planta1`, `planta2`, … del portal es el nombre de cada bloquera; todas están en la planta
  MATILLA de Core.
- **Lectura** (E-MFG2-1): Core pide los datos al portal cada 15 min (`data/exportar.php`, clave propia por encabezado): por máquina y
  turno, ciclos, molde, bloques y tiempo muerto; al cierre del turno, el consumo de la dosificadora.
- **Dosificadora** (E-MFG2-2): la HMI envía al cierre de cada turno el total por material a `data/consumo.php`.
- **Equivalencias** (E-MFG2-3): Producción › Portal en Core: máquina del portal → máquina de Core; molde → producto; material de la
  dosificadora → artículo con su unidad. Sin pareja no se importa y se avisa.
- **Borradores** (E-MFG2-4/5/6): por máquina, turno y producto, un resumen de turno en borrador (unidades = ciclos × bloques por
  ciclo), actualizado casi en vivo; corrida abierta automáticamente con la receta vigente si no hay una; el consumo del turno repartido
  entre productos según el teórico de cada uno; lo prepara el proceso diario, lo contabiliza el Supervisor.
- **Comparación** (E-MFG2-7): real (dosificadora) vs teórico (receta × unidades) por material, con aviso fuera de la tolerancia
  de la política de Producción (USAGE-TOLERANCE); no frena.
- **Faltantes** (E-MFG2-8): sin consumo de la dosificadora al cierre, el borrador no se contabiliza hasta que llegue o el Supervisor
  lo escriba con motivo.
- **Seguridad del portal** (E-MFG2-9/10): credenciales fuera del código y de la carpeta pública, contraseña de la base cambiada,
  código en GitHub privado.

- **Dosificadoras** (E-MFG2-11): planta1 ← Dosificadora 1 (sin internet: consumo a mano, sin comparación automática); planta2 y
  planta3 ← Dosificadora 2 (comparación automática).
- **Mantenimientos** (E-MFG2-12): en el portal, programados o al momento; sus ciclos no cuentan como producción.
- **Turnos** (E-MFG2-13): T1 y T2 de MATILLA en Core como en el portal.

## 3. Plan de PRs

| PR | Contenido |
| --- | --- |
| MFG2-00 (portal) | Repositorio `harochell-tech/portal` con la versión del servidor; credenciales a configuración; `data/consumo.php`, `data/exportar.php` |
| MFG2-01 | Esquema: equivalencias, lecturas del portal, marca de origen y de consumo en el resumen de turno, permisos del proceso diario |
| MFG2-02 | Lector del portal (servicio), borradores de resumen, reparto del consumo, avisos |
| MFG2-03 | Pantallas: Producción › Portal (equivalencias, estado de la conexión), comparación en el resumen, Inicio; recorrido de prueba |
