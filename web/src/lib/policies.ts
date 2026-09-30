// E-UX2-1…4: pure helpers of the accounting policy screen, unit-tested. Values stay the strings the server keeps; a percentage is
// only the fraction with its decimal point moved two places (E-UX2-1), and comparisons are of text, never of JavaScript numbers.
import { formatDecimal, formatPercent, fractionToPercent, isDecimal, normalizeInput, percentToFraction } from "./decimal";
import { compareDecimals } from "./quotes";

export interface PolicyParameterDefinition {
  paramCode: string;
  valueType: string;
  minValue: string | null;
  maxValue: string | null;
  allowedValues: readonly string[] | null;
  description: string;
  label?: string | null;
  unit?: string | null;
  example?: string | null;
  affects?: string | null;
}

export interface PolicyVersion {
  status: string;
  version: number;
  effectiveFrom: string;
  effectiveTo: string | null;
  parameters: Record<string, string>;
}

export type PolicyUnit = "PERCENT" | "AMOUNT" | "DAYS" | "HOURS" | "OPTION" | "NUMBER";

/** The unit of a parameter (E-UX2-2); a parameter a later migration added without texts falls back on its value type. */
export function policyUnit(definition: PolicyParameterDefinition): PolicyUnit {
  switch (definition.unit) {
    case "PERCENT":
    case "AMOUNT":
    case "DAYS":
    case "HOURS":
    case "OPTION":
      return definition.unit;
  }
  switch (definition.valueType) {
    case "DECIMAL_PERCENT":
      return "PERCENT";
    case "AMOUNT":
      return "AMOUNT";
    case "ENUM":
    case "BOOLEAN":
      return "OPTION";
    default:
      return "NUMBER";
  }
}

/** The unit as the label shows it: "Tolerancia de redondeo (RD$)". */
export const UNIT_SUFFIX: Readonly<Record<PolicyUnit, string>> = {
  PERCENT: "%",
  AMOUNT: "RD$",
  DAYS: "días",
  HOURS: "horas",
  OPTION: "",
  NUMBER: "",
};

/** The Spanish name of a parameter (its label; the older description when the label is missing). */
export function parameterLabel(definition: PolicyParameterDefinition): string {
  return definition.label?.trim() || definition.description || definition.paramCode;
}

/** The label with its unit: "Sobre-recepción permitida (%)". */
export function parameterLabelWithUnit(definition: PolicyParameterDefinition): string {
  const suffix = UNIT_SUFFIX[policyUnit(definition)];
  return suffix ? `${parameterLabel(definition)} (${suffix})` : parameterLabel(definition);
}

/** E-UX2-1: only DECIMAL_PERCENT parameters are typed and shown in % (the server keeps the fraction). */
function isPercent(definition: PolicyParameterDefinition): boolean {
  return definition.valueType === "DECIMAL_PERCENT";
}

/** The stored value as the input shows it: "0.05" → "5" for a percentage, the value itself otherwise. */
export function toInputValue(definition: PolicyParameterDefinition, stored: string | null | undefined): string {
  if (stored === null || stored === undefined || stored === "") {
    return "";
  }
  return isPercent(definition) ? (fractionToPercent(stored) ?? stored) : stored;
}

/** The typed value as the server expects it: "5" → "0.05" for a percentage; grouping removed from amounts. */
export function fromInputValue(definition: PolicyParameterDefinition, typed: string): string {
  const text = typed.trim();
  if (isPercent(definition)) {
    return percentToFraction(text) ?? text;
  }
  return policyUnit(definition) === "AMOUNT" ? normalizeInput(text) : text;
}

/** A stored value for display: "5 %", "RD$ 1.00", "48 horas", "30 días"; "—" when missing. */
export function formatParameterValue(definition: PolicyParameterDefinition | undefined, stored: string | null | undefined): string {
  if (stored === null || stored === undefined || stored === "") {
    return "—";
  }
  if (!definition) {
    return stored;
  }
  if (isPercent(definition)) {
    return formatPercent(stored);
  }
  switch (policyUnit(definition)) {
    case "AMOUNT":
      return isDecimal(stored, 6) ? `RD$ ${formatDecimal(stored)}` : stored;
    case "DAYS":
      return `${stored} ${stored === "1" ? "día" : "días"}`;
    case "HOURS":
      return `${stored} ${stored === "1" ? "hora" : "horas"}`;
    default:
      return stored;
  }
}

