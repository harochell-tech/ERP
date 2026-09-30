// E-UX2-8: the guided fiscal rule form. It builds exactly the JSON the Tax Engine accepts (src/Rochell.Tax/FiscalRuleDefinition.cs:
// the same keys, in the templates' order, rates as fraction strings) and reads a stored definition back into the form. The rate is
// typed in % and only its decimal point moves (E-UX2-1); no tax is computed here — expected amounts in the test cases are typed.
import { formatPercent, fractionToPercent, isDecimal, normalizeInput, percentToFraction, shiftDecimalPoint } from "./decimal";
import { GOODS_TYPES } from "./fiscalReports";
import type { FiscalRuleKind } from "./configuration";

/** The closed item category list of md.item (E-PR04-7), in catalogue order. */
export const ITEM_CATEGORIES: readonly { code: string; label: string }[] = [
  { code: "CEMENTO", label: "Cemento" },
  { code: "AGREGADO", label: "Agregado" },
  { code: "ADITIVO", label: "Aditivo" },
  { code: "OTRA_MATERIA_PRIMA", label: "Otra materia prima" },
  { code: "BLOQUE", label: "Bloque" },
  { code: "ADOQUIN", label: "Adoquín" },
  { code: "OTRO_PT", label: "Otro producto terminado" },
];

/** E-FIS2-01-2: the purchased categories the 606 classifies. */
export const RAW_MATERIAL_CATEGORIES = ["CEMENTO", "AGREGADO", "ADITIVO", "OTRA_MATERIA_PRIMA"] as const;

export function itemCategoryLabel(code: string): string {
  return ITEM_CATEGORIES.find((c) => c.code === code)?.label ?? code;
}

export const TAX_EFFECT_LABELS: Readonly<Record<string, string>> = {
  RECOVERABLE_INPUT: "Adelantado (se descuenta del ITBIS por pagar)",
  NON_RECOVERABLE_INPUT: "No adelantable (va al costo)",
  OUTPUT: "ITBIS facturado en ventas (por pagar)",
  WITHHOLDING: "Retención",
};

/** The effects each ITBIS kind accepts. */
export function effectsFor(kind: string): readonly string[] {
  if (kind === "PURCHASE_ITBIS") {
    return ["RECOVERABLE_INPUT", "NON_RECOVERABLE_INPUT"];
  }
  if (kind === "SALES_ITBIS") {
    return ["OUTPUT"];
  }
  return [];
}

export const PARTY_TYPE_LABELS: Readonly<Record<string, string>> = {
  COMPANY: "Persona jurídica (RNC)",
  INDIVIDUAL: "Persona física (cédula)",
  FOREIGN: "Extranjero (sin RNC ni cédula)",
};

/** The party types a withholding rule may name (FOREIGN only appears in test cases). */
export const WITHHOLDING_PARTY_TYPES = ["COMPANY", "INDIVIDUAL"] as const;

export const WITHHOLDING_BASE_LABELS: Readonly<Record<string, string>> = {
  ITBIS: "Sobre el ITBIS facturado (retención de ITBIS)",
  NET: "Sobre el monto neto (retención de ISR)",
};

/** E-FIS2-01-4: the 606's ISR withholding types; their official names come from the 606 instructivo (A-02). */
export const ISR_WITHHOLDING_TYPES = ["1", "2", "3", "4", "5", "6", "7", "8", "9"] as const;

export interface FiscalRuleForm {
  taxCode: string;
  /** The rate as typed, in % ("18"). */
  ratePercent: string;
  /** The rate as the definition read held it ("0.30"); sent back unchanged while the percentage still means it. */
  rateSource?: string;
  effect: string;
  exemptItemCategories: string[];
  base: string;
  partyTypes: string[];
  /** "" when the rule has none. */
  isrWithholdingType: string;
  classes: Record<string, string>;
}

const ITBIS_KEYS = ["tax_code", "rate", "effect", "exempt_item_categories"];
const WITHHOLDING_KEYS = ["tax_code", "rate", "base", "party_types", "isr_withholding_type"];

function blankForm(): FiscalRuleForm {
  return { taxCode: "", ratePercent: "", effect: "", exemptItemCategories: [], base: "", partyTypes: [], isrWithholdingType: "", classes: {} };
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((v) => typeof v === "string");
}

