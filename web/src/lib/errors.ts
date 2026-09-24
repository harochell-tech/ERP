// E-PR18b-11: errors are shown by code, in Spanish. Unknown codes fall back to the API message and the correlation id.
import { ApiError } from "@/api/client";

export const ERROR_MESSAGES: Readonly<Record<string, string>> = {
  SESSION_INVALID: "Su sesión no es válida. Inicie sesión de nuevo.",
  SESSION_EXPIRED: "Su sesión expiró. Inicie sesión de nuevo.",
  NOT_AUTHORIZED: "No tiene permiso para esta acción en esta empresa o planta.",
  STEP_UP_REQUIRED: "Esta acción requiere que vuelva a autenticarse.",
  CSRF_HEADER_REQUIRED: "Solicitud rechazada por seguridad. Recargue la página.",
  VERSION_CONFLICT: "El documento cambió mientras lo editaba. Recargue y vuelva a intentarlo.",
  CONCURRENCY_CONFLICT: "Hubo un conflicto con otra operación simultánea. Vuelva a intentarlo.",
  INVALID_REQUEST: "Los datos enviados no son válidos.",
  INVALID_PARAMETER: "Un filtro o parámetro no es válido.",
  NOT_FOUND: "El documento no existe o no pertenece a su planta.",
  SERVICE_UNAVAILABLE: "El servicio no está disponible en este ambiente.",
  INTERNAL_ERROR: "Error inesperado del sistema.",
  APPROVER_IS_CREATOR: "No puede aprobar un documento que usted creó.",
  APPROVAL_LIMIT_EXCEEDED: "El monto supera su límite de aprobación.",
  INVALID_STATE: "El documento no está en un estado que permita esta acción.",
  PLANT_MISMATCH: "El documento pertenece a otra planta.",
  SUPPLIER_NOT_ACTIVE: "El proveedor no está activo.",
  ITEM_NOT_ACTIVE: "El artículo no está activo.",
  UOM_NOT_CONVERTIBLE: "La unidad de medida no tiene conversión vigente para el artículo.",
  LINES_REQUIRED: "Agregue al menos una línea.",
  QUANTITY_INVALID: "La cantidad no es válida.",
  PRICE_INVALID: "El precio no es válido.",
  REASON_REQUIRED: "Indique el motivo.",
  RECEIPT_TOLERANCE_EXCEEDED: "La cantidad supera la tolerancia de recepción de la orden.",
  PURCHASE_ORDER_NOT_RECEIVABLE: "La orden no está aprobada para recibir.",
  LOCATION_NOT_IN_PLANT: "La ubicación no pertenece a la planta de la orden.",
  DUPLICATE_LINE: "Hay líneas repetidas.",
  WEIGH_TICKET_USED: "Ese ticket de báscula ya fue usado.",
  OCCURRED_IN_FUTURE: "La fecha y hora de la recepción no puede estar en el futuro.",
  GOODS_RECEIPT_NOT_REVERSIBLE: "La recepción no se puede reversar (sus lotes ya se movieron o está facturada).",
  ALREADY_INVOICED: "La cantidad ya fue facturada.",
  USE_RECEIPT_CORRECTION: "Use una corrección de recepción en lugar de una reversa.",
  QTY_EXCEPTION_NOT_APPROVABLE: "Un exceso de cantidad no se puede aprobar como excepción.",
  FISCAL_GATE_CLOSED: "No hay reglas fiscales activas para la fecha; no se puede contabilizar.",
  POSTING_PREREQUISITE_MISSING: "Falta configuración contable (regla de posteo, mapeo de cuentas o política) para contabilizar.",
  PERIOD_NOT_ENDED: "El período todavía no ha terminado.",
  COMPONENT_NOT_OPEN: "El componente no está abierto.",
  COMPONENT_NOT_CLOSED: "El componente no está cerrado.",
  INTEGRITY_NOT_SEALED: "Hay registros del período sin sellar; espere al sellador y vuelva a intentarlo.",
  RECONCILIATION_ERRORS: "Hay conciliaciones con errores que bloquean el cierre.",
  REOPEN_ALREADY_REQUESTED: "Ya hay una solicitud de reapertura pendiente.",
  REOPEN_NOT_REQUESTED: "No hay una solicitud de reapertura pendiente.",
  SAME_PERSON: "La misma persona no puede solicitar y aprobar.",
  FISCAL_NUMBER_INVALID: "El NCF no tiene un formato válido (B + 10 dígitos o E + 12 dígitos).",
  FISCAL_NUMBER_USED: "Ese NCF ya está registrado para el proveedor.",
  QTY_EXCEEDS_AVAILABLE: "La cantidad supera lo recibido y aún no facturado.",
  PO_LINE_NOT_INVOICEABLE: "La línea de la orden no se puede facturar.",
  AP_NOT_OPEN: "La cuenta por pagar ya tiene pagos o aplicaciones; no se puede reversar.",
  ALREADY_REVERSED: "El documento ya fue reversado.",
  EVIDENCE_REQUIRED: "Indique la evidencia (ticket corregido, foto o registro).",
  FOUR_EYES_REQUIRED: "Otra persona debe aprobar esta acción.",
  DATE_INVALID: "La fecha no es válida.",
  LINE_NOT_FOUND: "La línea no existe en el documento.",
  RECEIPT_CORRECTION_NOT_PENDING: "La corrección ya no está pendiente.",
  RNC_INVALID: "El RNC no es válido.",
  RNC_DUPLICATE: "Ya existe un proveedor con ese RNC.",
};

export interface DescribedError {
  message: string;
  detail?: string;
  correlationId?: string;
}

export function describeError(error: unknown): DescribedError {
  if (error instanceof ApiError) {
    const known = ERROR_MESSAGES[error.code];
    return known
      ? { message: known, correlationId: error.correlationId }
      : { message: `Error ${error.code}`, detail: error.message, correlationId: error.correlationId };
  }
  return { message: "No se pudo comunicar con el servidor.", detail: error instanceof Error ? error.message : String(error) };
}
