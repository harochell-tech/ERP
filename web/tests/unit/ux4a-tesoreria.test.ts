import { describe, expect, it } from "vitest";
import { activeShortcut, ALL_DUE, dueUntilFor, laterDueText, paymentsCountText, reconcilingRows, statementLinesText } from "@/lib/ux4a-tesoreria";

// UX4-02 (C-26, C-28, C-29, C-31): the treasury screens' pure helpers.

describe("payment proposal due date (C-26, E-UX4-12)", () => {
  it("opens with today + 7 and offers 15 days and everything", () => {
    expect(dueUntilFor("week", "2026-09-30")).toBe("2026-10-07");
    expect(dueUntilFor("fortnight", "2026-09-30")).toBe("2026-10-15");
    expect(dueUntilFor("all", "2026-09-30")).toBe(ALL_DUE);
  });

  it("knows which shortcut a date is, or none for a typed date", () => {
    expect(activeShortcut("2026-10-07", "2026-09-30")).toBe("week");
    expect(activeShortcut(ALL_DUE, "2026-09-30")).toBe("all");
    expect(activeShortcut("2026-10-01", "2026-09-30")).toBeNull();
  });

  it("says how much falls due later", () => {
    expect(laterDueText([])).toBeNull();
    expect(laterDueText([{ invoices: [1] }])).toBe("1 factura de 1 proveedor vence después de esa fecha.");
    expect(laterDueText([{ invoices: [1, 2] }, { invoices: [3] }])).toBe("3 facturas de 2 proveedores vencen después de esa fecha.");
  });
});

describe("statements and payments (C-28, C-31)", () => {
  it("reads a statement's lines in words", () => {
    expect(statementLinesText(0, 0)).toBe("Sin líneas");
    expect(statementLinesText(2, 0)).toBe("2 líneas, ninguna pendiente");
    expect(statementLinesText(1, 0)).toBe("1 línea, ninguna pendiente");
    expect(statementLinesText(2, 1)).toBe("1 de 2 pendiente de conciliar");
    expect(statementLinesText(3, 2)).toBe("2 de 3 pendientes de conciliar");
  });

  it("counts payments", () => {
    expect(paymentsCountText(1)).toBe("1 pago");
    expect(paymentsCountText(0)).toBe("0 pagos");
    expect(paymentsCountText(3)).toBe("3 pagos");
  });
});

describe("bank reconciliation (C-29)", () => {
  it("goes from the statement to the books with the server's totals, untouched", () => {
    // GL = statement + GL items − line items + difference: -10,620.00 = -10,770.00 + 0.00 − (-150.00) + 0.00 (an unrecorded charge).
    const rows = reconcilingRows({ glBalance: "-10620.00", statementBalance: "-10770.00", difference: "0.00", glItemsTotal: "0.00", lineItemsTotal: "-150.00" });
    expect(rows.map((r) => [r.sign, r.value])).toEqual([
      ["", "-10770.00"],
      ["+", "0.00"],
      ["−", "-150.00"],
      ["+", "0.00"],
      ["=", "-10620.00"],
    ]);
    expect(rows[3]?.label).toBe("Diferencia sin explicar");
  });
});
