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
  // VS#3 (VS3-10a): customers, orders and deliveries.
  BLOCKED: "Bloqueado",
  INACTIVE: "Inactivo",
  PENDING_CREDIT: "Pendiente de crédito",
  CONFIRMED: "Confirmado",
  PARTIALLY_DELIVERED: "Entregado parcialmente",
  DELIVERED: "Entregado",
  PLANNED: "Planificado",
  LOADING: "Cargando",
  LOADED: "Cargado",
  IN_TRANSIT: "En tránsito",
  DELIVERED_WITH_EXCEPTIONS: "Entregado con excepciones",
  RETURNED: "Devuelto",
  AUTO_APPROVED: "Aprobado automáticamente",
  NEEDS_APPROVAL: "Requiere aprobación",
  TRANSFERRED: "Control transferido",
  RETAINED: "Control retenido",
  // VS#3 (VS3-10b): invoices, credit notes, receipts and deposits.
  PENDING_EXTERNAL: "e-CF pendiente",
  NOT_FOUND: "No aparece en el padrón",
  NOT_ACTIVE: "No está activo",
  NAME_DIFFERS: "Nombre distinto",
  ACCEPTED_EXTERNAL: "e-CF aceptado",
  PARTIALLY_PAID: "Cobrada parcialmente",
  PAID: "Cobrada",
  CREDITED: "Acreditada",
  UNAPPLIED: "Sin aplicar",
  PARTIALLY_APPLIED: "Aplicado parcialmente",
  APPLIED: "Aplicado",
  DEPOSITED: "Depositado",
  BOUNCED: "Cheque devuelto",
  RECORDED: "Registrado",
  // MFG-1 (MFG1-07): production runs, finished-goods lots and cost collectors.
  IN_PROGRESS: "En proceso",
  COMPLETED: "Completada",
  CURING: "En curado",
  SCRAPPED: "Desechado",
  SETTLED: "Liquidado",
  // FIS-1 (FIS1-05): fiscal authorizations (CONFOTUR).
  PENDING_VERIFICATION: "Pendiente de verificación",
  SUSPENDED: "Suspendido",
  EXHAUSTED: "Agotado",
  EXPIRED: "Vencido",
  // QUO-1 (QUO1-04): sales quotations (the quote screens use quoteStatusLabel, feminine; these serve any other place).
  SENT: "Enviada",
  CONVERTED: "Convertida en pedido",
  LOST: "Perdida",
};

/** E-VS3-5: the two delivery terms. */
export const DELIVERY_TERMS: Readonly<Record<string, string>> = {
  PICKUP_AT_PLANT: "Retira en planta",
  DELIVERED_OWN_TRANSPORT: "Entregado en obra (camión propio)",
};

/** E-VS3-01-16: categories of finished goods. */
export const FINISHED_GOOD_CATEGORIES: Readonly<Record<string, string>> = {
  BLOQUE: "Bloque",
  ADOQUIN: "Adoquín",
  OTRO_PT: "Otro producto terminado",
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
  INACTIVE: "neutral", PLANNED: "neutral", RETAINED: "neutral",
  PENDING_CREDIT: "progress", PARTIALLY_DELIVERED: "progress", LOADING: "progress", LOADED: "progress", IN_TRANSIT: "progress", NEEDS_APPROVAL: "progress",
  CONFIRMED: "done", DELIVERED: "done", AUTO_APPROVED: "done", TRANSFERRED: "done",
  BLOCKED: "attention", DELIVERED_WITH_EXCEPTIONS: "attention", RETURNED: "attention",
  PENDING_EXTERNAL: "attention", UNAPPLIED: "attention", BOUNCED: "error",
  PARTIALLY_PAID: "progress", PARTIALLY_APPLIED: "progress", DEPOSITED: "progress", RECORDED: "progress",
  ACCEPTED_EXTERNAL: "done", PAID: "done", CREDITED: "done", APPLIED: "done",
  NOT_FOUND: "error", NOT_ACTIVE: "attention", NAME_DIFFERS: "attention", // E-RNC-7
  IN_PROGRESS: "progress", CURING: "progress", COMPLETED: "done", SETTLED: "done", SCRAPPED: "error", // MFG1-07
  PENDING_VERIFICATION: "progress", SUSPENDED: "attention", EXHAUSTED: "neutral", EXPIRED: "neutral", // FIS1-05
  SENT: "progress", CONVERTED: "done", LOST: "neutral", // QUO1-04
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
  "AR-REC": "Cuentas por cobrar",
  "OP-DAY": "Producción del día",
  "COST-SET": "Liquidación de costos",
};

/** FIS1-05: the reconciliations of FIS1-04 in Spanish (the others show the server's description). */
export const RECONCILIATIONS: Readonly<Record<string, string>> = {
  "AUTH-CONSUMPTION": "Consumo de autorizaciones fiscales",
  "EXEMPT-WITHOUT-AUTH": "Facturas exentas sin autorización",
  "AUTH-EXPIRY": "Vencimiento de autorizaciones fiscales",
  "TAX-606": "Formato 606 contra el ITBIS contabilizado", // FIS2-03 (E-FIS2-03-6)
};

/** FIS1-05: exception classifications in Spanish; the others are shown as the server sends them. */
export const EXCEPTION_CLASSIFICATIONS: Readonly<Record<string, string>> = {
  AUTH_LINE_CONSUMPTION_DIFFERENCE: "Consumido de la línea ≠ consumos − devoluciones",
  INVOICE_CONSUMPTION_DIFFERENCE: "Consumo de la factura ≠ su neto − notas de crédito",
  EXEMPT_WITHOUT_AUTHORIZATION: "Factura sin ITBIS que no es e-CF 44",
  AUTHORIZATION_EXPIRING: "Autorización por vencer",
  PROJECT_TERM_ENDED: "Terminó el plazo del proyecto",
  // FIS2-03 (E-FIS2-03-6): TAX-606.
  TAX606_ITBIS_DIFFERENCE: "ITBIS por adelantar del 606 ≠ ITBIS deducible contabilizado del mes",
  CLASSIFICATION_MISSING: "Compra sin clasificación del 606 (tipo de bienes y servicios en blanco)",
  ISR_WITHHOLDING_TYPE_MISSING: "Retención de ISR sin tipo de retención del 606",
};

export function classificationLabel(classification: string): string {
  return EXCEPTION_CLASSIFICATIONS[classification] ?? classification;
}

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
  // VS#3 (E-VS3-3)
  VENDEDOR: "Vendedor",
  CREDITO: "Crédito",
  DESPACHO: "Despacho",
  FACTURACION: "Facturación",
  COBROS: "Cobros",
  // MFG-1 (E-MFG1-14)
  SUPERVISOR_PRODUCCION: "Supervisor de producción",
  GERENTE_PLANTA: "Gerente de planta",
  CALIDAD: "Calidad",
};

/** A yyyy-MM-dd date plus whole days (calendar arithmetic, no time zone involved). */
export function addDays(isoDate: string, days: number): string {
  const [year, month, day] = isoDate.split("-").map(Number);
  return new Date(Date.UTC(year ?? 0, (month ?? 1) - 1, (day ?? 1) + days)).toISOString().slice(0, 10);
}
