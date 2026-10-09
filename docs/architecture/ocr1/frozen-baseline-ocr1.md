# OCR-1 — Facturas de proveedor desde el e-CF recibido, el QR o una foto

Aprobado por Alexander Rochell el 2026-10-09 (erratas E-OCR-1…8 en `../errata.md`). Se construye sobre las facturas de proveedor
(PR-13, GAS-1, USD-1), la pasarela e-CF con Alanube (VS#4), el padrón RNC y el almacén de evidencia de ENT-1 (B2).

## 1. Por qué

Hoy cada factura de proveedor se escribe a mano: proveedor, NCF, fecha, líneas, precios, ITBIS. Desde el 2026-11-01 casi todos los
proveedores formales emiten e-CF, y ese comprobante ya llega a Alanube con todos sus datos. Lo que queda en papel (NCF B01, gastos
menores, proveedores informales) se puede leer de una foto.

## 2. Alcance

- **Tres fuentes** (E-OCR-1), de la más confiable a la menos: e-CF recibido (XML exacto), QR de la factura impresa (encabezado
  exacto), foto o escaneo leído por IA (propuesta a revisar). Ninguna registra ni contabiliza sola: preparan un borrador que una
  persona revisa y aprueba con los comandos de siempre (match, excepción, contabilización).
- **e-CF recibidos** (E-OCR-2): Core consulta cada hora los documentos recibidos en Alanube y prepara un borrador desde el XML.
  Bandeja Compras › Comprobantes recibidos.
- **Aprobación comercial** (E-OCR-3): aceptar o rechazar ante la DGII desde la bandeja; registrar la factura la acepta; rechazar
  pide motivo.
- **QR** (E-OCR-4): «Escanear QR» en teléfono y computadora; llena RNC del emisor, e-NCF, fecha, total y código de seguridad; enlaza
  el XML recibido si ya llegó.
- **OCR** (E-OCR-5/6): solo la imagen va a la API de Anthropic; la clave en el servidor; interruptor Apagado / Encendido. Cada campo
  leído por IA queda marcado; sumas que no cuadran, NCF mal formado o RNC fuera del padrón se marcan en rojo.
- **Imagen** (E-OCR-7): en el bucket privado de B2 (almacén de evidencia de ENT-1), visible desde la factura.
- **Orden** (E-OCR-8): primero e-CF recibidos y QR; después OCR.

## 3. Plan de PRs

| PR | Contenido |
| --- | --- |
| OCR1-01 | Esquema: comprobantes capturados (fuente, encabezado, líneas, archivos, estado, enlace a la factura), respuesta comercial, permisos |
| OCR1-02 | Lector de recibidos de Alanube (servicio, simulado en pruebas), borradores desde el XML, aprobación comercial |
| OCR1-03 | Pantallas: bandeja, detalle, pasar a factura con el formulario lleno, «Escanear QR», Inicio; recorrido de prueba |
| OCR1-04 | OCR: foto o escaneo a B2, lectura por IA (simulada en pruebas), marcas y verificaciones, interruptor; recorrido de prueba |
| OCR1-05 | Matriz de aceptación `docs/acceptance/ocr1.md` |

Las erratas de cada PR se aprueban antes de su código.
