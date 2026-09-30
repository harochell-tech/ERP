// UX4-03: pure helpers of the sales, dispatch, billing and collection screens (unit-tested in tests/unit/ux4b-sales.test.ts). No
// money or quantity arithmetic: every amount is the server's decimal string; only string comparisons and sign handling.
import { formatDecimal } from "./decimal";
import { statusLabel, type StatusTone } from "./labels";

/**
 * E-UX4-11 (V-05, V-23): the accounting status and the control transfer are for who reads the books — Controller, Contador,
 * Auditor (configuration:read or ledger:read) —, not for Ventas, Despacho, Facturación or Cobros.
 */
export function canSeeAccounting(can: (permission: string) => boolean): boolean {
  return can("configuration:read") || can("ledger:read");
}

/** A decimal string that is zero ("0", "0.00", "-0.000"). */
export function isZeroDecimal(value: string | null | undefined): boolean {
  return value !== null && value !== undefined && /^[-+]?0*(\.0*)?$/.test(value.trim()) && value.trim() !== "";
}

/** A decimal string below zero (and not a negative zero). */
export function isNegativeDecimal(value: string | null | undefined): boolean {
  return value !== null && value !== undefined && value.trim().startsWith("-") && !isZeroDecimal(value);
}

/** The absolute value of a decimal string, by dropping its sign (no arithmetic). */
export function absDecimal(value: string): string {
  return value.trim().replace(/^[-+]/, "");
}

/** V-06: a sales invoice's commercial status in the invoice's own words ("Emitida", not "Confirmado"). */
const INVOICE_STATUS: Readonly<Record<string, string>> = {
  DRAFT: "Borrador",
  CONFIRMED: "Emitida",
  PARTIALLY_PAID: "Cobrada en parte",
  PAID: "Cobrada",
  CREDITED: "Acreditada",
  VOIDED: "Anulada",
};

export function invoiceStatusLabel(status: string | null | undefined): string {
  return (status && INVOICE_STATUS[status]) ?? statusLabel(status);
}

/** V-06: a credit note's commercial status ("Emitida", "Anulada"). */
const CREDIT_NOTE_STATUS: Readonly<Record<string, string>> = {
  DRAFT: "Borrador",
  CONFIRMED: "Emitida",
  VOIDED: "Anulada",
};

export function creditNoteStatusLabel(status: string | null | undefined): string {
  return (status && CREDIT_NOTE_STATUS[status]) ?? statusLabel(status);
}

/** V-35 (E-UX4-6): a company bank account as "alias · banco ••••6789" (the number comes masked from the server). */
export function bankAccountLabel(account: { alias?: string | null; bankCode?: string | null; accountNumber?: string | null }): string {
  const bank = [account.bankCode, account.accountNumber].filter((part) => part && part.trim() !== "").join(" ");
  const alias = account.alias?.trim();
  if (alias && bank) {
    return `${alias} · ${bank}`;
  }
  return alias || bank || "—";
}

/** V-36: a receipt's three statuses (E-VS3-07-4) read as one. */
export function receiptStatusSummary(receipt: { status: string; applicationStatus: string; bankStatus: string; method: string }): { label: string; tone: StatusTone } {
  if (receipt.status === "REVERSED") {
    return { label: "Anulado", tone: "reversed" };
  }
  if (receipt.status === "BOUNCED") {
    return { label: "Cheque devuelto", tone: "error" };
  }
  const application =
    receipt.applicationStatus === "APPLIED" ? "Aplicado" : receipt.applicationStatus === "PARTIALLY_APPLIED" ? "Aplicado en parte" : "Sin aplicar";
  const inTransit = receipt.bankStatus === "IN_TRANSIT" && receipt.method !== "TRANSFER";
  const label = inTransit ? `${application} · sin depositar` : application;
  const tone: StatusTone = receipt.applicationStatus === "APPLIED" && !inTransit ? "done" : receipt.applicationStatus === "UNAPPLIED" ? "attention" : "progress";
  return { label, tone };
}

/** V-14 (E-UX4-4): why an order would not be approved automatically, in words. */
export function creditReasonLabel(reason: string, preview?: { overdueDays: number; overdueDaysBlock: number }): string {
  switch (reason) {
    case "CUSTOMER_TERMS_REQUIRED":
      return "El cliente no tiene términos de crédito aprobados.";
    case "CREDIT_HOLD":
      return "El cliente tiene el crédito retenido.";
    case "CREDIT_LIMIT_EXCEEDED":
      return "El monto excede el crédito disponible.";
    case "OVERDUE_DAYS_EXCEEDED":
      return preview
        ? `El cliente tiene facturas vencidas hace ${preview.overdueDays} días (se permiten hasta ${preview.overdueDaysBlock}).`
        : "El cliente tiene facturas vencidas por más días de los permitidos.";
    default:
      return reason;
  }
}

export interface CreditPreviewLike {
  fits: boolean;
  available: string | null;
  availableAfter: string | null;
  overdueDays: number;
  overdueDaysBlock: number;
  reasons: readonly string[];
}

