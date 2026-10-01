import { describe, expect, it } from "vitest";
import { screenTitle } from "@/components/Shell";
import { authorizationsFor, certificationLabel, certificationStatus, collectsLabel, dueText, proformaStatusLabel } from "@/lib/proformas";

describe("proformas", () => {
  it("names the states and the certification in Spanish", () => {
    expect(["OPEN", "INVOICED", "VOIDED", "X"].map(proformaStatusLabel)).toEqual(["Abierta", "Facturada", "Anulada", "X"]);
    expect(["NONE", "IN_PROCESS", "CERTIFIED"].map(certificationLabel)).toEqual(["Sin certificación", "Certificación en trámite", "Certificada"]);
    expect(["NONE", "IN_PROCESS", "CERTIFIED"].map(certificationStatus)).toEqual(["DRAFT", "PENDING_VERIFICATION", "ACTIVE"]);
    expect([true, false].map(collectsLabel)).toEqual(["Se cobra con ITBIS", "Se cobra sin ITBIS"]);
  });

  it("says when an open proforma is due, overdue or collected", () => {
    expect(dueText({ status: "OPEN", balance: "100.00", daysOverdue: 0 }, "31/10/2026")).toBe("Vence el 31/10/2026");
    expect(dueText({ status: "OPEN", balance: "100.00", daysOverdue: 1 }, "31/10/2026")).toBe("Vencida hace 1 día");
    expect(dueText({ status: "OPEN", balance: "100.00", daysOverdue: 12 }, "31/10/2026")).toBe("Vencida hace 12 días");
    expect(dueText({ status: "OPEN", balance: "0.00", daysOverdue: 12 }, "31/10/2026")).toBe("Cobrada");
    expect(dueText({ status: "INVOICED", balance: "0.00", daysOverdue: 0 }, "31/10/2026")).toBe("—");
  });

  it("titles the list and the detail as Proformas", () => {
    expect(screenTitle("/facturacion/proformas/")).toBe("Proformas");
    expect(screenTitle("/facturacion/proforma/")).toBe("Proformas");
    expect(screenTitle("/ventas/proforma/")).toBe("Pedidos");
  });

  it("offers only the authorizations that cite every chosen proforma", () => {
    const cited = new Map([
      ["a1", new Set(["p1", "p2"])],
      ["a2", new Set(["p3"])],
    ]);
    expect(authorizationsFor([], cited)).toEqual([]);
    expect(authorizationsFor(["p1"], cited)).toEqual(["a1"]);
    expect(authorizationsFor(["p1", "p2"], cited)).toEqual(["a1"]);
    expect(authorizationsFor(["p1", "p3"], cited)).toEqual([]);
    expect(authorizationsFor(["p3"], cited)).toEqual(["a2"]);
  });
});
