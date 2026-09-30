// FIS2-03 (E-FIS2-03-1…8): pure helpers of the fiscal report screens (606, IT-1 and IR-17 summaries). No money arithmetic:
// every amount, count and total is the server's; only the period (a month string) is derived from today's date.

import { todayInDominicanRepublic } from "@/lib/labels";

/** The month before today's in the Dominican Republic, as AAAAMM (the period the 606 is usually filed for). */
export function previousPeriod(now: Date = new Date()): string {
  const [year, month] = todayInDominicanRepublic(now).split("-").map(Number) as [number, number];
  return month === 1 ? `${year - 1}12` : `${year}${String(month - 1).padStart(2, "0")}`;
}

/** Today's month in the Dominican Republic, as AAAAMM. */
export function currentPeriod(now: Date = new Date()): string {
  return todayInDominicanRepublic(now).slice(0, 7).replace("-", "");
}

/** AAAAMM with a month 01…12. */
export function isValidPeriod(period: string): boolean {
  const match = /^(\d{4})(\d{2})$/.exec(period);
  return match !== null && Number(match[2]) >= 1 && Number(match[2]) <= 12;
}

/** AAAAMM → yyyy-MM (the value of an `<input type="month">`) and back. */
export function periodToMonthInput(period: string): string {
  return isValidPeriod(period) ? `${period.slice(0, 4)}-${period.slice(4)}` : "";
}

export function monthInputToPeriod(value: string): string {
  return value.replace("-", "");
}

/** The 606 row warnings (E-FIS2-02). */
export const REPORT_606_WARNINGS: Readonly<Record<string, string>> = {
  CLASSIFICATION_MISSING: "Falta la clasificación del 606 para la categoría del artículo (tipo de bienes y servicios en blanco)",
  ISR_WITHHOLDING_TYPE_MISSING: "La retención de ISR no tiene tipo de retención (campo en blanco)",
};

export function warningLabel(code: string): string {
  return REPORT_606_WARNINGS[code] ?? code;
}

/** The 11 types of goods and services of the 606 instructivo (the REPORT_606_CLASSIFICATION codes). */
export const GOODS_TYPES: Readonly<Record<string, string>> = {
  "01": "Gastos de personal",
  "02": "Gastos por trabajos, suministros y servicios",
  "03": "Arrendamientos",
  "04": "Gastos de activos fijos",
  "05": "Gastos de representación",
  "06": "Otras deducciones admitidas",
  "07": "Gastos financieros",
  "08": "Gastos extraordinarios",
  "09": "Compras y gastos que formarán parte del costo de venta",
  "10": "Adquisiciones de activos",
  "11": "Gastos de seguros",
};

/** The 606's payment methods (field 23). */
export const PAYMENT_METHODS: Readonly<Record<number, string>> = {
  1: "Efectivo",
  2: "Cheques / transferencias / depósito",
  3: "Tarjeta de crédito / débito",
  4: "Compra a crédito",
  5: "Permuta",
  6: "Notas de crédito",
  7: "Mixto",
};

/** The 606's identification types (field 2). */
export const ID_TYPES: Readonly<Record<number, string>> = {
  1: "RNC",
  2: "Cédula",
};

/** A code with its Spanish name, or "—" when blank. */
export function codeLabel<K extends string | number>(labels: Readonly<Record<K, string>>, code: K | null | undefined): string {
  if (code === null || code === undefined || code === "") {
    return "—";
  }
  const label = labels[code];
  return label ? `${code} — ${label}` : String(code);
}

/** Withholding taxes (IT-1 customer withholdings, IR-17 lines). */
export const WITHHOLDING_TAXES: Readonly<Record<string, string>> = {
  ITBIS: "ITBIS retenido",
  ISR: "ISR retenido",
};

export type FiscalReportTab = "606" | "IT1" | "IR17";

export const FISCAL_REPORT_TABS: readonly { id: FiscalReportTab; label: string }[] = [
  { id: "606", label: "606" },
  { id: "IT1", label: "IT-1" },
  { id: "IR17", label: "IR-17" },
];
