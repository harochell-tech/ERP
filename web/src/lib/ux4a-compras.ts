// UX4-02 (E-UX4-1…8, 14): pure helpers of the Compras, Almacén and CxP screens. Unit-tested in tests/unit/ux4a-compras.test.ts.
// No arithmetic: totals, nets, differences and open quantities are the server's; these only choose words and shapes.
import { isPositiveDecimal, normalizeInput } from "./decimal";
import { statusLabel } from "./labels";

/** C-08: the `md.uom` codes in words ("tonelada (t)"); an unknown code as it comes. */
const UOM_NAMES: Readonly<Record<string, string>> = {
  kg: "kilogramo (kg)",
  t: "tonelada (t)",
  m3: "metro cúbico (m³)",
  l: "litro (L)",
  un: "unidad (un)",
};

export function uomLabel(code: string | null | undefined): string {
  if (!code) {
    return "—";
  }
  return UOM_NAMES[code] ?? code;
}

/** C-08: an item as "CODE — Description", or just the code when the description repeats it. */
export function itemLabel(code: string, description: string | null | undefined): string {
  const text = description?.trim() ?? "";
  return text === "" || text.toUpperCase() === code.trim().toUpperCase() ? code : `${code} — ${text}`;
}

/**
 * C-07: a supplier invoice's document status. MATCHED reads "Cotejada con OC y recepción" (it was compared with the order and the
 * receipt, not reconciled); the other statuses as everywhere else, in the feminine where the invoice needs it.
 */
const INVOICE_STATUS: Readonly<Record<string, string>> = {
  DRAFT: "Borrador",
  MATCHED: "Cotejada con OC y recepción",
  MATCH_EXCEPTION: "Cotejada con diferencias",
  POSTED: "Contabilizada",
  REVERSED: "Reversada",
  VOIDED: "Anulada",
};

export function invoiceStatusLabel(status: string | null | undefined): string {
  return status ? (INVOICE_STATUS[status] ?? statusLabel(status)) : "—";
}

/**
 * C-07: the accounting status is shown only when it says something the document status does not. NOT_POSTED before posting is
 * implied by a draft or cotejada invoice; POSTED / REVERSED repeat the document status. POSTING_BLOCKED (and anything unknown)
 * is always shown.
 */
export function accountingStatusWorthShowing(documentStatus: string, accountingStatus: string): boolean {
  if (accountingStatus === "NOT_POSTED") {
    return !(documentStatus === "DRAFT" || documentStatus === "MATCHED" || documentStatus === "MATCH_EXCEPTION");
  }
  if (accountingStatus === "POSTED" || accountingStatus === "REVERSED") {
    return accountingStatus !== documentStatus;
  }
  return true;
}

/** C-23: the effect of a determined tax in words. */
const TAX_EFFECTS: Readonly<Record<string, string>> = {
  RECOVERABLE_INPUT: "Crédito fiscal (deducible)",
  NON_RECOVERABLE_INPUT: "No deducible (va al costo)",
  OUTPUT: "ITBIS facturado (por pagar)",
  WITHHOLDING: "Retención",
};

export function taxEffectLabel(effect: string | null | undefined): string {
  return effect ? (TAX_EFFECTS[effect] ?? effect) : "—";
}

/** C-20: the printed total against the invoice's gross total, in words; null when there is nothing to say yet. */
export function printedTotalNotice(printedTotal: string | null | undefined, difference: string | null | undefined): { tone: "done" | "attention"; text: string } | null {
  if (!printedTotal) {
    return null;
  }
  if (difference === null || difference === undefined || difference === "") {
    return { tone: "attention", text: "La diferencia con el total del sistema se conoce al contabilizar." };
  }
  if (!/[1-9]/.test(difference)) {
    return { tone: "done", text: "Coincide con el total calculado por el sistema." };
  }
  return {
    tone: "attention",
    text: difference.startsWith("-") ? "La factura impresa dice menos que el total del sistema." : "La factura impresa dice más que el total del sistema.",
  };
}

/** C-09: the preview is asked only for a form that could be created: plant, supplier, date and at least one complete, valid line. */
export interface PreviewFormLine {
  itemId: string;
  uom: string;
  quantity: string;
  unitPrice: string;
}

export function previewableLines(lines: readonly PreviewFormLine[]): { itemId: string; uom: string; quantity: string; unitPrice: string }[] | null {
  const normalized = lines.map((l) => ({ itemId: l.itemId, uom: l.uom, quantity: normalizeInput(l.quantity), unitPrice: normalizeInput(l.unitPrice) }));
  const complete = normalized.every((l) => l.itemId !== "" && l.uom !== "" && isPositiveDecimal(l.quantity, 6) && isPositiveDecimal(l.unitPrice, 6));
  return complete && normalized.length > 0 ? normalized : null;
}

export function previewKey(header: { plantId: string; partyId: string; orderDate: string }, lines: readonly PreviewFormLine[]): string | null {
  if (!header.plantId || !header.partyId || !header.orderDate) {
    return null;
  }
  const valid = previewableLines(lines);
  return valid === null ? null : JSON.stringify({ ...header, lines: valid });
}

/** C-15: CURADO (finished goods curing) and TRANSITO (goods on the road) never take a receipt (LOCATION_NOT_RECEIVABLE). */
export function isReceivableLocation(code: string): boolean {
  return code !== "CURADO" && code !== "TRANSITO";
}

/** C-10: the orders an approver still has to decide — pending approval and not created by the viewer (when the list says who). */
export function pendingMyApproval<T extends { status: string; createdBy?: string | null }>(orders: readonly T[], isMine: (actor: string | null | undefined) => boolean): T[] {
  return orders.filter((o) => o.status === "PENDING_APPROVAL" && !isMine(o.createdBy ?? null));
}

/** C-18: the corrections filter in words, for the heading and the empty state. */
export function correctionsFilterLabel(status: string): string {
  return status === "PENDING_APPROVAL" ? "Pendientes de aprobación" : "Todas";
}

export function correctionsEmptyText(status: string): string {
  return status === "PENDING_APPROVAL"
    ? "No hay correcciones pendientes de aprobación: el Controller no tiene nada que decidir."
    : "No hay correcciones. Una corrección se registra desde el detalle de la recepción, con «Corregir cantidad», cuando lo recibido no coincide con lo pesado o contado.";
}
