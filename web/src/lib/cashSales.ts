import { compareDecimals } from "@/lib/quotes";

// CF1-05 (E-CF1-05-1…13): the «Venta de contado» screens. Every amount is the server's; here only labels, checks and choices.

/** E-CF1-2: how a final consumer may be identified. */
export const BUYER_ID_KINDS: Readonly<Record<string, string>> = {
  CEDULA: "Cédula",
  RNC: "RNC",
  PASAPORTE: "Pasaporte",
};

/** The buyer's identification as typed: digits for a cédula or an RNC, capitals without spaces for a passport. */
export function normalizeBuyerId(kind: string, value: string): string {
  return kind === "PASAPORTE" ? value.replace(/\s+/g, "").toUpperCase() : value.replace(/\D/g, "");
}

/** What is wrong with the identification typed, or null. Both empty is fine: it is optional below the rule's amount. */
export function buyerIdError(kind: string, value: string): string | null {
  const id = normalizeBuyerId(kind, value);
  if (kind === "" && value.trim() === "") {
    return null;
  }
  if (kind === "") {
    return "Elija el tipo de identificación.";
  }
  if (kind === "CEDULA") {
    return id.length === 11 ? null : "La cédula tiene 11 dígitos.";
  }
  if (kind === "RNC") {
    return id.length === 9 ? null : "El RNC tiene 9 dígitos.";
  }
  return /^[A-Z0-9]{5,20}$/.test(id) ? null : "El pasaporte tiene de 5 a 20 letras y números.";
}

/** E-CF1-05-1: how a cash sale's customer reads in lists — «Consumidor final — María Pérez». */
export function cashCustomerName(customerName: string, buyerName: string | null | undefined): string {
  return buyerName ? `${customerName} — ${buyerName}` : customerName;
}

/**
 * E-CF1-05-5: cash and cheques are received for what is still to pay, never more (change is handed over, not recorded); a transfer
 * may be for more, and what exceeds stays on the receipt to be returned through the bank.
 */
export function paymentAmountError(method: string, amount: string, stillToPay: string): string | null {
  if (method !== "TRANSFER" && compareDecimals(amount, stillToPay) === 1) {
    return method === "CASH"
      ? "El efectivo se registra por lo que falta por pagar, no por más: el vuelto se entrega en mano."
      : "El cheque no puede ser por más de lo que falta por pagar.";
  }
  return null;
}

/** What of a receipt is assigned to the sale: the smaller of what the receipt has and what the sale still needs. */
export function amountToAssign(available: string, stillToPay: string): string {
  return compareDecimals(available, stillToPay) === 1 ? stillToPay : available;
}

/** E-CF1-05-8: why a payment does not count yet, or null when it counts. */
export function paymentPendingReason(payment: { counts: boolean; method: string; receiptStatus: string }): string | null {
  if (payment.counts) {
    return null;
  }
  if (payment.receiptStatus !== "RECORDED") {
    return "No cuenta: el recibo fue anulado o el cheque fue devuelto.";
  }
  return payment.method === "CHEQUE" ? "Pendiente: el cheque cuenta cuando el banco lo acredite." : "Pendiente.";
}

/** E-CF1-05-2: the three steps of the screen and which one the sale is at. */
export type CashSaleStep = "PRODUCTS" | "PAYMENT" | "DISPATCH" | "DONE" | "CANCELLED";

export function cashSaleStep(status: string): CashSaleStep {
  if (status === "DRAFT") {
    return "PRODUCTS";
  }
  if (status === "PENDING_PAYMENT") {
    return "PAYMENT";
  }
  if (status === "CANCELLED") {
    return "CANCELLED";
  }
  return status === "CONFIRMED" || status === "PARTIALLY_DELIVERED" ? "DISPATCH" : "DONE";
}
