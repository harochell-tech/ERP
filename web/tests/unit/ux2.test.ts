import { describe, expect, it } from "vitest";
import { CASES_TEMPLATE, DEFINITION_TEMPLATES, FISCAL_RULE_KINDS, humanizeExplanation, ruleLinesSummary } from "@/lib/configuration";
import { formatPercent, fractionToPercent, percentToFraction, shiftDecimalPoint } from "@/lib/decimal";
import {
  buildFiscalDefinition,
  casesToRows,
  describeFiscalDefinition,
  initialCases,
  ISR_WITHHOLDING_TYPES,
  isrWithholdingTypeLabel,
  parseFiscalDefinition,
  rowsToCases,
  validateCases,
  validateFiscalForm,
  type CaseRow,
  type FiscalRuleForm,
} from "@/lib/fiscalRuleForm";
import {
  formatParameterValue,
  fromInputValue,
  missingParameters,
  parameterLabelWithUnit,
  policyDiff,
  policyInForce,
  toInputValue,
  validateParameter,
  type PolicyParameterDefinition,
  type PolicyVersion,
} from "@/lib/policies";
import { areaLight, missingLabel, nextSteps, SETUP_STEPS, setupProgress, stepInfo } from "@/lib/setup";

describe("percentages move the decimal point on the text (E-UX2-1)", () => {
  it("fraction → percent", () => {
    expect(fractionToPercent("0")).toBe("0");
    expect(fractionToPercent("1")).toBe("100");
    expect(fractionToPercent("0.05")).toBe("5");
    expect(fractionToPercent("0.005")).toBe("0.5");
    expect(fractionToPercent("0.125")).toBe("12.5");
    expect(fractionToPercent("0.18")).toBe("18");
    expect(fractionToPercent("0.180000")).toBe("18");
    expect(fractionToPercent("0.123456")).toBe("12.3456");
    expect(fractionToPercent(".5")).toBe("50");
    expect(fractionToPercent("12")).toBe("1200");
    expect(fractionToPercent("abc")).toBeNull();
    expect(fractionToPercent("1,5")).toBeNull();
    expect(fractionToPercent("")).toBeNull();
    expect(fractionToPercent(null)).toBeNull();
  });

  it("percent → fraction", () => {
    expect(percentToFraction("0")).toBe("0");
    expect(percentToFraction("100")).toBe("1");
    expect(percentToFraction("5")).toBe("0.05");
    expect(percentToFraction("0.5")).toBe("0.005");
    expect(percentToFraction("12.5")).toBe("0.125");
    expect(percentToFraction("18")).toBe("0.18");
    expect(percentToFraction("5 %")).toBe("0.05");
    expect(percentToFraction(" 7.50% ")).toBe("0.075");
    expect(percentToFraction("12.3456")).toBe("0.123456");
    expect(percentToFraction("007")).toBe("0.07");
    expect(percentToFraction("")).toBeNull();
    expect(percentToFraction("%")).toBeNull();
    expect(percentToFraction("1,5")).toBeNull();
    expect(percentToFraction("5e2")).toBeNull();
    expect(percentToFraction("1.2.3")).toBeNull();
  });

  it("round-trips without losing digits", () => {
    for (const fraction of ["0", "1", "0.05", "0.005", "0.125", "0.18", "0.3", "0.000001", "0.999999"]) {
      expect(percentToFraction(fractionToPercent(fraction))).toBe(fraction);
    }
    expect(shiftDecimalPoint("-0.05", 2)).toBe("-5");
    expect(shiftDecimalPoint("-0.00", 2)).toBe("0");
    expect(shiftDecimalPoint("123.45", 0)).toBe("123.45");
  });

  it("formats a fraction as a percentage", () => {
    expect(formatPercent("0.05")).toBe("5 %");
    expect(formatPercent("0.075")).toBe("7.5 %");
    expect(formatPercent("")).toBe("—");
    expect(formatPercent("x")).toBe("x");
  });
});

