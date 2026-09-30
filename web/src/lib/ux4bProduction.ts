// UX4-03: pure helpers of the production screens (P-03…P-41, E-UX4-14). No arithmetic on quantities or money: every figure is
// the server's decimal string; comparisons are on the text. Unit-tested in tests/unit/ux4b-production.test.ts.
import { formatQuantity, fractionToPercent } from "./decimal";
import type { StatusTone } from "./labels";

/** P-06: a unit code as read ("l" → "L", the litre's symbol); other codes as they come. */
export function uomLabel(uom: string | null | undefined): string {
  if (!uom) {
    return "";
  }
  return uom === "l" ? "L" : uom;
}

/** P-05: a quantity with its unit ("1,480 un", "15 L"); "—" when there is no quantity. */
export function quantityWithUnit(value: string | null | undefined, uom: string | null | undefined): string {
  const text = formatQuantity(value);
  if (text === "—") {
    return text;
  }
  const unit = uomLabel(uom);
  return unit ? `${text} ${unit}` : text;
}

/** P-37: "CODE — Description (unit)", without repeating the code when the description is the code ("ADITIVO-P (L)"). */
export function itemDisplay(code: string, description?: string | null, uom?: string | null): string {
  const name = description?.trim() ?? "";
  const base = name && name.toUpperCase() !== code.toUpperCase() ? `${code} — ${name}` : code;
  return uom ? `${base} (${uomLabel(uom)})` : base;
}

/** P-16: a signed figure: "+0.35", "-0.2", "0" (the server's sign; a positive value gets "+"). */
export function signedQuantity(value: string | null | undefined): string {
  const text = formatQuantity(value);
  if (text === "—" || text.startsWith("-") || !/[1-9]/.test(text)) {
    return text;
  }
  return `+${text}`;
}

/** P-16: the server's difference percentage ("3.50") as "+3.5 %"; null stays "—" (theoretical 0). */
export function signedPercent(value: string | null | undefined): string {
  if (value === null || value === undefined || value === "") {
    return "—";
  }
  return `${signedQuantity(value)} %`;
}

/** P-16: the PRODUCTION policy's usage tolerance (a fraction, "0.05") as "±5 %"; null when no policy covers the date. */
export function toleranceText(usageTolerancePct: string | null | undefined): string | null {
  const percent = usageTolerancePct ? fractionToPercent(usageTolerancePct) : null;
  return percent === null ? null : `±${percent} %`;
}

export type VarianceMark = { label: string; tone: StatusTone };

/** P-16: the tolerance mark of a variance, from the server's `outOfTolerance` (null: no policy, nothing to mark). */
export function varianceMark(outOfTolerance: boolean | null | undefined): VarianceMark | null {
  if (outOfTolerance === null || outOfTolerance === undefined) {
    return null;
  }
  return outOfTolerance ? { label: "Fuera de tolerancia", tone: "attention" } : { label: "Dentro de tolerancia", tone: "done" };
}

/** P-12 / E-UX4-14: the production statuses in words, with their tone (a lot released is done; a recipe is feminine). */
const LOT: Readonly<Record<string, [string, StatusTone]>> = {
  CURING: ["En curado", "progress"],
  RELEASED: ["Liberado", "done"],
  BLOCKED: ["Bloqueado", "attention"],
  SCRAPPED: ["Desechado", "error"],
  VOIDED: ["Anulado", "neutral"],
};
const RECIPE: Readonly<Record<string, [string, StatusTone]>> = {
  DRAFT: ["Borrador", "neutral"],
  ACTIVE: ["Activa", "done"],
  SUPERSEDED: ["Reemplazada", "neutral"],
};
const SUMMARY: Readonly<Record<string, [string, StatusTone]>> = {
  DRAFT: ["Borrador", "neutral"],
  POSTED: ["Cerrado", "done"],
  REVERSED: ["Revertido", "reversed"],
};
const RUN: Readonly<Record<string, [string, StatusTone]>> = {
  IN_PROGRESS: ["En proceso", "progress"],
  COMPLETED: ["Completada", "done"],
  CANCELLED: ["Cancelada", "neutral"],
};
const COLLECTOR: Readonly<Record<string, [string, StatusTone]>> = {
  OPEN: ["Abierto", "progress"],
  SETTLED: ["Liquidado", "done"],
};

