import { describe, expect, it } from "vitest";
import { DEFINITION_TEMPLATES, FISCAL_KIND_LABELS, ruleKindRunsTests } from "@/lib/configuration";
import {
  codeLabel,
  currentPeriod,
  GOODS_TYPES,
  isValidPeriod,
  monthInputToPeriod,
  PAYMENT_METHODS,
  periodToMonthInput,
  previousPeriod,
  warningLabel,
} from "@/lib/fiscalReports";

// FIS2-03 (E-FIS2-03-2/5/6): the default period, the 606 codes in Spanish and the classification kind on the rules screen.
describe("fiscal report periods", () => {
  it("defaults to the previous month in the Dominican Republic", () => {
    expect(previousPeriod(new Date("2026-09-29T12:00:00Z"))).toBe("202608");
    expect(previousPeriod(new Date("2026-01-15T12:00:00Z"))).toBe("202512");
    expect(previousPeriod(new Date("2026-10-01T02:00:00Z"))).toBe("202608"); // still 30 September in Santo Domingo (UTC−4)
    expect(currentPeriod(new Date("2026-09-29T12:00:00Z"))).toBe("202609");
  });

  it("accepts AAAAMM with a month 01…12 only", () => {
    expect(["202609", "202612", "202601"].map(isValidPeriod)).toEqual([true, true, true]);
    expect(["202613", "202600", "20269", "2026-09", ""].map(isValidPeriod)).toEqual([false, false, false, false, false]);
  });

  it("converts to and from a month input", () => {
    expect(periodToMonthInput("202609")).toBe("2026-09");
    expect(periodToMonthInput("x")).toBe("");
    expect(monthInputToPeriod("2026-09")).toBe("202609");
  });
});

describe("606 labels", () => {
  it("names the codes and the warnings in Spanish", () => {
    expect(Object.keys(GOODS_TYPES)).toHaveLength(11);
    expect(codeLabel(GOODS_TYPES, "09")).toBe("09 — Compras y gastos que formarán parte del costo de venta");
    expect(codeLabel(GOODS_TYPES, null)).toBe("—");
    expect(codeLabel(PAYMENT_METHODS, 4)).toBe("4 — Compra a crédito");
    expect(codeLabel(PAYMENT_METHODS, 9)).toBe("9");
    expect(warningLabel("CLASSIFICATION_MISSING")).toMatch(/clasificación del 606/);
    expect(warningLabel("ISR_WITHHOLDING_TYPE_MISSING")).toMatch(/tipo de retención/);
    expect(warningLabel("OTHER")).toBe("OTHER");
  });
});

describe("the 606 classification rule kind", () => {
  it("has a Spanish label, a valid template and no regression tests", () => {
    expect(FISCAL_KIND_LABELS.REPORT_606_CLASSIFICATION).toBe("Clasificación del 606");
    expect(JSON.parse(DEFINITION_TEMPLATES.REPORT_606_CLASSIFICATION)).toEqual({
      classes: { CEMENTO: "09", AGREGADO: "09", ADITIVO: "09", OTRA_MATERIA_PRIMA: "09" },
    });
    expect(ruleKindRunsTests("REPORT_606_CLASSIFICATION")).toBe(false);
    expect(ruleKindRunsTests("PURCHASE_ITBIS")).toBe(true);
  });
});

describe("TAX-606 on the reconciliation screens", () => {
  it("names the reconciliation and its three classifications in Spanish", async () => {
    const { RECONCILIATIONS, classificationLabel } = await import("@/lib/labels");
    expect(RECONCILIATIONS["TAX-606"]).toBeDefined();
    for (const code of ["TAX606_ITBIS_DIFFERENCE", "CLASSIFICATION_MISSING", "ISR_WITHHOLDING_TYPE_MISSING"]) {
      expect(classificationLabel(code)).not.toBe(code);
    }
  });
});
