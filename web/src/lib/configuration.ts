// E-B03-15: helpers of the configuration screens, kept pure so their behaviour is unit-tested. No amount arithmetic here:
// decimals travel as the strings the user typed (E-PR18b-11); the server validates every value.

/** Hex SHA-256 of a file's bytes, computed in the browser; the file itself is not uploaded (E-B03-15-3). */
export async function sha256Hex(data: ArrayBuffer): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", data);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

export const FISCAL_RULE_KINDS = ["PURCHASE_ITBIS", "PURCHASE_WITHHOLDING"] as const;
export type FiscalRuleKind = (typeof FISCAL_RULE_KINDS)[number];

export const FISCAL_KIND_LABELS: Readonly<Record<string, string>> = {
  PURCHASE_ITBIS: "ITBIS de compras",
  PURCHASE_WITHHOLDING: "Retención en compras",
};

/**
 * Starting definitions (E-B03-15-3): the shape the Tax Engine expects, with rates the fiscal specialist must confirm against
 * the official source. They are templates, never defaults the system applies.
 */
export const DEFINITION_TEMPLATES: Readonly<Record<FiscalRuleKind, string>> = {
  PURCHASE_ITBIS: JSON.stringify({ tax_code: "ITBIS", rate: "0.18", effect: "RECOVERABLE_INPUT", exempt_item_categories: [] }, null, 2),
  PURCHASE_WITHHOLDING: JSON.stringify({ tax_code: "RET_ITBIS", rate: "0.30", base: "ITBIS", party_types: ["INDIVIDUAL"] }, null, 2),
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