const PERCENT: PolicyParameterDefinition = {
  paramCode: "receipt_tolerance_pct",
  valueType: "DECIMAL_PERCENT",
  minValue: "0",
  maxValue: "1",
  allowedValues: null,
  description: "Sobre-recepción permitida (fracción)",
  label: "Sobre-recepción permitida",
  unit: "PERCENT",
  example: "5 %",
  affects: "Cuánto más de lo pedido puede recibir el almacén.",
};
const AMOUNT: PolicyParameterDefinition = { ...PERCENT, paramCode: "rounding_difference_tolerance", valueType: "AMOUNT", minValue: "0", maxValue: null, label: "Tolerancia de redondeo", unit: "AMOUNT", example: "RD$ 1.00" };
const HOURS: PolicyParameterDefinition = { ...PERCENT, paramCode: "late_entry_hours", valueType: "INTEGER", minValue: "0", maxValue: null, label: "Registro tardío", unit: "HOURS", example: "48 horas" };
const OPTION: PolicyParameterDefinition = { ...PERCENT, paramCode: "method", valueType: "ENUM", minValue: null, maxValue: null, allowedValues: ["A", "B"], label: "Método", unit: "OPTION", example: null };

describe("policy parameters by unit (E-UX2-1/2)", () => {
  it("labels carry the unit", () => {
    expect(parameterLabelWithUnit(PERCENT)).toBe("Sobre-recepción permitida (%)");
    expect(parameterLabelWithUnit(AMOUNT)).toBe("Tolerancia de redondeo (RD$)");
    expect(parameterLabelWithUnit(HOURS)).toBe("Registro tardío (horas)");
    expect(parameterLabelWithUnit(OPTION)).toBe("Método");
    expect(parameterLabelWithUnit({ ...AMOUNT, label: null, unit: null })).toBe("Sobre-recepción permitida (fracción) (RD$)");
  });

  it("percentages are typed in % and sent as fractions; the rest as typed", () => {
    expect(toInputValue(PERCENT, "0.05")).toBe("5");
    expect(fromInputValue(PERCENT, "7.5")).toBe("0.075");
    expect(fromInputValue(AMOUNT, "1,000.50")).toBe("1000.50");
    expect(toInputValue(AMOUNT, "1.00")).toBe("1.00");
    expect(fromInputValue(HOURS, " 48 ")).toBe("48");
  });

  it("shows values with their unit", () => {
    expect(formatParameterValue(PERCENT, "0.05")).toBe("5 %");
    expect(formatParameterValue(AMOUNT, "1.0000")).toBe("RD$ 1.00");
    expect(formatParameterValue(HOURS, "48")).toBe("48 horas");
    expect(formatParameterValue({ ...HOURS, unit: "DAYS" }, "1")).toBe("1 día");
    expect(formatParameterValue(OPTION, "A")).toBe("A");
    expect(formatParameterValue(PERCENT, null)).toBe("—");
  });

  it("validates per unit and within bounds (text comparison)", () => {
    expect(validateParameter(PERCENT, "5")).toBeNull();
    expect(validateParameter(PERCENT, "100")).toBeNull();
    expect(validateParameter(PERCENT, "100.01")).toMatch(/entre 0 % y 100 %/);
    expect(validateParameter(PERCENT, "-1")).toMatch(/porcentaje/);
    expect(validateParameter(PERCENT, "1.23456")).toMatch(/4 decimales/);
    expect(validateParameter(PERCENT, "")).toMatch(/p\. ej\. 5 %/);
    expect(validateParameter(AMOUNT, "1,000.25")).toBeNull();
    expect(validateParameter(AMOUNT, "1.00001")).toMatch(/monto/);
    expect(validateParameter(HOURS, "48")).toBeNull();
    expect(validateParameter(HOURS, "4.5")).toMatch(/entero de horas/);
    expect(validateParameter(OPTION, "B")).toBeNull();
    expect(validateParameter(OPTION, "")).toMatch(/^Elija/);
    expect(validateParameter(OPTION, "C")).toMatch(/opciones/);
  });
});