export type ProductionStatusKind = "lot" | "recipe" | "summary" | "run" | "collector";
const KINDS: Readonly<Record<ProductionStatusKind, Readonly<Record<string, [string, StatusTone]>>>> = {
  lot: LOT,
  recipe: RECIPE,
  summary: SUMMARY,
  run: RUN,
  collector: COLLECTOR,
};

/** The label and tone of a production status; null when the kind does not know it (the caller falls back to the shared labels). */
export function productionStatus(kind: ProductionStatusKind, status: string | null | undefined): { label: string; tone: StatusTone } | null {
  const found = status ? KINDS[kind][status] : undefined;
  return found ? { label: found[0], tone: found[1] } : null;
}

/** P-26: the lot's remaining curing in words, from the server's whole hours. */
export function curingRemainingText(status: string, curingHoursRemaining: number | null | undefined): string | null {
  if (status !== "CURING" || curingHoursRemaining === null || curingHoursRemaining === undefined) {
    return null;
  }
  if (curingHoursRemaining <= 0) {
    return "Curado cumplido: se puede liberar";
  }
  return curingHoursRemaining === 1 ? "Falta 1 hora de curado" : `Faltan ${curingHoursRemaining} horas de curado`;
}

export type RunStep = { label: string; href: string; primary: boolean } | null;

/**
 * P-14: the next step of a run of the production day, by its state and what the user may do: record the shift summary, close it,
 * release the lot, or just open it.
 */
export function runNextStep(
  run: { runId: string; status: string; summaryStatus: string | null; lotCode: string | null; lotStatus: string | null },
  can: (permission: string) => boolean,
): RunStep {
  const href = `/produccion/corrida/?id=${run.runId}`;
  if (run.status === "IN_PROGRESS" && run.summaryStatus === null) {
    return can("shift_summary:record") ? { label: "Registrar resumen", href, primary: true } : { label: "Ver corrida", href, primary: false };
  }
  if (run.status === "IN_PROGRESS" && run.summaryStatus === "DRAFT") {
    if (can("shift_summary:post")) {
      return { label: "Cerrar resumen del turno", href, primary: true };
    }
    return can("shift_summary:record") ? { label: "Revisar resumen", href, primary: false } : { label: "Ver corrida", href, primary: false };
  }
  if (run.lotCode && run.lotStatus === "CURING" && can("fg_lot:release")) {
    return { label: "Ver lote en curado", href: lotHref(run.lotCode), primary: false };
  }
  return { label: "Ver corrida", href, primary: false };
}

/** P-17: the lots screen opened on one lot. */
export function lotHref(lotCode: string): string {
  return `/produccion/lotes/?lote=${encodeURIComponent(lotCode)}`;
}

/** P-32: the production day of a date (the run's day). */
export function dayHref(businessDate: string): string {
  return `/produccion/dia/?dia=${encodeURIComponent(businessDate)}`;
}

/** P-13: a date from the address ("?dia=2026-09-29"), or null when it is not a date. */
export function dayFromQuery(value: string | null | undefined): string | null {
  return value && /^\d{4}-\d{2}-\d{2}$/.test(value) ? value : null;
}

/** P-40: the month before today's ("2026-09-30" → "2026-08"; January → December of the year before). */
export function previousMonth(today: string): string {
  const year = Number.parseInt(today.slice(0, 4), 10);
  const month = Number.parseInt(today.slice(5, 7), 10);
  return month === 1 ? `${year - 1}-12` : `${year}-${String(month - 1).padStart(2, "0")}`;
}

/** P-41: a cost collector can be settled only once its month has ended (text comparison of "yyyy-MM"). */
export function monthEnded(periodMonth: string, today: string): boolean {
  return periodMonth.slice(0, 7) < today.slice(0, 7);
}

