// UX4-02 (E-UX4-1, 2, 14, 15): pure helpers of the Cierre, Auditoría and Fiscal screens. No money arithmetic: totals and
// differences are the server's; only labels, dates as text and whole-day counts are handled here. Unit-tested in
// tests/unit/ux4a-auditoria.test.ts.

/** A-22: a run's totals with the reconciliation's own labels ("Saldo del mayor", "Extracto más partidas en tránsito"). */
export function sideLabels(run: { sideALabel?: string | null; sideBLabel?: string | null }): { a: string; b: string } {
  const a = run.sideALabel?.trim();
  const b = run.sideBLabel?.trim();
  return { a: a ? a : "Total A", b: b ? b : "Total B" };
}

/** A-08: the document an event belongs to, by its aggregate type, with its article ("de la recepción", "del pago"). */
const AGGREGATES: Readonly<Record<string, string>> = {
  GoodsReceipt: "de la recepción",
  GoodsReceiptReversal: "de la reversa de recepción",
  ReceiptCorrection: "de la corrección de recepción",
  SupplierInvoice: "de la factura de proveedor",
  Payment: "del pago",
  SupplierPayment: "del pago",
  BankStatementLine: "de la línea del extracto",
  ManualJournal: "del ajuste",
  Invoice: "de la factura",
  SalesInvoice: "de la factura",
  CreditNote: "de la nota de crédito",
  Receipt: "del recibo de cobro",
  Deposit: "del depósito",
  ReceiptDeposit: "del depósito",
  Delivery: "del conduce",
  ProductionRun: "de la corrida de producción",
  ShiftSummary: "del resumen de turno",
  OpeningInventory: "del inventario de apertura",
  CostCollector: "de la liquidación de costos",
};

/** A-08: "Asientos de la recepción RM-2026-000001"; "Asientos del documento" when neither kind nor number is known. */
export function journalsTitle(aggregateType: string | null | undefined, documentNumber: string | null | undefined): string {
  const kind = aggregateType ? AGGREGATES[aggregateType] : undefined;
  const number = documentNumber?.trim() ?? "";
  if (kind) {
    return number ? `Asientos ${kind} ${number}` : `Asientos ${kind}`;
  }
  return number ? `Asientos del documento ${number}` : "Asientos del documento";
}

/** A-07: the search box accepts 1–100 characters (SearchJournals' rule); a blank search is not sent. */
export function journalSearchText(typed: string): string | null {
  const text = typed.trim();
  return text.length >= 1 && text.length <= 100 ? text : null;
}

/** A-09: the three ledgers of the hash chain. */
export const LEDGERS: Readonly<Record<string, string>> = { GL: "Libro mayor", INV: "Inventario", DOMAIN_EVENT: "Eventos del sistema" };

export function ledgerLabel(code: string): string {
  return LEDGERS[code] ?? code;
}

/** G-20 (P-7): what a TEST or an official source may do. */
export function sourceEnvironmentLabel(environment: string): string {
  return environment === "PRODUCTION" ? "Oficial" : "Prueba";
}

export function sourceEnvironmentHint(environment: string): string {
  return environment === "PRODUCTION"
    ? "Documento oficial de la DGII: puede activar reglas fiscales en producción."
    : "Solo para pruebas: nunca activa una regla en la base de datos de producción (P-7).";
}

/** G-20: a short fingerprint for display ("3f2a9c…"); the full hash goes in the tooltip. */
export function shortHash(hash: string | null | undefined, length = 8): string {
  const text = hash?.trim() ?? "";
  return text.length > length ? `${text.slice(0, length)}…` : text || "—";
}

/** G-25: e-CF types of the DGII by code ("31 — Crédito fiscal"); an unknown code as it comes. */
export const ECF_TYPES: Readonly<Record<string, string>> = {
  "31": "Crédito fiscal",
  "32": "Consumo",
  "33": "Nota de débito",
  "34": "Nota de crédito",
  "41": "Compras",
  "43": "Gastos menores",
  "44": "Regímenes especiales",
  "45": "Gubernamental",
  "46": "Exportaciones",
  "47": "Pagos al exterior",
};

export function ecfTypeLabel(code: string | null | undefined): string {
  if (!code) {
    return "—";
  }
  const name = ECF_TYPES[code];
  return name ? `${code} — ${name}` : code;
}

/** G-24: the month picker's bounds are the text "yyyy-MM"; AAAAMM is what the server takes. */
export function monthValueToPeriod(value: string): string | null {
  return /^\d{4}-(0[1-9]|1[0-2])$/.test(value) ? value.replace("-", "") : null;
}

const MONTHS = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

/** G-24: "202609" → "septiembre 2026" (the period as a reader says it); the text as is when it is not a period. */
export function periodLabel(period: string): string {
  const match = /^(\d{4})(\d{2})$/.exec(period);
  const month = match ? MONTHS[Number(match[2]) - 1] : undefined;
  return match && month ? `${month} ${match[1]}` : period;
}

/** G-26: the server's whole days to expiry in words; null without a date. */
export function expiryText(daysToExpiry: number | null | undefined): string | null {
  if (daysToExpiry === null || daysToExpiry === undefined) {
    return null;
  }
  if (daysToExpiry === 0) {
    return "Vence hoy";
  }
  if (daysToExpiry > 0) {
    return daysToExpiry === 1 ? "Vence mañana" : `Vence en ${daysToExpiry} días`;
  }
  const ago = -daysToExpiry;
  return ago === 1 ? "Venció ayer" : `Venció hace ${ago} días`;
}

/** G-26: the tone of the expiry text: attention within 30 days (or past), neutral otherwise. */
export function expiryTone(daysToExpiry: number | null | undefined): "attention" | "neutral" {
  return daysToExpiry !== null && daysToExpiry !== undefined && daysToExpiry <= 30 ? "attention" : "neutral";
}
