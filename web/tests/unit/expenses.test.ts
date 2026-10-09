import { describe, expect, it } from "vitest";
import { previewLines } from "@/components/ExpenseLines";
import { DEFINITION_TEMPLATES, FISCAL_RULE_KINDS } from "@/lib/configuration";
import { categoryCode, EMPTY_EXPENSE_LINE } from "@/lib/expenses";
import { buildFiscalDefinition, describeFiscalDefinition, initialCases, parseFiscalDefinition, validateCases, validateFiscalForm } from "@/lib/fiscalRuleForm";
import { taxEffectLabel } from "@/lib/ux4a-compras";

const TELECOM = JSON.stringify({
  label: "Telecomunicaciones",
  components: [
    { tax_code: "ITBIS", rate: "0.18", effect: "RECOVERABLE_INPUT" },
    { tax_code: "ISC", rate: "0.10", effect: "SELECTIVE_TAX" },
    { tax_code: "CDT", rate: "0.02", effect: "OTHER_TAX" },
  ],
});

describe("purchase tax types in the guided form (E-GAS-07-5)", () => {
  it("is offered with a template the form can show", () => {
    expect(FISCAL_RULE_KINDS).toContain("PURCHASE_TAX_TYPE");
    const parsed = parseFiscalDefinition("PURCHASE_TAX_TYPE", DEFINITION_TEMPLATES.PURCHASE_TAX_TYPE);
    expect(parsed.form?.label).toBe("ITBIS 18 %");
    expect(parsed.form?.components).toEqual([{ taxCode: "ITBIS", ratePercent: "18", rateSource: "0.18", effect: "RECOVERABLE_INPUT" }]);
  });

  it("reads and rebuilds a type with its rates in % and keeps the stored text", () => {
    const form = parseFiscalDefinition("PURCHASE_TAX_TYPE", TELECOM).form!;
    expect(form.components!.map((c) => c.ratePercent)).toEqual(["18", "10", "2"]);
    expect(JSON.parse(buildFiscalDefinition("PURCHASE_TAX_TYPE", form))).toEqual(JSON.parse(TELECOM));
    form.components![2] = { ...form.components![2]!, ratePercent: "2.5" };
    expect(JSON.parse(buildFiscalDefinition("PURCHASE_TAX_TYPE", form)).components[2].rate).toBe("0.025");
  });

  it("builds an exempt type with no component", () => {
    const form = parseFiscalDefinition("PURCHASE_TAX_TYPE", JSON.stringify({ label: "Exento", components: [] })).form!;
    expect(validateFiscalForm("PURCHASE_TAX_TYPE", form)).toEqual({});
    expect(buildFiscalDefinition("PURCHASE_TAX_TYPE", form)).toBe(JSON.stringify({ label: "Exento", components: [] }, null, 2));
    expect(describeFiscalDefinition("PURCHASE_TAX_TYPE", buildFiscalDefinition("PURCHASE_TAX_TYPE", form))).toEqual(["Exento", "Sin impuestos (exento)"]);
  });

  it("refuses an unknown key, a missing name, a repeated code, a bad rate and an effect the type cannot have", () => {
    expect(parseFiscalDefinition("PURCHASE_TAX_TYPE", JSON.stringify({ label: "X", components: [], rate: "0.18" })).error).toContain("rate");
    const form = parseFiscalDefinition("PURCHASE_TAX_TYPE", TELECOM).form!;
    form.label = " ";
    form.components = [
      { taxCode: "ITBIS", ratePercent: "18", effect: "RECOVERABLE_INPUT" },
      { taxCode: "ITBIS", ratePercent: "0", effect: "WITHHOLDING" },
    ];
    expect(Object.keys(validateFiscalForm("PURCHASE_TAX_TYPE", form)).sort()).toEqual(["component-1-effect", "component-1-ratePercent", "component-1-taxCode", "label"]);
  });

  it("starts its regression case on the net alone, one expected tax per component, without item category", () => {
    const [first] = initialCases("PURCHASE_TAX_TYPE", TELECOM);
    expect(first).toMatchObject({ itemCategory: "", netAmount: "1000.00", itbisAmount: "0.00" });
    expect(first!.expected.map((e) => `${e.taxCode}:${e.effect}`)).toEqual(["ITBIS:RECOVERABLE_INPUT", "ISC:SELECTIVE_TAX", "CDT:OTHER_TAX"]);
    const filled = [{ ...first!, expected: first!.expected.map((e) => ({ ...e, amount: "1.00" })) }];
    expect(validateCases(filled, "PURCHASE_TAX_TYPE")).toEqual({});
    expect(validateCases(filled)).toHaveProperty("case-0-itemCategory");
  });

  it("limits a withholding to the lines it applies to", () => {
    const form = parseFiscalDefinition("PURCHASE_WITHHOLDING", DEFINITION_TEMPLATES.PURCHASE_WITHHOLDING).form!;
    form.appliesTo = ["EXPENSE_SERVICE"];
    expect(JSON.parse(buildFiscalDefinition("PURCHASE_WITHHOLDING", form)).applies_to).toEqual(["EXPENSE_SERVICE"]);
  });

  it("keeps a withholding to B-series invoices when it says so (X1-02, E-X1-02-3)", () => {
    const form = parseFiscalDefinition("PURCHASE_WITHHOLDING", DEFINITION_TEMPLATES.PURCHASE_WITHHOLDING).form!;
    form.documentSeries = ["B"];
    const built = buildFiscalDefinition("PURCHASE_WITHHOLDING", form);
    expect(JSON.parse(built).document_series).toEqual(["B"]);
    expect(parseFiscalDefinition("PURCHASE_WITHHOLDING", built).form!.documentSeries).toEqual(["B"]);
  });
});

