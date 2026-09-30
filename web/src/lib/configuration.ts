// E-B03-15: helpers of the configuration screens, kept pure so their behaviour is unit-tested. No amount arithmetic here:
// decimals travel as the strings the user typed (E-PR18b-11); the server validates every value.

/** Hex SHA-256 of a file's bytes, computed in the browser; the file itself is not uploaded (E-B03-15-3). */
export async function sha256Hex(data: ArrayBuffer): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export const FISCAL_RULE_KINDS = ["PURCHASE_ITBIS", "PURCHASE_WITHHOLDING", "SALES_ITBIS", "REPORT_606_CLASSIFICATION"] as const;
export type FiscalRuleKind = (typeof FISCAL_RULE_KINDS)[number];

export const FISCAL_KIND_LABELS: Readonly<Record<string, string>> = {
  PURCHASE_ITBIS: "ITBIS de compras",
  PURCHASE_WITHHOLDING: "Retención en compras",
  SALES_ITBIS: "ITBIS de ventas",
  REPORT_606_CLASSIFICATION: "Clasificación del 606", // FIS2-03 (E-FIS2-03-5)
};

/** FIS2-01: the 606 classification is READY with its official source alone; the server refuses test runs for it. */
export function ruleKindRunsTests(kind: string): boolean {
  return kind !== "REPORT_606_CLASSIFICATION";
}

/** FIS2-03 (E-FIS2-03-5): the help shown under the definition of each kind; none for the tax kinds. */
export const DEFINITION_HELP: Readonly<Partial<Record<FiscalRuleKind, readonly string[]>>> = {
  PURCHASE_WITHHOLDING: [
    "Con base \"NET\" la retención es de ISR: agregue \"isr_withholding_type\" con el tipo de retención del 606 (\"1\" a \"9\"). Sin él, el 606 deja el tipo en blanco y TAX-606 lo advierte.",
  ],
  REPORT_606_CLASSIFICATION: [
    "Asigne a cada categoría de materia prima (CEMENTO, AGREGADO, ADITIVO, OTRA_MATERIA_PRIMA) el tipo de bienes y servicios del instructivo del 606:",
    "01 Gastos de personal · 02 Gastos por trabajos, suministros y servicios · 03 Arrendamientos · 04 Gastos de activos fijos · 05 Gastos de representación · 06 Otras deducciones admitidas · 07 Gastos financieros · 08 Gastos extraordinarios · 09 Compras y gastos que formarán parte del costo de venta · 10 Adquisiciones de activos · 11 Gastos de seguros.",
    "No lleva pruebas de regresión: queda lista para activar al vincular su fuente oficial (el instructivo del 606).",
  ],
};

/**
 * Starting definitions (E-B03-15-3): the shape the Tax Engine expects, with rates the fiscal specialist must confirm against
 * the official source. They are templates, never defaults the system applies.
 */
export const DEFINITION_TEMPLATES: Readonly<Record<FiscalRuleKind, string>> = {
  PURCHASE_ITBIS: JSON.stringify({ tax_code: "ITBIS", rate: "0.18", effect: "RECOVERABLE_INPUT", exempt_item_categories: [] }, null, 2),
  PURCHASE_WITHHOLDING: JSON.stringify({ tax_code: "RET_ITBIS", rate: "0.30", base: "ITBIS", party_types: ["INDIVIDUAL"] }, null, 2),
  SALES_ITBIS: JSON.stringify({ tax_code: "ITBIS", rate: "0.18", effect: "OUTPUT", exempt_item_categories: [] }, null, 2),
  REPORT_606_CLASSIFICATION: JSON.stringify({ classes: { CEMENTO: "09", AGREGADO: "09", ADITIVO: "09", OTRA_MATERIA_PRIMA: "09" } }, null, 2),
};

/** One regression case to start from; the analyst writes the expected taxes the source dictates. */
export const CASES_TEMPLATE = JSON.stringify(
  [
    {
      caseId: "caso-1",
      partyType: "COMPANY",
      itemCategory: "CEMENTO",
      netAmount: "1000.00",
      itbisAmount: "180.00",
      expected: [{ taxCode: "ITBIS", amount: "180.00", effect: "RECOVERABLE_INPUT" }],
    },
  ],
  null,
  2,
);

/** Parses JSON typed in a textarea; returns an error message in Spanish instead of throwing. */
export function parseJson<T>(text: string): { value: T; error: null } | { value: null; error: string } {
  try {
    return { value: JSON.parse(text) as T, error: null };
  } catch (caught) {
    return { value: null, error: `JSON inválido: ${caught instanceof Error ? caught.message : String(caught)}` };
  }
}

export interface PolicyDefinitionLike {
  paramCode: string;
}

export interface PolicyVersionLike {
  status: string;
  version: number;
  parameters: Record<string, string>;
}

/**
 * Initial values of a new policy version: those of the ACTIVE version (or the newest one) for every defined parameter, empty
 * for a parameter never set. The preparer changes what the policy change is about; every parameter is sent (E-PR06-3).
 */
export function initialPolicyValues(definitions: readonly PolicyDefinitionLike[], versions: readonly PolicyVersionLike[]): Record<string, string> {
  const source = versions.find((v) => v.status === "ACTIVE") ?? [...versions].sort((a, b) => b.version - a.version)[0];
  return Object.fromEntries(definitions.map((d) => [d.paramCode, source?.parameters[d.paramCode] ?? ""]));
}