/**
 * Reads a definition into the form. `error` (Spanish) when the JSON is invalid or holds something the form cannot show (an unknown
 * key, a value of the wrong type); the advanced JSON editor still keeps the text.
 */
export function parseFiscalDefinition(kind: string, json: string): { form: FiscalRuleForm; error: null } | { form: null; error: string } {
  let root: unknown;
  try {
    root = JSON.parse(json);
  } catch (caught) {
    return { form: null, error: `JSON inválido: ${caught instanceof Error ? caught.message : String(caught)}` };
  }
  if (root === null || typeof root !== "object" || Array.isArray(root)) {
    return { form: null, error: "La definición debe ser un objeto JSON." };
  }
  const obj = root as Record<string, unknown>;
  const form = blankForm();
  if (kind === "REPORT_606_CLASSIFICATION") {
    const unknown = Object.keys(obj).filter((k) => k !== "classes");
    const classes = obj.classes;
    if (unknown.length > 0 || classes === null || typeof classes !== "object" || Array.isArray(classes)) {
      return { form: null, error: "El formulario solo muestra \"classes\": un objeto de categoría a código del 606." };
    }
    for (const [category, code] of Object.entries(classes as Record<string, unknown>)) {
      if (typeof code !== "string") {
        return { form: null, error: `El código de ${category} debe ser texto ("01"…"11").` };
      }
      form.classes[category] = code;
    }
    return { form, error: null };
  }
  const allowed = kind === "PURCHASE_WITHHOLDING" ? WITHHOLDING_KEYS : ITBIS_KEYS;
  const unknown = Object.keys(obj).filter((k) => !allowed.includes(k));
  if (unknown.length > 0) {
    return { form: null, error: `Clave desconocida para este tipo: ${unknown.join(", ")}.` };
  }
  for (const key of ["tax_code", "rate", "effect", "base", "isr_withholding_type"]) {
    if (obj[key] !== undefined && typeof obj[key] !== "string") {
      return { form: null, error: `"${key}" debe ser texto.` };
    }
  }
  for (const key of ["exempt_item_categories", "party_types"]) {
    if (obj[key] !== undefined && !isStringArray(obj[key])) {
      return { form: null, error: `"${key}" debe ser una lista de textos.` };
    }
  }
  form.taxCode = (obj.tax_code as string | undefined) ?? "";
  const rate = (obj.rate as string | undefined) ?? "";
  form.ratePercent = rate === "" ? "" : (fractionToPercent(rate) ?? rate);
  form.rateSource = rate;
  form.effect = (obj.effect as string | undefined) ?? "";
  form.exemptItemCategories = [...((obj.exempt_item_categories as string[] | undefined) ?? [])];
  form.base = (obj.base as string | undefined) ?? "";
  form.partyTypes = [...((obj.party_types as string[] | undefined) ?? [])];
  form.isrWithholdingType = (obj.isr_withholding_type as string | undefined) ?? "";
  return { form, error: null };
}

/** Catalogue order first, then anything else as it came (the server refuses unknown codes and says which). */
function inOrder(values: readonly string[], order: readonly string[]): string[] {
  const unique = [...new Set(values)];
  return [...order.filter((c) => unique.includes(c)), ...unique.filter((c) => !order.includes(c))];
}

/**
 * The definition the server receives, as the templates write it (2-space JSON, their key order). The rate goes as the fraction of
 * the typed percentage; a percentage that is not a decimal goes as typed so the server names the error.
 */
export function buildFiscalDefinition(kind: FiscalRuleKind | string, form: FiscalRuleForm): string {
  if (kind === "REPORT_606_CLASSIFICATION") {
    const classes: Record<string, string> = {};
    for (const category of inOrder(Object.keys(form.classes), RAW_MATERIAL_CATEGORIES)) {
      classes[category] = form.classes[category] ?? "";
    }
    return JSON.stringify({ classes }, null, 2);
  }
  const fraction = percentToFraction(form.ratePercent);
  // The stored text is kept while the percentage still means it ("0.30" stays "0.30", not "0.3").
  const kept = form.rateSource !== undefined && fraction !== null && shiftDecimalPoint(form.rateSource, 0) === fraction;
  const rate = kept ? form.rateSource : (fraction ?? form.ratePercent.trim());
  const taxCode = form.taxCode.trim();
  if (kind === "PURCHASE_WITHHOLDING") {
    const definition: Record<string, unknown> = {
      tax_code: taxCode,
      rate,
      base: form.base,
      party_types: inOrder(form.partyTypes, WITHHOLDING_PARTY_TYPES),
    };
    if (form.isrWithholdingType !== "") {
      definition.isr_withholding_type = form.isrWithholdingType;
    }
    return JSON.stringify(definition, null, 2);
  }
  return JSON.stringify(
    { tax_code: taxCode, rate, effect: form.effect, exempt_item_categories: inOrder(form.exemptItemCategories, ITEM_CATEGORIES.map((c) => c.code)) },
    null,
    2,
  );
}

