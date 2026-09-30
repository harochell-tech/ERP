import { describe, expect, it } from "vitest";
import {
  approverPending,
  balanceCell,
  effectiveFromLabel,
  filterAccounts,
  inMonth,
  isZeroDecimal,
  lineOptionLabel,
  openingButtonLabel,
  openingMissing,
  openingTemplateCsv,
  sameStructure,
  shortHash,
} from "@/lib/ux4a-contabilidad";

// UX4-02 (A-10…A-18): the pure helpers of the accounting screens.

describe("zero balances (A-11, A-16)", () => {
  it("tests zero on the text", () => {
    expect(["0", "0.00", "-0.00", "0.0000"].map(isZeroDecimal)).toEqual([true, true, true, true]);
    expect(["0.01", "-1250.00", "", "abc"].map(isZeroDecimal)).toEqual([false, false, false, false]);
    expect(isZeroDecimal(null)).toBe(false);
  });

  it("leaves a zero debit or credit balance blank", () => {
    expect(balanceCell("0.00")).toBeNull();
    expect(balanceCell("1250.00")).toBe("1250.00");
  });
});

describe("account search (A-12)", () => {
  const accounts = [
    { accountId: "1", code: "1100", name: "Banco operativo" },
    { accountId: "2", code: "6200", name: "Energía eléctrica" },
    { accountId: "3", code: "2200", name: "Gastos acumulados por pagar" },
  ];

  it("matches code or name, ignoring case and accents", () => {
    expect(filterAccounts(accounts, "energia").map((a) => a.code)).toEqual(["6200"]);
    expect(filterAccounts(accounts, "22").map((a) => a.code)).toEqual(["2200"]);
    expect(filterAccounts(accounts, "  ").map((a) => a.code)).toEqual(["1100", "6200", "2200"]);
  });

  it("keeps the chosen account listed", () => {
    expect(filterAccounts(accounts, "banco", "2").map((a) => a.code)).toEqual(["1100", "6200"]);
  });
});

describe("adjustments (A-14, A-15)", () => {
  it("shortens the hash", () => {
    expect(shortHash("a".repeat(60) + "bcde")).toBe("aaaaaaaa…bcde");
    expect(shortHash("")).toBe("—");
  });

  it("names the approver only while pending", () => {
    expect(["DRAFT", "PENDING_APPROVAL", "POSTED", "REJECTED"].map(approverPending)).toEqual([true, true, false, false]);
  });

  it("filters by month", () => {
    expect(inMonth("2026-09-30", "2026-09")).toBe(true);
    expect(inMonth("2026-08-31", "2026-09")).toBe(false);
    expect(inMonth("2026-08-31", "")).toBe(true);
  });
});

describe("report structures (A-17)", () => {
  const lines = [
    { lineCode: "A", caption: "Activo", parentLineCode: "", sign: 1 },
    { lineCode: "P", caption: "Pasivo", parentLineCode: "", sign: -1 },
  ];
  const original = { lines, placement: { x: "A", y: "P" } };

  it("sees an unchanged copy, spaces and empty placements aside", () => {
    expect(sameStructure(original, { lines: lines.map((l) => ({ ...l, caption: ` ${l.caption} ` })), placement: { y: "P", x: "A", z: "" } })).toBe(true);
  });

  it("sees a change of concept, order, parent, sign or placement", () => {
    expect(sameStructure(original, { lines: [lines[1]!, lines[0]!], placement: original.placement })).toBe(false);
    expect(sameStructure(original, { lines: [{ ...lines[0]!, sign: -1 }, lines[1]!], placement: original.placement })).toBe(false);
    expect(sameStructure(original, { lines, placement: { x: "P", y: "P" } })).toBe(false);
    expect(sameStructure(original, { lines: [...lines, { lineCode: "K", caption: "Patrimonio", parentLineCode: "", sign: -1 }], placement: original.placement })).toBe(false);
  });

  it("names lines by their concept and dates by status", () => {
    expect(lineOptionLabel("K", "Patrimonio")).toBe("Patrimonio (K)");
    expect(lineOptionLabel("K", " ")).toBe("K");
    expect(effectiveFromLabel("DRAFT")).toBe("Regirá desde (al aprobarse)");
    expect(effectiveFromLabel("ACTIVE")).toBe("Vigente desde");
    expect(effectiveFromLabel("SUPERSEDED")).toBe("Rigió desde");
  });
});

describe("opening inventory (A-18)", () => {
  it("builds a template with the parser's header", () => {
    expect(openingTemplateCsv("P01", "PATIO")).toBe("planta,ubicacion,producto,cantidad,documento\r\nP01,PATIO,CODIGO-PRODUCTO,1000,CONTEO-0001\r\n");
    expect(openingTemplateCsv()).toContain("CODIGO-PLANTA,CODIGO-UBICACION");
  });

  it("says what is missing on the button", () => {
    expect(openingButtonLabel(openingMissing(false, ""))).toBe("Faltan el archivo CSV y la fecha de corte");
    expect(openingButtonLabel(openingMissing(true, ""))).toBe("Falta la fecha de corte");
    expect(openingButtonLabel(openingMissing(true, "2026-09-30"))).toBe("Preparar apertura");
  });
});
