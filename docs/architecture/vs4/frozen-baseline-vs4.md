# VS#4 — Gateway e-CF con Alanube

Aprobado por Alexander Rochell el 2026-10-07 (erratas E-VS4-1…14 en `../errata.md`). Proveedor elegido: **Alanube** (cierra X-1 del
proveedor). Resumen técnico de la API: `../../fiscal/alanube-api.md`. Se construye sobre VS#3 (facturas y notas de crédito con el canal
externo), CF-1 (e-CF 32), FIS-1 (e-CF 44 CONFOTUR) y MAIL (correo de documentos). La empresa ya está certificada como emisor electrónico
y emite con otro proveedor; desde el 2026-11-01 solo puede facturar con e-CF.

## 1. Por qué

Hoy el e-CF se emite fuera del sistema (portal del proveedor) y se registra a mano en Core (FISCAL_PENDING_EXTERNAL → ISSUED). Con
el volumen de facturas de cada día eso es lento y propenso a errores, y la factura no puede salir por correo sin el QR del timbre DGII.

## 2. Alcance

- **Secuencias** (E-VS4-1): Core asigna el e-NCF (CORE_MANAGED, ADR-034) de un rango autorizado por la DGII por tipo, con su vencimiento,
  registrado en Fiscal por una persona y aprobado por otra; un número usado nunca se reutiliza.
- **Tipos** (E-VS4-2): 31 crédito fiscal, 32 consumo, 34 nota de crédito y 44 régimen especial. Fuera: 33, 41, 43, 45, 46, 47 y la
  recepción de e-CF de proveedores con su aprobación comercial (VS#4b).
- **Envío** (E-VS4-3): al emitir, Core arma el e-CF, asigna el e-NCF y lo deja en una cola; un proceso lo envía a Alanube y consulta
  el estado hasta la respuesta final. El webhook de Alanube solo avisa para consultar antes; su contenido no se cree y se valida un
  encabezado secreto propio.
- **Reenvíos** (E-VS4-4): nunca a ciegas. Tras un error de red se consulta primero; «ya usado» o «en proceso» se resuelve consultando el
  existente; si no se puede determinar, «Requiere atención» en la bandeja de Fiscal.
- **Aceptado** (E-VS4-5): factura emitida con e-NCF, código de seguridad, QR (timbre DGII) y fecha de firma; el XML y el PDF firmados se
  descargan y se guardan en el sistema.
- **Rechazado** (E-VS4-6): la factura vuelve a «por corregir» con el motivo de la DGII; el e-NCF queda consumido y la corrección sale con
  otro número.
- **Nota de crédito 34** (E-VS4-7): cita el e-CF original con el código DGII (1 anula total, 3 corrige montos); una factura aceptada se
  anula siempre con nota de crédito.
- **CONFOTUR 44** (E-VS4-8): el número de la certificación va en «Información adicional» (X-1).
- **Correo** (E-VS4-9): la factura sale por el módulo de correo de Core con el PDF y el QR (E-MAIL-01-2).
- **Contingencia** (E-VS4-10): sin respuesta, la factura queda «pendiente fiscal»; el conduce y el despacho siguen; como último recurso,
  el camino manual de hoy (portal + registro del e-NCF).
- **Ambientes** (E-VS4-11): Apagado / Sandbox / Producción, como el correo; staging en sandbox hasta la orden del dueño; el token de
  Alanube solo como secreto en el servidor.
- **Rangos sobrantes** (E-VS4-12): los números no usados de un rango cerrado o vencido se anulan por la anulación de Alanube, con
  aprobación del Controller.

## 3. Estados del e-CF (Gateway, v2.1 §4.1 adaptado)

| Estado | Significa |
| --- | --- |
| PENDING | Armado, con e-NCF asignado, en la cola |
| SUBMITTED | Alanube lo recibió (tiene su id) y no hay respuesta final |
| UNKNOWN_OUTCOME | El envío no tuvo respuesta clara: se consulta antes de cualquier reintento |
| ACCEPTED / ACCEPTED_CONDITIONAL | La DGII lo aceptó (con observaciones) |
| REJECTED | La DGII lo rechazó: la factura vuelve a corrección |
| REQUIRES_ACTION | No se pudo determinar o hay un error de datos: lo resuelve Fiscal |
| CONTINGENCY | Alanube o la DGII no responden más allá del umbral |
| ACCEPTED_EXTERNAL | Emitido por el portal y registrado a mano (contingencia) |

## 4. Pruebas de aceptación

| ID | Dado | Cuando | Entonces |
| --- | --- | --- | --- |
| ECF-01 | Rango 31 autorizado | Se emite una factura a crédito fiscal | Toma el siguiente e-NCF, se envía, se acepta; la factura muestra e-NCF, código de seguridad y QR; XML y PDF guardados |
| ECF-02 | Factura a consumidor bajo el monto de resumen | Se emite | e-CF 32 aceptado en la misma respuesta |
| ECF-03 | Nota de crédito comercial | Se emite | e-CF 34 con referencia al original y código 3 |
| ECF-04 | Factura CONFOTUR | Se emite | e-CF 44 exento con la certificación en información adicional |
| ECF-05 | La DGII rechaza | — | Factura por corregir con el motivo; el número no se reutiliza; la corrección sale con otro |
| ECF-06 | Timeout al enviar | — | Se consulta antes de reenviar; nunca dos e-CF para una factura |
| ECF-07 | Webhook con encabezado inválido | — | Se ignora; con encabezado válido dispara la consulta |
| ECF-08 | Alanube caído | — | Factura pendiente fiscal; contingencia manual disponible |
| ECF-09 | Rango agotado o vencido | — | No se emite; aviso en Inicio antes de agotarse |
| ECF-10 | Rango cerrado con números sin usar | Se anula | Anulación en Alanube aprobada por el Controller |
| E2E-ECF | Flujo completo contra el Alanube simulado y el sandbox | — | Factura → e-CF aceptado → correo con PDF y QR |

## 5. Dependencias externas

- **Sandbox de Alanube** y su token: CT-01…16 (v2.1 §5.1) ejecutados y firmados antes de Producción (N-01, E-VS4-13).
- **Rangos de e-NCF** autorizados por la DGII (31, 32, 34, 44) y el **corte** con el proveedor actual (último número que él usa).
- **Alanube**: alta de la empresa, certificado digital cargado y la empresa autorizando a Alanube ante la DGII.
- **X-1**: la certificación CONFOTUR en el e-CF 44.

## 6. Plan de PRs

| PR | Contenido |
| --- | --- |
| VS4-01 | Esquema: rangos de e-NCF, documentos fiscales e intentos, configuración del gateway (modo, URLs) |
| VS4-02 | Adaptador de Alanube, cola y consulta de estado contra un Alanube simulado |
| VS4-03 | Armado del 31, 32, 34 y 44 desde facturas y notas de crédito |
| VS4-04 | Webhook, pantallas (rangos, bandeja fiscal, estado y QR en la factura), herramienta del contract test |
| VS4-05 | Puesta en marcha con el sandbox: CT-01…16, matriz de aceptación, correo de facturas |

## 7. Decisiones aprobadas

E-VS4-1…14 en `../errata.md`.