export type FiscalFormField = "taxCode" | "ratePercent" | "effect" | "base" | "partyTypes" | "isrWithholdingType" | `class-${string}`;

/** The form's own checks, in Spanish (the server checks the same and more). */
export function validateFiscalForm(kind: string, form: FiscalRuleForm): Partial<Record<FiscalFormField, string>> {
  const errors: Partial<Record<FiscalFormField, string>> = {};
  if (kind === "REPORT_606_CLASSIFICATION") {
    for (const category of RAW_MATERIAL_CATEGORIES) {
      const code = form.classes[category] ?? "";
      if (!(code in GOODS_TYPES)) {
        errors[`class-${category}`] = `Elija el tipo de bienes y servicios de ${itemCategoryLabel(category).toLowerCase()}.`;
      }
    }
    return errors;
  }
  if (!/^[A-Z][A-Z0-9_]*$/.test(form.taxCode.trim())) {
    errors.taxCode = "Código en mayúsculas, dígitos y guion bajo (p. ej. ITBIS).";
  }
  const percent = form.ratePercent.replace(/%/g, "").replace(/\s/g, "");
  const fraction = percentToFraction(percent);
  if (!isDecimal(percent, 4) || percent.startsWith("-") || fraction === null || !/[1-9]/.test(fraction) || !isAtMostOne(fraction)) {
    errors.ratePercent = "Tasa en %: mayor que 0 y hasta 100, con hasta 4 decimales (p. ej. 18).";
  }
  if (kind === "PURCHASE_WITHHOLDING") {
    if (form.base !== "NET" && form.base !== "ITBIS") {
      errors.base = "Elija sobre qué se calcula la retención.";
    }
    if (form.partyTypes.length === 0) {
      errors.partyTypes = "Marque a quién se le retiene.";
    }
    if (form.isrWithholdingType !== "" && !(ISR_WITHHOLDING_TYPES as readonly string[]).includes(form.isrWithholdingType)) {
      errors.isrWithholdingType = "Tipo de retención del 606: 1 a 9.";
    }
  } else if (!effectsFor(kind).includes(form.effect)) {
    errors.effect = "Elija el efecto del impuesto.";
  }
  return errors;
}

/** "1", "0.18", "1.000" are at most one; "1.01", "2" are not (text only). */
function isAtMostOne(fraction: string): boolean {
  const [integer = "", decimals = ""] = fraction.split(".");
  const int = integer.replace(/^0+/, "");
  return int === "" || (int === "1" && /^0*$/.test(decimals));
}

/** E-UX2-8: a stored definition in words, one line per element; the JSON itself when it cannot be read. */
export function describeFiscalDefinition(kind: string, json: string): string[] {
  const parsed = parseFiscalDefinition(kind, json);
  if (parsed.form === null) {
    return [json];
  }
  const form = parsed.form;
  if (kind === "REPORT_606_CLASSIFICATION") {
    return Object.entries(form.classes).map(([category, code]) => `${itemCategoryLabel(category)}: ${code} ${GOODS_TYPES[code] ?? ""}`.trim());
  }
  const rate = percentToFraction(form.ratePercent);
  const lines = [`${form.taxCode} al ${rate === null ? form.ratePercent : formatPercent(rate)}`];
  if (kind === "PURCHASE_WITHHOLDING") {
    lines.push(WITHHOLDING_BASE_LABELS[form.base] ?? `Base ${form.base}`);
    lines.push(`Se retiene a: ${form.partyTypes.map((p) => PARTY_TYPE_LABELS[p] ?? p).join(", ") || "—"}`);
    if (form.isrWithholdingType) {
      lines.push(`Tipo de retención del 606: ${form.isrWithholdingType}`);
    }
  } else {
    lines.push(TAX_EFFECT_LABELS[form.effect] ?? form.effect);
    lines.push(form.exemptItemCategories.length === 0 ? "Sin categorías exentas" : `Exentas: ${form.exemptItemCategories.map(itemCategoryLabel).join(", ")}`);
  }
  return lines;
}

