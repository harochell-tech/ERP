// Spanish labels for the statuses the API returns (E-PR18b-11). Unknown values are shown as they come.
const STATUS: Readonly<Record<string, string>> = {
  DRAFT: "Borrador",
  PENDING_APPROVAL: "Pendiente de aprobación",
  APPROVED: "Aprobado",
  PARTIALLY_RECEIVED: "Recibido parcialmente",
  RECEIVED: "Recibido",
  CLOSED: "Cerrado",
  CANCELLED: "Cancelado",
  POSTED: "Contabilizado",
  CORRECTED: "Corregido",
  REVERSED: "Reversado",
  MATCH_EXCEPTION: "Excepción de conciliación",
  MATCHED: "Conciliado",
  VOIDED: "Anulado",
  NOT_POSTED: "No contabilizado",
  POSTING_BLOCKED: "Contabilización bloqueada",
  OPEN: "Abierto",
  REOPENED: "Reabierto",
  EXCEPTIONS: "Con excepciones",
  REJECTED: "Rechazado",
  ACTIVE: "Activo",
  PENDING: "Pendiente",
  REQUESTED: "Solicitada",
  MATCHED_WITH_TOLERANCE: "Conciliado dentro de tolerancia",
  FAILED: "Falló",
};

export function statusLabel(status: string | null | undefined): string {
  return status ? (STATUS[status] ?? status) : "—";
}

export const COMPONENTS: Readonly<Record<string, string>> = {
  "INV-MOV": "Movimientos de inventario",
  "AP-REC": "Cuentas por pagar",
  "BANK-REC": "Bancos",
};

export function formatDate(value: string | null | undefined): string {
  if (!value) {
    return "—";
  }
  const [year, month, day] = value.slice(0, 10).split("-");
  return `${day}/${month}/${year}`;
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) {
    return "—";
  }
  return new Date(value).toLocaleString("es-DO", { timeZone: "America/Santo_Domingo", dateStyle: "short", timeStyle: "short" });
}

/** Today's date in the Dominican Republic (the business calendar of the slice), as yyyy-MM-dd. */
export function todayInDominicanRepublic(now: Date = new Date()): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(now);
}