describe("policy versions in force and their changes (E-UX2-3/4)", () => {
  const versions: PolicyVersion[] = [
    { status: "ACTIVE", version: 1, effectiveFrom: "2026-01-01", effectiveTo: "2027-01-01", parameters: { receipt_tolerance_pct: "0.05" } },
    { status: "ACTIVE", version: 2, effectiveFrom: "2027-01-01", effectiveTo: null, parameters: { receipt_tolerance_pct: "0.06", rounding_difference_tolerance: "1.00" } },
    { status: "DRAFT", version: 3, effectiveFrom: "2028-01-01", effectiveTo: null, parameters: { receipt_tolerance_pct: "0.075", rounding_difference_tolerance: "1.00" } },
  ];

  it("the version in force covers today; the end date is exclusive", () => {
    expect(policyInForce(versions, "2026-09-30")?.version).toBe(1);
    expect(policyInForce(versions, "2027-01-01")?.version).toBe(2);
    expect(policyInForce(versions, "2025-12-31")).toBeUndefined();
    expect(policyInForce([], "2026-09-30")).toBeUndefined();
  });

  it("names the parameters the version in force lacks", () => {
    expect(missingParameters([PERCENT, AMOUNT], versions[0]).map((d) => d.paramCode)).toEqual(["rounding_difference_tolerance"]);
    expect(missingParameters([PERCENT, AMOUNT], versions[1])).toEqual([]);
    expect(missingParameters([PERCENT, AMOUNT], undefined)).toEqual([]);
  });

  it("diffs in force → proposed by text, in definition order", () => {
    const rows = policyDiff([PERCENT, AMOUNT], versions[0]!.parameters, versions[2]!.parameters);
    expect(rows.map((r) => [r.paramCode, r.current, r.proposed, r.changed])).toEqual([
      ["receipt_tolerance_pct", "0.05", "0.075", true],
      ["rounding_difference_tolerance", null, "1.00", true],
    ]);
    const same = policyDiff([PERCENT], { receipt_tolerance_pct: "0.05" }, { receipt_tolerance_pct: "0.05" });
    expect(same[0]?.changed).toBe(false);
    // Only text: "0.050" differs from "0.05".
    expect(policyDiff([PERCENT], { receipt_tolerance_pct: "0.05" }, { receipt_tolerance_pct: "0.050" })[0]?.changed).toBe(true);
    expect(policyDiff([PERCENT], undefined, { receipt_tolerance_pct: "0.05", extra: "1" }).map((r) => r.paramCode)).toEqual(["receipt_tolerance_pct", "extra"]);
  });
});

