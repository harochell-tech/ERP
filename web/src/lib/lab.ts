// LAB1-01: helpers of the Calidad screens (pure; unit-tested in tests/unit/lab.test.ts). Decimals stay strings (E-PR18b-2).
import type { Schemas } from "@/api/client";
import { normalizeInput } from "./decimal";

export const BLOCK_CONDITION: Record<string, string> = { SECO_AL_AIRE: "Seco al aire", HUMEDO: "Húmedo", SATURADO: "Saturado" };

/** E-LAB1-01-4: why a lot has no field code yet. */
export const FIELD_CODE_WAITS: Record<string, string> = {
  ITEM_PREFIX: "Sin código de campo: al ítem le faltan sus requisitos (prefijo de lote)",
  MACHINE_SHORT_CODE: "Sin código de campo: a la máquina le falta su código corto",
  DUPLICATE: "Sin código de campo: otro lote ya usa ese código",
};

export type SpecimenDraft = { widthCm: string; heightCm: string; lengthCm: string; weightKg: string; loadKg: string; blockCondition: string; failureType: string; notes: string };

function optional(value: string): string | null {
  const normalized = normalizeInput(value);
  return normalized === "" ? null : normalized;
}

/** What the form's row sends: empty measures are null (the server uses the item's nominal ones). */
export function specimenBody(row: SpecimenDraft): Schemas["CompressionSpecimen"] {
  return {
    loadKg: normalizeInput(row.loadKg),
    widthCm: optional(row.widthCm),
    heightCm: optional(row.heightCm),
    lengthCm: optional(row.lengthCm),
    weightKg: optional(row.weightKg),
    blockCondition: row.blockCondition || null,
    failureType: row.failureType || null,
    notes: row.notes.trim() || null,
  };
}

/** Today in the Dominican Republic, yyyy-MM-dd (the business date the server uses). */
export function todayIso(now: Date = new Date()): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(now);
}

/** A parameter's value as the screen shows it: whole numbers without decimals, the rest without trailing zeros. */
export function parameterText(parameter: Pick<Schemas["LabParameterView"], "kind" | "number" | "text">): string {
  if (parameter.kind !== "NUMBER") {
    return parameter.text ?? "";
  }
  const value = parameter.number ?? "";
  return value.includes(".") ? value.replace(/0+$/, "").replace(/\.$/, "") : value;
}

// LAB1-02 (E-LAB1-02-1…10): the lot's verdict and alerts in words.
const VERDICT: Record<string, { label: string; tone: string }> = {
  COMPLIES: { label: "Cumple", tone: "tone-done" },
  FAILS: { label: "No cumple", tone: "tone-error" },
  NO_SPEC: { label: "Sin requisito", tone: "tone-neutral" },
  NO_DATA: { label: "Sin dato", tone: "tone-neutral" },
};

/** «Cumple (estimado)», «No cumple», «Sin requisito»…; a lot never evaluated is «Sin ensayos». */
export function verdictBadge(verdict: string | null | undefined, basis: string | null | undefined): { label: string; tone: string } {
  if (!verdict) {
    return { label: "Sin ensayos", tone: "tone-neutral" };
  }
  const known = VERDICT[verdict] ?? { label: verdict, tone: "tone-neutral" };
  const estimated = basis === "ESTIMATED" && (verdict === "COMPLIES" || verdict === "FAILS");
  return estimated ? { label: `${known.label} (estimado)`, tone: verdict === "FAILS" ? "tone-error" : "tone-attention" } : known;
}

export const LOT_ALERT: Record<string, string> = {
  NO_TESTS: "Sin probetas",
  FEW_SPECIMENS: "Pocas probetas",
  HIGH_CV: "CV alto",
  HIGH_ABSORPTION: "Absorción alta",
};

export function alertsText(alerts: readonly string[]): string {
  return alerts.map((a) => LOT_ALERT[a] ?? a).join(" · ");
}

/** What Calidad may do with a lot on Calidad › Lotes (E-LAB1-4, E-LAB1-02-10). */
export function qualityActions(lot: { status: string; readyForFinalRelease: boolean }, can: (permission: string) => boolean): ("finalRelease" | "block" | "unblock" | "reevaluate")[] {
  const actions: ("finalRelease" | "block" | "unblock" | "reevaluate")[] = [];
  if (lot.readyForFinalRelease && can("fg_lot:final_release")) {
    actions.push("finalRelease");
  }
  if ((lot.status === "CURING" || lot.status === "RELEASED" || lot.status === "FINAL_RELEASED") && can("fg_lot:release")) {
    actions.push("block");
  }
  if (lot.status === "BLOCKED" && can("fg_lot:release")) {
    actions.push("unblock");
  }
  if (can("lab_spec:manage")) {
    actions.push("reevaluate");
  }
  return actions;
}

/** LAB1-03 (E-LAB1-03-10): a rack label's QR — `/calidad/lotes/?lote=<id>&rack=<n>&codigo=<field code>` — or null when the text is not one. */
export function parseRackQr(text: string): { lotId: string; rackNo: number | null; code: string | null } | null {
  let url: URL;
  try {
    url = new URL(text.trim(), "https://rochell.invalid");
  } catch {
    return null;
  }
  const lotId = url.searchParams.get("lote") ?? "";
  if (!url.pathname.endsWith("/calidad/lotes/") || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(lotId)) {
    return null;
  }
  const rack = Number.parseInt(url.searchParams.get("rack") ?? "", 10);
  return { lotId: lotId.toLowerCase(), rackNo: Number.isInteger(rack) && rack >= 1 ? rack : null, code: url.searchParams.get("codigo") };
}

/** E-LAB1-03-2/4: the break dates a certificate can be issued for — dates with at least one valid specimen, newest first. */
export function certifiableDates(tests: Pick<Schemas["CompressionTestView"], "breakDate" | "status">[]): string[] {
  return [...new Set(tests.filter((t) => t.status === "RECORDED").map((t) => t.breakDate))].sort().reverse();
}

/** E-LAB1-03-5: why a certificate is void, in words. */
export function certificateVoidText(cause: string | null | undefined): string {
  return cause === "SPECIMEN_VOIDED" ? "anulado porque se anuló una de sus probetas" : cause === "MANUAL" ? "anulado por Calidad" : "";
}
