import { describe, expect, it } from "vitest";
import { AUTHORIZATION_STATUSES, authorizationActions, documentKindLabel, expiredCount, invoiceEncfPrefix } from "@/lib/authorizations";
import { classificationLabel, RECONCILIATIONS, statusLabel, statusTone } from "@/lib/labels";

// FIS1-05 (E-FIS1-05-1…12): the fiscal authorization screens' helpers.
const holding =
  (...permissions: string[]) =>
  (permission: string) =>
    permissions.includes(permission);

describe("fiscal authorization actions", () => {
  const registrar = holding("fiscal_authorization:register");
  const specialist = holding("fiscal_authorization:verify", "fiscal_authorization:suspend");

  it("lets Crédito and Facturación edit, submit and attach a draft", () => {
    expect(authorizationActions("DRAFT", registrar, true)).toEqual(["EDIT", "SUBMIT", "ATTACH"]);
    expect(authorizationActions("DRAFT", specialist, false)).toEqual([]);
  });

  it("offers verify only to a specialist who did not register it", () => {
    expect(authorizationActions("PENDING_VERIFICATION", specialist, false)).toEqual(["VERIFY", "RETURN", "REJECT"]);
    expect(authorizationActions("PENDING_VERIFICATION", specialist, true)).toEqual(["RETURN", "REJECT"]);
    expect(authorizationActions("PENDING_VERIFICATION", registrar, true)).toEqual(["ATTACH"]);
  });

  it("suspends an active one, reactivates a suspended one and attaches nothing to a terminal one", () => {
    expect(authorizationActions("ACTIVE", specialist, false)).toEqual(["SUSPEND"]);
    expect(authorizationActions("SUSPENDED", specialist, false)).toEqual(["REACTIVATE"]);
    expect(authorizationActions("EXHAUSTED", registrar, false)).toEqual(["ATTACH"]);
    expect(authorizationActions("EXPIRED", holding("fiscal_authorization:register", "fiscal_authorization:suspend"), false)).toEqual([]);
    expect(authorizationActions("REJECTED", registrar, false)).toEqual([]);
  });
});

describe("fiscal authorization labels", () => {
  it("names every status and gives it a tone", () => {
    expect(AUTHORIZATION_STATUSES.map(statusLabel)).toEqual(["Borrador", "Pendiente de verificación", "Activo", "Suspendido", "Agotado", "Vencido", "Rechazado"]);
    expect(AUTHORIZATION_STATUSES.map(statusTone)).toEqual(["neutral", "progress", "done", "attention", "neutral", "neutral", "error"]);
  });

  it("names the documents, the reconciliations and their classifications", () => {
    expect(documentKindLabel("CERTIFICADO_DGII")).toBe("Certificado de exención (DGII)");
    expect(documentKindLabel("OTRO")).toBe("OTRO");
    expect(Object.keys(RECONCILIATIONS)).toEqual(["AUTH-CONSUMPTION", "EXEMPT-WITHOUT-AUTH", "AUTH-EXPIRY", "TAX-606", "CONTROLS-WAIVED"]);
    expect(classificationLabel("PROJECT_TERM_ENDED")).toBe("Terminó el plazo del proyecto");
    expect(classificationLabel("SOMETHING_ELSE")).toBe("SOMETHING_ELSE");
  });
});

describe("exempt invoices", () => {
  it("uses the e-NCF prefix of the invoice's e-CF type", () => {
    expect(["31", "32", "44"].map(invoiceEncfPrefix)).toEqual(["E31", "E32", "E44"]);
  });

  it("counts what the expiry run moved to EXPIRED", () => {
    expect(expiredCount({ expired: ["a", "b"] })).toBe(2);
    expect(expiredCount({ expired: [] })).toBe(0);
    expect(expiredCount(null)).toBe(0);
    expect(expiredCount("x")).toBe(0);
  });
});