describe("expense lines (E-GAS-07-2/3)", () => {
  it("names a category's code from its name", () => {
    expect(categoryCode("Teléfono e internet")).toBe("TELEFONO_E_INTERNET");
    expect(categoryCode("  Reparación de equipos (planta)  ")).toBe("REPARACION_DE_EQUIPOS_PLANTA");
    expect(categoryCode("5S y limpieza")).toBe("C_5S_Y_LIMPIEZA");
  });

  it("asks the server for a preview only when every line is complete", () => {
    const line = { ...EMPTY_EXPENSE_LINE, description: " Teléfono ", expenseCategoryId: "c", taxTypeId: "t", quantity: "1", unitPrice: "5,000.00" };
    expect(previewLines([line])).toEqual([{ description: "Teléfono", expenseCategoryId: "c", taxTypeId: "t", quantity: "1", unitPrice: "5000.00" }]);
    expect(previewLines([line, { ...EMPTY_EXPENSE_LINE }])).toBeNull();
    expect(previewLines([])).toBeNull();
  });

  it("names the expense taxes in Spanish (E-GAS-07-4)", () => {
    expect(["SELECTIVE_TAX", "OTHER_TAX", "LEGAL_TIP"].map(taxEffectLabel)).toEqual([
      "Selectivo al consumo (gasto)",
      "Otros impuestos y tasas (gasto)",
      "Propina legal (gasto)",
    ]);
  });
});

describe("an expense invoice's status (E-GAS-07-4)", () => {
  it("reads without the receipt and as waiting for approval", async () => {
    const { invoiceStatusLabel } = await import("@/lib/ux4a-compras");
    expect(invoiceStatusLabel("MATCHED", "EXPENSE")).toBe("Cotejada, lista para contabilizar");
    expect(invoiceStatusLabel("MATCH_EXCEPTION", "EXPENSE")).toBe("Pendiente de aprobación");
    expect(invoiceStatusLabel("POSTED", "EXPENSE")).toBe("Contabilizada");
    expect(invoiceStatusLabel("MATCHED", "INVENTORY")).toBe("Cotejada con OC y recepción");
  });
});