/**
 * E-UX2-2: the prepare form checks each value by its unit before sending (the server checks again): a percentage with at most 4
 * decimals within the parameter's bounds, an amount with at most 4 decimals, whole days and hours, an allowed option.
 */
export function validateParameter(definition: PolicyParameterDefinition, typed: string): string | null {
  const label = parameterLabel(definition);
  const text = typed.trim();
  const example = definition.example ? ` (p. ej. ${definition.example})` : "";
  if (text === "") {
    return policyUnit(definition) === "OPTION" ? `Elija ${label.toLowerCase()}.` : `Indique ${label.toLowerCase()}${example}.`;
  }
  if (definition.valueType === "ENUM") {
    return definition.allowedValues?.includes(text) ? null : `Elija una de las opciones de ${label.toLowerCase()}.`;
  }
  if (definition.valueType === "BOOLEAN") {
    return text === "true" || text === "false" ? null : `Elija sí o no.`;
  }
  const unit = policyUnit(definition);
  let stored = text;
  if (isPercent(definition)) {
    const plain = text.replace(/%/g, "").replace(/\s/g, "");
    if (!isDecimal(plain, 4) || plain.startsWith("-")) {
      return `Escriba un porcentaje con punto decimal y hasta 4 decimales${example}.`;
    }
    stored = percentToFraction(plain) ?? plain;
  } else if (unit === "AMOUNT") {
    stored = normalizeInput(text);
    if (!isDecimal(stored, 4) || stored.startsWith("-")) {
      return `Escriba un monto en RD$ con punto decimal y hasta 4 decimales${example}.`;
    }
  } else if (definition.valueType === "INTEGER" || unit === "DAYS" || unit === "HOURS") {
    if (!/^\d+$/.test(text)) {
      return `Escriba un número entero de ${unit === "HOURS" ? "horas" : unit === "DAYS" ? "días" : "unidades"}${example}.`;
    }
  } else if (!isDecimal(text, 6) || text.startsWith("-")) {
    return `Escriba un número con punto decimal${example}.`;
  }
  const min = definition.minValue;
  const max = definition.maxValue;
  const below = min !== null && compareDecimals(stored, min) === -1;
  const above = max !== null && compareDecimals(stored, max) === 1;
  if (below || above) {
    return `Debe estar entre ${formatParameterValue(definition, min ?? "0")} y ${formatParameterValue(definition, max ?? "")}.`;
  }
  return null;
}

/** E-UX2-4: the ACTIVE version covering `today` (yyyy-MM-dd; the end date is exclusive, as the server reads it). */
export function policyInForce<V extends PolicyVersion>(versions: readonly V[], today: string): V | undefined {
  return versions.find((v) => v.status === "ACTIVE" && v.effectiveFrom <= today && (v.effectiveTo === null || v.effectiveTo > today));
}

/** E-UX2-4: the defined parameters the version in force does not carry (a later migration added them). */
export function missingParameters<D extends PolicyParameterDefinition>(definitions: readonly D[], inForce: PolicyVersion | undefined): D[] {
  if (!inForce) {
    return [];
  }
  return definitions.filter((d) => !Object.prototype.hasOwnProperty.call(inForce.parameters, d.paramCode));
}

export interface PolicyDiffRow {
  paramCode: string;
  label: string;
  current: string | null;
  proposed: string | null;
  changed: boolean;
  definition: PolicyParameterDefinition | undefined;
}

/**
 * E-UX2-3: "en vigor → propuesta" per parameter, in definition order (then any parameter the definitions no longer list). A
 * parameter is changed when the two texts differ (string comparison only).
 */
export function policyDiff(
  definitions: readonly PolicyParameterDefinition[],
  current: Record<string, string> | undefined,
  proposed: Record<string, string>,
): PolicyDiffRow[] {
  const codes = [...definitions.map((d) => d.paramCode)];
  for (const code of [...Object.keys(current ?? {}), ...Object.keys(proposed)]) {
    if (!codes.includes(code)) {
      codes.push(code);
    }
  }
  return codes.map((code) => {
    const definition = definitions.find((d) => d.paramCode === code);
    const before = current && Object.prototype.hasOwnProperty.call(current, code) ? (current[code] ?? null) : null;
    const after = Object.prototype.hasOwnProperty.call(proposed, code) ? (proposed[code] ?? null) : null;
    return { paramCode: code, label: definition ? parameterLabel(definition) : code, current: before, proposed: after, changed: before !== after, definition };
  });
}
