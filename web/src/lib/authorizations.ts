// FIS1-05 (E-FIS1-05-1…12): pure helpers of the fiscal authorization screens (CONFOTUR exempt sales). No money or quantity
// arithmetic: available amounts are the server's.

/** The seven statuses of an authorization, in lifecycle order (the list's filter). */
export const AUTHORIZATION_STATUSES = ["DRAFT", "PENDING_VERIFICATION", "ACTIVE", "SUSPENDED", "EXHAUSTED", "EXPIRED", "REJECTED"] as const;

/** E-FIS1-01: the documents an authorization carries; the DGII certificate is required to submit. */
export const AUTHORIZATION_DOCUMENT_KINDS: Readonly<Record<string, string>> = {
  CERTIFICADO_DGII: "Certificado de exención (DGII)",
  RESOLUCION_CONFOTUR: "Resolución CONFOTUR",
  LISTA_MATERIALES: "Lista de materiales",
  PROFORMA: "Proforma",
};

export function documentKindLabel(kind: string): string {
  return AUTHORIZATION_DOCUMENT_KINDS[kind] ?? kind;
}

export type AuthorizationAction = "EDIT" | "SUBMIT" | "VERIFY" | "RETURN" | "REJECT" | "SUSPEND" | "REACTIVATE" | "ATTACH";

/**
 * The actions a user may be offered on an authorization in `status`, given the permissions it holds (`can`) and whether it
 * registered it (the verifier is never the registrar, E-FIS1-3; the server refuses it anyway).
 */
export function authorizationActions(status: string, can: (permission: string) => boolean, isRegistrar: boolean): AuthorizationAction[] {
  const actions: AuthorizationAction[] = [];
  const register = can("fiscal_authorization:register");
  const verify = can("fiscal_authorization:verify");
  const suspend = can("fiscal_authorization:suspend");
  if (status === "DRAFT" && register) {
    actions.push("EDIT", "SUBMIT");
  }
  if (status === "PENDING_VERIFICATION" && verify) {
    if (!isRegistrar) {
      actions.push("VERIFY");
    }
    actions.push("RETURN", "REJECT");
  }
  if (status === "ACTIVE" && suspend) {
    actions.push("SUSPEND");
  }
  if (status === "SUSPENDED" && suspend) {
    actions.push("REACTIVATE");
  }
  if (register && status !== "EXPIRED" && status !== "REJECTED") {
    actions.push("ATTACH");
  }
  return actions;
}

/** The e-NCF prefix of an invoice's e-CF type (E31, E32, E44). */
export function invoiceEncfPrefix(ecfType: string): "E31" | "E32" | "E44" {
  return ecfType === "32" ? "E32" : ecfType === "44" ? "E44" : "E31";
}

/** How many authorizations `ExpireFiscalAuthorizations` moved to EXPIRED (its result is `{ expired: [ids] }`). */
export function expiredCount(result: unknown): number {
  if (result !== null && typeof result === "object" && "expired" in result) {
    const expired = (result as { expired: unknown }).expired;
    return Array.isArray(expired) ? expired.length : 0;
  }
  return 0;
}
