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
  REVIEW: "En revisión",
  OBSOLETE: "Obsoleto",
  BLOCKED_PENDING_SOURCE: "Bloqueada: falta fuente o prueba",
  READY: "Lista para activar",
  RETIRED: "Retirada",
  // E-UI-5: treasury.
  PREPARED: "Preparado",
  RELEASED: "Liberado",
  CLEARED: "Compensado",
  VERIFIED: "Verificada",
  SUPERSEDED: "Reemplazada",
  UNMATCHED: "Sin conciliar",
  CHARGE_RECOGNIZED: "Cargo registrado",
  PAYABLE: "Verificada · pagable",
  HOLD_PENDING: "Retención 72 h",
  NONE: "Sin cuenta",
};

/** E-UI-5: a matched statement line reads "Conciliada" (an invoice match stays "Conciliado"). */
export function lineStatusLabel(status: string | null | undefined): string {
  return status === "MATCHED" ? "Conciliada" : statusLabel(status);
}

export type StatusTone = "neutral" | "progress" | "done" | "attention" | "error" | "reversed";

// Design canvas "Componentes y reglas comunes": the colour always comes with the label.
const TONES: Readonly<Record<string, StatusTone>> = {
  DRAFT: "neutral", VOIDED: "neutral", SUPERSEDED: "neutral", CANCELLED: "neutral", RETIRED: "neutral", NONE: "neutral", NOT_POSTED: "neutral",
  PENDING_APPROVAL: "progress", PREPARED: "progress", RELEASED: "progress", REVIEW: "progress", REQUESTED: "progress", PENDING: "progress",
  OPEN: "progress", REOPENED: "progress", PARTIALLY_RECEIVED: "progress", READY: "progress",
  APPROVED: "done", POSTED: "done", CLEARED: "done", VERIFIED: "done", ACTIVE: "done", MATCHED: "done", RECEIVED: "done", CLOSED: "done",
  CORRECTED: "done", PAYABLE: "done", CHARGE_RECOGNIZED: "done", MATCHED_WITH_TOLERANCE: "done",
  MATCH_EXCEPTION: "attention", UNMATCHED: "attention", HOLD_PENDING: "attention", POSTING_BLOCKED: "attention", BLOCKED_PENDING_SOURCE: "attention",
  REJECTED: "error", EXCEPTIONS: "error", FAILED: "error",
  REVERSED: "reversed",
};

export function statusTone(status: string | null | undefined): StatusTone {
  return (status && TONES[status]) || "neutral";
}

export function statusLabel(status: string | null | undefined): string {
  return status ? (STATUS[status] ?? status) : "—";
}

export const COMPONENTS: Readonly<Record<string, string>> = {
  "INV-MOV": "Movimientos de inventario",
  "AP-REC": "Cuentas por pagar",
  "BANK-REC": "Bancos",
  "ACR-NTX": "Ajustes contables",
  "ACR-TAX": "Ajustes con efecto fiscal",
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

/** Role names as seeded (§14, VS#2 §7, E-B03-14). */
export const ROLES: Readonly<Record<string, string>> = {
  COMPRADOR: "Comprador",
  APROBADOR_COMPRAS: "Aprobador de compras",
  ALMACENISTA: "Almacenista",
  CUENTAS_POR_PAGAR: "Cuentas por pagar",
  CONTROLLER: "Controller",
  TESORERO: "Tesorero",
  ESPECIALISTA_FISCAL: "Especialista fiscal",
  ANALISTA_FISCAL: "Analista fiscal",
  ADMIN_SEGURIDAD: "Administrador de seguridad",
  AUDITOR: "Auditor",
  APROBADOR_POLITICAS: "Aprobador de políticas contables",
  SEGUNDO_APROBADOR_CIERRE: "Segundo aprobador de cierre",
  SEGUNDO_APROBADOR_SEGURIDAD: "Segundo aprobador de seguridad",
  PROBADOR: "Probador",
  DIRECTOR: "Director (solo lectura)",
  CONTADOR: "Contador",
};