const MONTHS = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

/** "2026-08-01" → "agosto 2026". */
export function monthName(periodMonth: string): string {
  const month = Number.parseInt(periodMonth.slice(5, 7), 10);
  const name = MONTHS[month - 1];
  return name ? `${name} ${periodMonth.slice(0, 4)}` : periodMonth;
}

/** Text form of a decimal for comparison ("1.500000" and "1.5" are the same figure). */
function sameFigure(a: string | null | undefined, b: string | null | undefined): boolean {
  return formatQuantity(a) === formatQuantity(b);
}

export type RecipeLineComparison = {
  materialItemId: string;
  materialCode: string;
  baseUom: string;
  previous: string | null;
  current: string | null;
  changed: boolean;
};

/** P-36: a recipe's materials against the previous version's (text comparison only): added, removed and changed quantities. */
export function compareRecipeLines(
  current: readonly { materialItemId: string; materialCode: string; baseUom: string; qtyPerBatch: string }[],
  previous: readonly { materialItemId: string; materialCode: string; baseUom: string; qtyPerBatch: string }[],
): RecipeLineComparison[] {
  const rows: RecipeLineComparison[] = current.map((line) => {
    const before = previous.find((p) => p.materialItemId === line.materialItemId);
    return {
      materialItemId: line.materialItemId,
      materialCode: line.materialCode,
      baseUom: line.baseUom,
      previous: before?.qtyPerBatch ?? null,
      current: line.qtyPerBatch,
      changed: !before || !sameFigure(before.qtyPerBatch, line.qtyPerBatch),
    };
  });
  for (const line of previous) {
    if (!current.some((c) => c.materialItemId === line.materialItemId)) {
      rows.push({ materialItemId: line.materialItemId, materialCode: line.materialCode, baseUom: line.baseUom, previous: line.qtyPerBatch, current: null, changed: true });
    }
  }
  return rows;
}

export type RecipeParameterComparison = { label: string; previous: string; current: string; changed: boolean };

type RecipeParameters = { unitsPerBatch: string; unitsPerCycle: string; unitsPerRack: string; minCuringHours: number; maxCuringHours: number };

/** P-36: the recipe's parameters against the previous version's. */
export function compareRecipeParameters(current: RecipeParameters, previous: RecipeParameters): RecipeParameterComparison[] {
  const figure = (label: string, a: string, b: string) => ({ label, previous: formatQuantity(b), current: formatQuantity(a), changed: !sameFigure(a, b) });
  const hours = (label: string, a: number, b: number) => ({ label, previous: `${b} h`, current: `${a} h`, changed: a !== b });
  return [
    figure("Unidades por tanda", current.unitsPerBatch, previous.unitsPerBatch),
    figure("Unidades por ciclo", current.unitsPerCycle, previous.unitsPerCycle),
    figure("Unidades por rack", current.unitsPerRack, previous.unitsPerRack),
    hours("Curado mínimo", current.minCuringHours, previous.minCuringHours),
    hours("Curado máximo", current.maxCuringHours, previous.maxCuringHours),
  ];
}

/** The previous version of a recipe (same product and machine, the highest lower version), or undefined for the first. */
export function previousRecipeVersion<T extends { itemId: string; machineId: string; version: number }>(recipes: readonly T[], of: { itemId: string; machineId: string; version: number }): T | undefined {
  return recipes
    .filter((r) => r.itemId === of.itemId && r.machineId === of.machineId && r.version < of.version)
    .sort((a, b) => b.version - a.version)[0];
}

/** E-UX4-9 / P-34: the minimum curing in whole hours, at least 1; the maximum above it. Messages in Spanish, null when valid. */
export function curingHoursErrors(min: number | null, max: number | null): { min: string | null; max: string | null } {
  return {
    min: min === null ? "Horas en número entero." : min < 1 ? "El curado mínimo es de al menos 1 hora." : null,
    max: max === null ? "Horas en número entero." : min !== null && max <= min ? "El máximo debe ser mayor que el mínimo." : null,
  };
}
