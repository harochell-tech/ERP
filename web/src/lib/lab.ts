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