describe("guided fiscal rule form builds the server's JSON (E-UX2-8)", () => {
  it("every template round-trips to the exact same text", () => {
    for (const kind of FISCAL_RULE_KINDS) {
      const parsed = parseFiscalDefinition(kind, DEFINITION_TEMPLATES[kind]);
      expect(parsed.error).toBeNull();
      expect(buildFiscalDefinition(kind, parsed.form!)).toBe(DEFINITION_TEMPLATES[kind]);
    }
  });

  const base: FiscalRuleForm = { taxCode: "ITBIS", ratePercent: "18", effect: "RECOVERABLE_INPUT", exemptItemCategories: [], base: "", partyTypes: [], isrWithholdingType: "", classes: {}, amount: "" };

  it("purchase ITBIS: rate as a fraction, exempt categories in catalogue order", () => {
    const json = buildFiscalDefinition("PURCHASE_ITBIS", { ...base, ratePercent: "16", effect: "NON_RECOVERABLE_INPUT", exemptItemCategories: ["ADITIVO", "CEMENTO"] });
    expect(json).toBe(JSON.stringify({ tax_code: "ITBIS", rate: "0.16", effect: "NON_RECOVERABLE_INPUT", exempt_item_categories: ["CEMENTO", "ADITIVO"] }, null, 2));
    expect(parseFiscalDefinition("PURCHASE_ITBIS", json).form).toMatchObject({ ratePercent: "16", exemptItemCategories: ["CEMENTO", "ADITIVO"] });
  });

  it("sales ITBIS", () => {
    const json = buildFiscalDefinition("SALES_ITBIS", { ...base, effect: "OUTPUT", exemptItemCategories: ["BLOQUE"] });
    expect(JSON.parse(json)).toEqual({ tax_code: "ITBIS", rate: "0.18", effect: "OUTPUT", exempt_item_categories: ["BLOQUE"] });
    expect(buildFiscalDefinition("SALES_ITBIS", parseFiscalDefinition("SALES_ITBIS", json).form!)).toBe(json);
  });

  it("withholding: base, party types, optional ISR type (only when chosen)", () => {
    const isr = { ...base, taxCode: "RET_ISR", ratePercent: "10", base: "NET", partyTypes: ["INDIVIDUAL", "COMPANY"], isrWithholdingType: "2" };
    const json = buildFiscalDefinition("PURCHASE_WITHHOLDING", isr);
    expect(json).toBe(JSON.stringify({ tax_code: "RET_ISR", rate: "0.1", base: "NET", party_types: ["COMPANY", "INDIVIDUAL"], isr_withholding_type: "2" }, null, 2));
    expect(buildFiscalDefinition("PURCHASE_WITHHOLDING", parseFiscalDefinition("PURCHASE_WITHHOLDING", json).form!)).toBe(json);
    expect(JSON.parse(buildFiscalDefinition("PURCHASE_WITHHOLDING", { ...isr, isrWithholdingType: "" }))).not.toHaveProperty("isr_withholding_type");
  });

  it("606 classification: the four raw-material categories", () => {
    const json = buildFiscalDefinition("REPORT_606_CLASSIFICATION", { ...base, classes: { AGREGADO: "02", CEMENTO: "09", ADITIVO: "09", OTRA_MATERIA_PRIMA: "06" } });
    expect(json).toBe(JSON.stringify({ classes: { CEMENTO: "09", AGREGADO: "02", ADITIVO: "09", OTRA_MATERIA_PRIMA: "06" } }, null, 2));
    expect(validateFiscalForm("REPORT_606_CLASSIFICATION", parseFiscalDefinition("REPORT_606_CLASSIFICATION", json).form!)).toEqual({});
    expect(validateFiscalForm("REPORT_606_CLASSIFICATION", { ...base, classes: { CEMENTO: "12" } })).toHaveProperty("class-CEMENTO");
  });

  it("refuses what the form cannot show", () => {
    expect(parseFiscalDefinition("PURCHASE_ITBIS", "{").error).toMatch(/^JSON inválido/);
    expect(parseFiscalDefinition("PURCHASE_ITBIS", "[]").error).toMatch(/objeto/);
    expect(parseFiscalDefinition("PURCHASE_ITBIS", '{"tax_code":"ITBIS","base":"NET"}').error).toMatch(/base/);
    expect(parseFiscalDefinition("PURCHASE_ITBIS", '{"rate":0.18}').error).toMatch(/rate/);
  });

  it("validates the form in Spanish", () => {
    expect(validateFiscalForm("PURCHASE_ITBIS", base)).toEqual({});
    expect(validateFiscalForm("PURCHASE_ITBIS", { ...base, taxCode: "itbis", ratePercent: "0", effect: "OUTPUT" })).toEqual({
      taxCode: expect.any(String),
      ratePercent: expect.any(String),
      effect: expect.any(String),
    });
    expect(validateFiscalForm("SALES_ITBIS", { ...base, ratePercent: "100.5", effect: "OUTPUT" })).toHaveProperty("ratePercent");
    expect(validateFiscalForm("SALES_ITBIS", { ...base, ratePercent: "100", effect: "OUTPUT" })).toEqual({});
    expect(validateFiscalForm("PURCHASE_WITHHOLDING", { ...base, taxCode: "RET", ratePercent: "30" })).toEqual({ base: expect.any(String), partyTypes: expect.any(String) });
  });

  it("describes stored definitions in words", () => {
    expect(describeFiscalDefinition("PURCHASE_ITBIS", DEFINITION_TEMPLATES.PURCHASE_ITBIS)).toEqual(["ITBIS al 18 %", "Adelantado (se descuenta del ITBIS por pagar)", "Sin categorías exentas"]);
    expect(describeFiscalDefinition("PURCHASE_WITHHOLDING", DEFINITION_TEMPLATES.PURCHASE_WITHHOLDING)).toEqual([
      "RET_ITBIS al 30 %",
      "Sobre el ITBIS facturado (retención de ITBIS)",
      "Se retiene a: Persona física (cédula)",
    ]);
    expect(describeFiscalDefinition("REPORT_606_CLASSIFICATION", DEFINITION_TEMPLATES.REPORT_606_CLASSIFICATION)[0]).toBe("Cemento: 09 Compras y gastos que formarán parte del costo de venta");
    expect(describeFiscalDefinition("PURCHASE_ITBIS", "not json")).toEqual(["not json"]);
  });
});

