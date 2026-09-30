// UX4-02 (C-26, C-28, C-29, C-31): pure helpers of the treasury screens, unit-tested in tests/unit/ux4a-tesoreria.test.ts. No
// money arithmetic: amounts are the server's; only dates are moved and rows counted.
import { addDays } from "./labels";

/** "Todo": no due-date limit in practice (the proposal query needs a date). */
export const ALL_DUE = "9999-12-31";

export type DueShortcut = "week" | "fortnight" | "all";

/** C-26 / E-UX4-12: the proposal's shortcuts; "Esta semana" (today + 7) is the default. */
export const DUE_SHORTCUTS: readonly { id: DueShortcut; label: string }[] = [
  { id: "week", label: "Esta semana" },
  { id: "fortnight", label: "15 días" },
  { id: "all", label: "Todo" },
];

export function dueUntilFor(shortcut: DueShortcut, today: string): string {
  switch (shortcut) {
    case "week":
      return addDays(today, 7);
    case "fortnight":
      return addDays(today, 15);
    default:
      return ALL_DUE;
  }
}

/** The shortcut a date corresponds to, or null for a date typed by hand. */
export function activeShortcut(dueUntil: string, today: string): DueShortcut | null {
  return DUE_SHORTCUTS.find((s) => dueUntilFor(s.id, today) === dueUntil)?.id ?? null;
}

function plural(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`;
}

/**
 * C-26: the empty proposal says how much falls due later (the proposal without a limit, counted): "1 factura de 1 proveedor vence
 * después de esa fecha." Null when nothing is open at all.
 */
export function laterDueText(suppliers: readonly { invoices: readonly unknown[] }[]): string | null {
  const invoices = suppliers.reduce((n, s) => n + s.invoices.length, 0);
  if (invoices === 0) {
    return null;
  }
  const verb = invoices === 1 ? "vence" : "vencen";
  return `${plural(invoices, "factura", "facturas")} de ${plural(suppliers.length, "proveedor", "proveedores")} ${verb} después de esa fecha.`;
}

/** C-28: a statement's lines in words: "2 líneas, ninguna pendiente", "1 de 2 pendiente de conciliar", "Sin líneas" (a recognized charge is not pending). */
export function statementLinesText(lines: number, unmatched: number): string {
  if (lines === 0) {
    return "Sin líneas";
  }
  if (unmatched === 0) {
    return lines === 1 ? "1 línea, ninguna pendiente" : `${lines} líneas, ninguna pendiente`;
  }
  return `${unmatched} de ${lines} ${unmatched === 1 ? "pendiente" : "pendientes"} de conciliar`;
}

/** C-31: "1 pago" / "3 pagos". */
export function paymentsCountText(count: number): string {
  return plural(count, "pago", "pagos");
}

export interface ReconcilingRow {
  sign: "" | "+" | "−" | "=";
  label: string;
  value: string | null;
  testId: string;
}

/**
 * C-29 (E-UX4-2): the reconciling statement read from the server's totals, in the server's own identity
 * GL = statement + GL items − line items + difference: from the statement's balance to the books'. Nothing is added here.
 */
export function reconcilingRows(r: {
  glBalance: string | null;
  statementBalance: string | null;
  difference: string | null;
  glItemsTotal: string;
  lineItemsTotal: string;
}): ReconcilingRow[] {
  return [
    { sign: "", label: "Saldo del extracto", value: r.statementBalance, testId: "recon-statement" },
    { sign: "+", label: "Movimientos en libros que el banco aún no refleja (en tránsito)", value: r.glItemsTotal, testId: "recon-gl-items" },
    { sign: "−", label: "Movimientos del banco aún no registrados en libros", value: r.lineItemsTotal, testId: "recon-line-items" },
    { sign: "+", label: "Diferencia sin explicar", value: r.difference, testId: "recon-difference" },
    { sign: "=", label: "Saldo en libros", value: r.glBalance, testId: "recon-gl" },
  ];
}
