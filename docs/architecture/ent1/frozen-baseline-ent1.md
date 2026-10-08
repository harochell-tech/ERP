# ENT-1 — Entregas con QR desde Core

Aprobado por Alexander Rochell el 2026-10-08 (erratas E-ENT-1…8 en `../errata.md`). Se construye sobre VS3-04 (entregas, salida por
portón, entrega en obra C-09 `RecordPod`, P-15 / P-15R / P-16 / P-30), FLT-01 (ficha del vehículo y chofer en el conduce) y UX3-02
(conduce imprimible). Sustituye el módulo de entregas con QR del portal (`industriasrochell.com.do/entregas`), que hoy toma los datos
de ADM Cloud.

## 1. Por qué

Hoy la oficina crea cada entrega en el portal con los datos de ADM Cloud, imprime un QR y el chofer confirma con foto. Core ya tiene
la entrega, el conduce y la entrega en obra con su efecto contable, pero la confirmación se escribe a mano en Core. Con ENT-1 el QR
sale del conduce de Core y la confirmación del chofer queda como la entrega en obra, sin pasar por el portal ni por ADM Cloud.

## 2. Alcance

- **Enlace del chofer** (E-ENT-1): al registrar la salida por portón (C-08) Core crea para esa entrega un enlace propio, de un solo
  uso, que vence a los 7 días. El conduce impreso lleva su QR. La página es pública en el dominio de Core; no hay inicio de sesión.
  El portal no participa.
- **PIN del chofer** (E-ENT-2): cada chofer de Maestros › Choferes tiene un PIN de 4 dígitos que le asigna el Gerente de Despacho
  (se guarda solo su hash). La página pide el PIN del chofer asignado a la entrega. Cinco intentos fallidos bloquean el enlace; Despacho
  lo reabre.
- **Qué registra** (E-ENT-3): nombre de quien recibe (y cédula opcional), foto de la entrega o firma en pantalla (obligatoria), hora
  de la confirmación y ubicación del teléfono si el chofer la permite; «Recibido completo» o «Hubo diferencias» con una nota.
- **Efecto en Core** (E-ENT-4): «Recibido completo» registra la entrega en obra (C-09) con todo recibido, ejecutada por la identidad
  de servicio «Confirmación de entrega», que solo tiene ese permiso. «Hubo diferencias» no registra nada: queda pendiente para Despacho,
  que completa la entrega en obra con devueltos, faltantes y motivo partiendo de lo escrito por el chofer.
- **Evidencia** (E-ENT-5): la foto o firma se reduce en el teléfono (lado mayor ≈ 1600 px) y se guarda en Backblaze B2, en un
  depósito privado distinto del WORM. La entrega en obra guarda la referencia del objeto y su SHA-256; Despacho la ve desde el detalle
  de la entrega.
- **Sin señal** (E-ENT-6): la página guarda la confirmación en el teléfono y la envía sola al volver la señal; la hora registrada es
  la de la confirmación (dentro de los límites del enlace), no la del envío.
- **Transición** (E-ENT-7): desde el 2026-11-01 (staging pasa a producción) las entregas nuevas salen solo de Core. El módulo de
  entregas del portal queda en solo lectura para el historial; al cerrar noviembre se borran del portal las credenciales de ADM Cloud.
- **Conduce** (E-ENT-8): el QR va abajo a la derecha con «Chofer: escanee para confirmar la entrega». Un conduce sin salida por
  portón conserva la marca de agua y no lleva QR.

Fuera de alcance: retiros en planta (ya transfieren el control en la salida, P-16); devoluciones totales (`RecordReturnTrip` sigue en
Despacho); seguimiento del camión en ruta.

## 3. Seguridad

- El enlace lleva un token aleatorio de 256 bits; Core guarda solo su hash. Sirve para una entrega, vence, y se anula al confirmar,
  al cancelar la entrega o al registrar la entrega en obra a mano.
- La página pública solo muestra el número de conduce, el cliente, el destino, el chofer y las líneas (sin precios).
- Límite de intentos por enlace (PIN) y por dirección IP; las subidas tienen tamaño máximo y solo imagen JPEG / PNG.
- La identidad «Confirmación de entrega» no inicia sesión: el servidor la usa solo dentro de la confirmación pública.

## 4. Plan de PRs

| PR | Contenido |
| --- | --- |
| ENT1-01 | Esquema: PIN del chofer (hash, intentos), enlace de confirmación por entrega (hash, vence, estado), confirmación del chofer (inmutable), referencia de evidencia; rol de servicio CONFIRMACION_ENTREGA; permisos `driver_pin:manage`, `delivery_link:reopen`. |
| ENT1-02 | Servidor: enlace creado en la salida por portón, `SetDriverPin`, `ReopenDeliveryLink`, almacén de evidencia en B2 (simulado en pruebas y en el entorno local), endpoints públicos (ver entrega, confirmar con PIN y foto), entrega en obra automática si es completa, pendientes con diferencias para Despacho. |
| ENT1-03 | Pantallas: QR en el conduce, PIN en Maestros › Choferes, página pública del chofer (con cola sin señal), confirmación y foto en el detalle de la entrega, «Completar entrega en obra» desde lo del chofer, contador en Inicio; recorrido Playwright E2E-ENT; matriz de aceptación `docs/acceptance/ent1.md`. |

## 5. Lo que hace el dueño

- Crear en Backblaze el depósito privado de evidencias y una llave limitada a él; se carga como archivo en el servidor (no en el chat).
- Asignar los PIN a los choferes en Maestros › Choferes.
- Avisar a los choferes del cambio a partir del 2026-11-01.
