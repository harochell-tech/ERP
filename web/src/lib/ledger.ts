// FIN1-04: shared pieces of the accounting screens (E-FIN1-04-1…11).
import { buildUrl, type QueryParams } from "@/api/client";

export const ACCOUNT_CLASSES: Readonly<Record<string, string>> = {
  ASSET: "Activo",
  LIABILITY: "Pasivo",
  EQUITY: "Patrimonio",
  REVENUE: "Ingreso",
  COST: "Costo",
  EXPENSE: "Gasto",
};

export function accountClassLabel(value: string | null | undefined): string {
  return value ? (ACCOUNT_CLASSES[value] ?? value) : "Sin clase";
}

export const REPORTS: Readonly<Record<string, string>> = {
  BALANCE_SHEET: "Balance general",
  INCOME_STATEMENT: "Estado de resultados",
};

/** E-FIN1-03-10: the same report as a CSV file (the browser downloads it with the session cookie). */
export function csvUrl(template: string, params: QueryParams): string {
  return buildUrl(template, { ...params, query: { ...params.query, format: "csv" } });
}

/** First day of the month of a yyyy-MM-dd date. */
export function monthStart(isoDate: string): string {
  return `${isoDate.slice(0, 7)}-01`;
}

/** E-FIN1-04-5: SHA-256 of the support document, computed in the browser; the file itself is not uploaded. */
export async function sha256Hex(file: Blob): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", await file.arrayBuffer());
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}
