// GAS1-07 (E-GAS-07-1…8): purchases of expenses and services on screen. Every amount is the server's; here only labels and checks.

/** E-GAS-2: an expense category is a service or a good (withholdings and the 606's amount columns). */
export const LINE_CLASS_LABELS: Readonly<Record<string, string>> = {
  SERVICE: "Servicio",
  GOODS: "Bien",
};

/** An expense line as typed. */
export interface ExpenseLine {
  description: string;
  expenseCategoryId: string;
  taxTypeId: string;
  quantity: string;
  unitPrice: string;
  /** The order line it bills, when the invoice cites an order (E-GAS-05-2): category and tax type are then the order's. */
  purchaseOrderLineId?: string;
}

export const EMPTY_EXPENSE_LINE: ExpenseLine = { description: "", expenseCategoryId: "", taxTypeId: "", quantity: "1", unitPrice: "" };

/** The code of a category from its name, as E-GAS-03-5 has it: capitals, digits and underscores ("Teléfono e internet" → "TELEFONO_E_INTERNET"). */
export function categoryCode(name: string): string {
  return name
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "")
    .toUpperCase()
    .replace(/[^A-Z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "")
    .replace(/^([0-9])/, "C_$1")
    .slice(0, 40);
}

/** E-FIS2-01: the 606 type ("01"…"11") with its DGII name. */
export function goodsTypeLabel(code: string, names: Readonly<Record<string, string>>): string {
  return names[code] ? `${code} — ${names[code]}` : code;
}