/**
 * V-14: the credit preview in one sentence — "Cabe en el crédito disponible" or "Excede el crédito disponible por RD$ X" (X is the
 * server's availableAfter without its sign) — and the reasons it would go to Crédito.
 */
export function creditPreviewSummary(preview: CreditPreviewLike): { fits: boolean; headline: string; reasons: string[] } {
  const reasons = preview.reasons.map((r) => creditReasonLabel(r, preview));
  if (preview.fits) {
    return { fits: true, headline: "Cabe en el crédito disponible: el pedido se confirmará al enviarlo.", reasons: [] };
  }
  if (preview.availableAfter !== null && isNegativeDecimal(preview.availableAfter)) {
    return { fits: false, headline: `Excede el crédito disponible por RD$ ${formatDecimal(absDecimal(preview.availableAfter))}: irá a Crédito para su aprobación.`, reasons };
  }
  return { fits: false, headline: "No se aprobará automáticamente: irá a Crédito para su aprobación.", reasons };
}

/** V-11: a draft line the preview can price (a product and a quantity greater than zero, up to 6 decimals). */
export function previewableLines<L extends { itemId: string; uom: string; quantity: string }>(lines: readonly L[]): L[] | null {
  const valid = lines.every((l) => l.itemId !== "" && l.uom !== "" && /^\d{1,12}(\.\d{1,6})?$/.test(l.quantity.trim()) && !isZeroDecimal(l.quantity));
  return valid && lines.length > 0 ? lines.map((l) => ({ ...l, quantity: l.quantity.trim() })) : null;
}

/** V-05: a document's history split into its commercial (DOCUMENT) and accounting (ACCOUNTING) status changes. */
export function splitHistory<H extends { statusKind: string }>(history: readonly H[]): { commercial: H[]; accounting: H[] } {
  return { commercial: history.filter((h) => h.statusKind !== "ACCOUNTING"), accounting: history.filter((h) => h.statusKind === "ACCOUNTING") };
}

/**
 * V-25: the quantity still to dispatch, only when the server's figures give it without arithmetic — nothing delivered yet and no
 * open delivery of the order: then it is the ordered quantity. Otherwise null (the server checks the open quantity when planning).
 */
export function pendingWithoutArithmetic(line: { qtyOrdered: string; qtyDelivered: string }, hasOpenDeliveries: boolean): string | null {
  return !hasOpenDeliveries && isZeroDecimal(line.qtyDelivered) ? line.qtyOrdered : null;
}

/** Delivery statuses still holding planned quantity (not yet delivered, returned or cancelled). */
export function isOpenDelivery(status: string): boolean {
  return status === "PLANNED" || status === "LOADING" || status === "LOADED" || status === "IN_TRANSIT";
}

/** V-34 (E-UX4-10): the suggested amount per invoice, as the editable form values (zero suggestions left empty). */
export function suggestionAmounts(invoices: readonly { invoiceId: string; suggested: string }[]): Record<string, string> {
  return Object.fromEntries(invoices.filter((i) => !isZeroDecimal(i.suggested)).map((i) => [i.invoiceId, i.suggested]));
}

/** V-18: a customer of the AR aging with something past due (any bucket beyond "al día"). */
export function hasOverdue(customer: { bucket1: string; bucket2: string; bucket3: string; over: string }): boolean {
  return [customer.bucket1, customer.bucket2, customer.bucket3, customer.over].some((v) => !isZeroDecimal(v));
}

/** V-18: the aging customers matching the name filter (case- and accent-insensitive) and, optionally, only those past due. */
export function filterAging<C extends { customerName: string; bucket1: string; bucket2: string; bucket3: string; over: string }>(
  customers: readonly C[],
  text: string,
  overdueOnly: boolean,
): C[] {
  const fold = (s: string) => s.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
  const wanted = fold(text.trim());
  return customers.filter((c) => (wanted === "" || fold(c.customerName).includes(wanted)) && (!overdueOnly || hasOverdue(c)));
}

/** V-20: the proforma of an order not yet confirmed prints "BORRADOR"; a cancelled one "CANCELADO". */
export function proformaWatermark(orderStatus: string | null | undefined): string | null {
  switch (orderStatus) {
    case "DRAFT":
    case "PENDING_CREDIT":
      return "BORRADOR";
    case "CANCELLED":
      return "CANCELADO";
    default:
      return null;
  }
}

/** V-31: a credit note is offered only on an invoice with its e-CF accepted and something still open (not fully paid). */
export function creditNoteOffered(commercialStatus: string, fiscalStatus: string): boolean {
  return fiscalStatus === "ACCEPTED_EXTERNAL" && (commercialStatus === "CONFIRMED" || commercialStatus === "PARTIALLY_PAID");
}

/** V-27: a delivery lot in words: "Lote L-0001: 100 (sale de CURADO)" — the quantity in the product's base unit. */
export function deliveryLotLabel(lot: { lotCode: string; baseQuantity: string; sourceLocationCode?: string | null }, quantity: (v: string) => string): string {
  const from = lot.sourceLocationCode ? ` (sale de ${lot.sourceLocationCode})` : "";
  return `Lote ${lot.lotCode}: ${quantity(lot.baseQuantity)}${from}`;
}