describe("regression cases as a table (E-UX2-8)", () => {
  it("produces the same payload as the JSON template", () => {
    const template = JSON.parse(CASES_TEMPLATE) as CaseRow[];
    expect(rowsToCases(casesToRows(template))).toEqual(template);
  });

  it("trims texts and removes grouping from amounts", () => {
    const rows: CaseRow[] = [{ caseId: " c1 ", partyType: "COMPANY", itemCategory: "CEMENTO", netAmount: "1,000.00", itbisAmount: "180.00", expected: [{ taxCode: " ITBIS ", amount: "180.00", effect: "RECOVERABLE_INPUT" }] }];
    expect(rowsToCases(rows)).toEqual([{ caseId: "c1", partyType: "COMPANY", itemCategory: "CEMENTO", netAmount: "1000.00", itbisAmount: "180.00", expected: [{ taxCode: "ITBIS", amount: "180.00", effect: "RECOVERABLE_INPUT" }] }]);
  });

  it("starts from the rule's tax code and effect, amount left to the analyst", () => {
    expect(initialCases("PURCHASE_WITHHOLDING", DEFINITION_TEMPLATES.PURCHASE_WITHHOLDING)[0]).toMatchObject({
      partyType: "INDIVIDUAL",
      expected: [{ taxCode: "RET_ITBIS", amount: "", effect: "WITHHOLDING" }],
    });
    expect(initialCases("SALES_ITBIS", DEFINITION_TEMPLATES.SALES_ITBIS)[0]).toMatchObject({ itemCategory: "BLOQUE", expected: [{ taxCode: "ITBIS", effect: "OUTPUT" }] });
  });

  it("flags empty, repeated and malformed cells", () => {
    const rows: CaseRow[] = [
      { caseId: "a", partyType: "COMPANY", itemCategory: "CEMENTO", netAmount: "1", itbisAmount: "0.18", expected: [] },
      { caseId: "a", partyType: "", itemCategory: "", netAmount: "x", itbisAmount: "-1", expected: [{ taxCode: "", amount: "1.00001", effect: "" }] },
    ];
    expect(Object.keys(validateCases(rows)).sort()).toEqual(
      [
        "case-1-caseId",
        "case-1-partyType",
        "case-1-itemCategory",
        "case-1-netAmount",
        "case-1-itbisAmount",
        "case-1-expected-0-taxCode",
        "case-1-expected-0-amount",
        "case-1-expected-0-effect",
      ].sort(),
    );
  });
});

describe("posting rule lines (E-UX2-7)", () => {
  it("summarizes debits and credits by the roles' names", () => {
    expect(
      ruleLinesSummary([
        { side: "DEBIT", accountRole: "RAW_MATERIAL", accountRoleName: "Inventario de materia prima" },
        { side: "CREDIT", accountRole: "GRNI", accountRoleName: "Recibido no facturado (GRNI)" },
      ]),
    ).toBe("Genera: Débito Inventario de materia prima / Crédito Recibido no facturado (GRNI)");
    expect(
      ruleLinesSummary([
        { side: "DEBIT", accountRole: "AR_CONTROL", accountRoleName: null },
        { side: "CREDIT", accountRole: "REVENUE_PRODUCT", accountRoleName: "Ingresos" },
        { side: "CREDIT", accountRole: "ITBIS_PAYABLE", accountRoleName: "ITBIS por pagar" },
      ]),
    ).toBe("Genera: Débito AR_CONTROL / Crédito Ingresos + ITBIS por pagar");
    expect(ruleLinesSummary(null)).toBe("");
  });

  it("makes placeholders readable", () => {
    expect(humanizeExplanation("Factura {ncf} del conduce {delivery_no} ({some_thing})")).toBe("Factura [NCF] del conduce [n.º de conduce] ([some thing])");
    expect(humanizeExplanation(null)).toBe("");
  });
});

