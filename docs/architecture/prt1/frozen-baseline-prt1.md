# PRT-1 — Formatos de impresión editables

Aprobado por Alexander Rochell el 2026-10-08 (erratas E-PRT-1…10 en `../errata.md`). Hoy cada documento tiene dos diseños: la
página «Imprimir» de la web y el HTML del servidor que se convierte en PDF para el correo (`Rochell.Sales/Mail/DocumentHtml.cs`).
PRT-1 los sustituye por un solo formato por documento, guardado en Core y editable desde la pantalla.

## 1. Alcance

- **Un formato** (E-PRT-1): pantalla, PDF del correo y reimpresión salen del mismo formato.
- **Documentos** (E-PRT-2): conduce, factura (e-CF 31 / 32 / 44), cotización, proforma, estado de cuenta, antigüedad; nuevos: nota de
  crédito (e-CF 34), recibo de cobro, devolución al cliente (DEV-), orden de compra.
- **Pantalla sencilla** (E-PRT-3): Configuración › Formatos de impresión, con vista previa de un documento real: logo, colores, letra,
  papel (carta, media carta, ticket 80 mm), márgenes, columnas (cuáles, orden, ancho, alineación), alto de fila, tamaño de letra,
  textos fijos (encabezado, pie, condiciones, cuentas bancarias, firma).
- **Modo avanzado** (E-PRT-4): plantilla HTML / CSS con variables en un lenguaje de plantillas seguro (sin acceso fuera del documento).
- **Obligatorio** (E-PRT-5): no se activa un formato sin lo exigido — factura y nota de crédito: e-NCF, RNC del emisor y del comprador,
  QR, código de seguridad, fecha de firma; conduce: QR del chofer (ENT-1) y marca de agua antes del portón. Se comprueba dibujando un
  documento de prueba.
- **Versiones** (E-PRT-6): borrador con vista previa → activar (con reautenticación); historial y vuelta a una versión anterior.
- **Reimpresión** (E-PRT-7): con el formato activo; el PDF enviado guarda la versión usada y queda copia.
- **Logo** (E-PRT-8): PNG o JPEG hasta 1 MB, uno por empresa; cada formato decide si lo muestra y su tamaño; sin SVG; también
  reemplaza el monograma del ícono de la aplicación.
- **Permiso** (E-PRT-9): `print_format:manage` para Director y Superadministrador.
- **Variantes** (E-PRT-10): un formato activo por documento y empresa; variantes por cliente o planta, más adelante.

## 2. Plan de PRs

| PR | Contenido |
| --- | --- |
| PRT-01 | Esquema (formatos, versiones, logo), motor de plantillas, los formatos actuales pasados a plantillas sin cambio visible; la web imprime el HTML del servidor. |
| PRT-02 | Configuración › Formatos de impresión: ajustes sencillos, modo avanzado, vista previa, logo, validación de lo obligatorio, historial. |
| PRT-03 | Impresiones nuevas: nota de crédito, recibo de cobro, devolución, orden de compra. |

Orden: después de ENT1-01 (el conduce nace en plantilla con el QR del chofer) y antes del 2026-11-01.