// ---- Regression cases (E-UX2-8): an editable table producing the same `cases` payload as the former JSON textarea.

export interface ExpectedTaxRow {
  taxCode: string;
  amount: string;
  effect: string;
}

export interface CaseRow {
  caseId: string;
  partyType: string;
  itemCategory: string;
  netAmount: string;
  itbisAmount: string;
  expected: ExpectedTaxRow[];
}

/**
 * The first case offered for a version: a company buying cement (a person, for a withholding on persons; a block, for sales
 * ITBIS), net 1000.00 with ITBIS 180.00 billed, and one expected tax of the rule's code and effect whose amount the analyst types
 * from the official source (nothing is computed here).
 */
export function initialCases(kind: string, definition: string): CaseRow[] {
  const form = parseFiscalDefinition(kind, definition).form;
  const withholding = kind === "PURCHASE_WITHHOLDING";
  return [
    {
      caseId: "caso-1",
      partyType: withholding ? (form?.partyTypes[0] ?? "COMPANY") : "COMPANY",
      itemCategory: kind === "SALES_ITBIS" ? "BLOQUE" : "CEMENTO",
      netAmount: "1000.00",
      itbisAmount: "180.00",
      expected: [{ taxCode: form?.taxCode ?? "", amount: "", effect: withholding ? "WITHHOLDING" : (form?.effect ?? "") }],
    },
  ];
}

/** Cases (e.g. the template's) as table rows. */
export function casesToRows(cases: readonly CaseRow[]): CaseRow[] {
  return cases.map((c) => ({ ...c, expected: c.expected.map((e) => ({ ...e })) }));
}

/** The rows as the run-tests command expects them: texts trimmed, amounts without grouping. */
export function rowsToCases(rows: readonly CaseRow[]): CaseRow[] {
  return rows.map((r) => ({
    caseId: r.caseId.trim(),
    partyType: r.partyType,
    itemCategory: r.itemCategory,
    netAmount: normalizeInput(r.netAmount),
    itbisAmount: normalizeInput(r.itbisAmount),
    expected: r.expected.map((e) => ({ taxCode: e.taxCode.trim(), amount: normalizeInput(e.amount), effect: e.effect })),
  }));
}

/** Per-cell messages keyed "case-<i>-<field>" or "case-<i>-expected-<j>-<field>"; empty when the table can be sent. */
export function validateCases(rows: readonly CaseRow[]): Record<string, string> {
  const errors: Record<string, string> = {};
  const ids = new Set<string>();
  rows.forEach((r, i) => {
    const id = r.caseId.trim();
    if (id === "") {
      errors[`case-${i}-caseId`] = "Nombre del caso.";
    } else if (ids.has(id)) {
      errors[`case-${i}-caseId`] = "Nombre repetido.";
    }
    ids.add(id);
    if (!r.partyType) {
      errors[`case-${i}-partyType`] = "Elija el tipo de contraparte.";
    }
    if (!r.itemCategory) {
      errors[`case-${i}-itemCategory`] = "Elija la categoría.";
    }
    if (!isDecimal(normalizeInput(r.netAmount), 4) || r.netAmount.trim().startsWith("-")) {
      errors[`case-${i}-netAmount`] = "Monto con hasta 4 decimales.";
    }
    if (!isDecimal(normalizeInput(r.itbisAmount), 4) || r.itbisAmount.trim().startsWith("-")) {
      errors[`case-${i}-itbisAmount`] = "Monto con hasta 4 decimales.";
    }
    r.expected.forEach((e, j) => {
      if (e.taxCode.trim() === "") {
        errors[`case-${i}-expected-${j}-taxCode`] = "Código del impuesto.";
      }
      if (!isDecimal(normalizeInput(e.amount), 4) || e.amount.trim().startsWith("-")) {
        errors[`case-${i}-expected-${j}-amount`] = "Monto con hasta 4 decimales.";
      }
      if (!e.effect) {
        errors[`case-${i}-expected-${j}-effect`] = "Elija el efecto.";
      }
    });
  });
  return errors;
}