describe("setup steps in Spanish (E-UX2-9/10)", () => {
  const step = (order: number, code: string, area: string, status: string, missing: string[] = []) => ({ order, code, area, status, missing });

  it("names and links all 19 steps", () => {
    const codes = [
      "COMPANY", "PLANTS", "USERS", "PERIODS", "ACCOUNTS", "REPORT_STRUCTURES", "ACCOUNT_MAPS", "POSTING_RULES", "POLICIES", "FISCAL_SOURCES",
      "FISCAL_RULES", "BANK_ACCOUNTS", "SUPPLIERS", "ITEMS", "STANDARD_COSTS", "PRICE_LIST", "CUSTOMERS", "RECIPES", "OPENING_INVENTORY",
    ];
    expect(Object.keys(SETUP_STEPS)).toEqual(codes);
    for (const code of codes) {
      expect(stepInfo(code).href).toMatch(/^\/[a-z-/]+\/$/);
    }
    expect(stepInfo("NEW_STEP").title).toBe("NEW_STEP");
  });

  it("lights an area: red on PENDING, amber on WARNING, green when all DONE", () => {
    expect(areaLight([step(1, "A", "X", "DONE"), step(2, "B", "X", "DONE")])).toBe("green");
    expect(areaLight([step(1, "A", "X", "DONE"), step(2, "B", "X", "WARNING")])).toBe("amber");
    expect(areaLight([step(1, "A", "X", "WARNING"), step(2, "B", "X", "PENDING")])).toBe("red");
  });

  it("translates what is missing", () => {
    expect(missingLabel("USERS", "SEGUNDO_APROBADOR_SEGURIDAD")).toBe("Nadie tiene el rol Segundo aprobador de seguridad");
    expect(missingLabel("REPORT_STRUCTURES", "INCOME_STATEMENT")).toBe("Estado de resultados sin estructura activa");
    expect(missingLabel("FISCAL_RULES", "SALES_ITBIS")).toBe("Falta una regla activa de ITBIS de ventas");
    expect(missingLabel("ACCOUNT_MAPS", "BANK", { accountRoles: { BANK: "Bancos" } })).toBe("Bancos sin cuenta asignada para hoy");
    expect(missingLabel("ACCOUNT_MAPS", "WIP")).toBe("WIP sin cuenta asignada para hoy");
    expect(missingLabel("POLICIES", "TREASURY")).toBe("Política Tesorería sin versión completa en vigor");
    expect(missingLabel("PERIODS", "TODAY")).toBe("No hay un período que cubra la fecha de hoy");
    expect(missingLabel("PLANTS", "P1")).toBe("Planta P1 sin nombre");
    expect(missingLabel("CUSTOMERS", "Constructora Uno")).toBe("Constructora Uno sin condiciones de crédito activas");
    expect(missingLabel("ITEMS", "RAW_MATERIAL")).toBe("No hay una materia prima activa");
    expect(missingLabel("OPENING_INVENTORY", "BATCH")).toBe("No hay un lote de inventario de apertura contabilizado");
    expect(missingLabel("UNKNOWN", "X")).toBe("X");
  });

  it("counts progress and picks the next steps in order", () => {
    const status = { complete: false, steps: [step(3, "USERS", "SEGURIDAD", "WARNING"), step(1, "COMPANY", "EMPRESA", "DONE"), step(2, "PLANTS", "EMPRESA", "PENDING"), step(4, "PERIODS", "CONTABILIDAD", "DONE")] };
    expect(setupProgress(status)).toEqual({ done: 2, total: 4 });
    expect(nextSteps(status, 3).map((s) => s.code)).toEqual(["PLANTS", "USERS"]);
  });
});

describe("ISR withholding types (E-UX2-14)", () => {
  it("names the nine types of field 17 of the 606 as the DGII does", () => {
    expect(ISR_WITHHOLDING_TYPES.map(isrWithholdingTypeLabel)).toEqual([
      "1 — Alquileres",
      "2 — Honorarios por servicios",
      "3 — Otras rentas",
      "4 — Otras rentas (rentas presuntas)",
      "5 — Intereses pagados a personas jurídicas residentes",
      "6 — Intereses pagados a personas físicas residentes",
      "7 — Retención por proveedores del Estado",
      "8 — Juegos telefónicos",
      "9 — Retenciones subsector de ganadería de carne bovina",
    ]);
    expect(isrWithholdingTypeLabel("0")).toBe("0");
  });
});
