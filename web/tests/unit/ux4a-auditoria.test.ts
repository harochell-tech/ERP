import { describe, expect, it } from "vitest";
import {
  ecfTypeLabel,
  expiryText,
  expiryTone,
  journalSearchText,
  journalsTitle,
  ledgerLabel,
  monthValueToPeriod,
  periodLabel,
  shortHash,
  sideLabels,
  sourceEnvironmentHint,
  sourceEnvironmentLabel,
} from "@/lib/ux4a-auditoria";

// UX4-02 (E-UX4-1, 2, 14, 15): the helpers of the Cierre, Auditoría and Fiscal screens.

describe("reconciliation runs (A-21, A-22)", () => {
  it("labels the totals with the reconciliation's own sides, else Total A / Total B", () => {
    expect(sideLabels({ sideALabel: "Saldo del mayor", sideBLabel: "Extracto más partidas en tránsito" })).toEqual({
      a: "Saldo del mayor",
      b: "Extracto más partidas en tránsito",
    });
    expect(sideLabels({ sideALabel: null, sideBLabel: " " })).toEqual({ a: "Total A", b: "Total B" });
  });
});

describe("journals (A-07, A-08, A-09)", () => {
  it("names the page after the document", () => {
    expect(journalsTitle("GoodsReceipt", "RM-2026-000001")).toBe("Asientos de la recepción RM-2026-000001");
    expect(journalsTitle("Payment", "PAG-000001")).toBe("Asientos del pago PAG-000001");
    expect(journalsTitle("Unknown", "X-1")).toBe("Asientos del documento X-1");
    expect(journalsTitle(null, null)).toBe("Asientos del documento");
    expect(journalsTitle("SupplierInvoice", null)).toBe("Asientos de la factura de proveedor");
  });

  it("sends a search of 1 to 100 characters, trimmed", () => {
    expect(journalSearchText("  RM-2026-000001 ")).toBe("RM-2026-000001");
    expect(journalSearchText("   ")).toBeNull();
    expect(journalSearchText("x".repeat(101))).toBeNull();
  });

  it("names the ledgers and shortens hashes", () => {
    expect(ledgerLabel("GL")).toBe("Libro mayor");
    expect(ledgerLabel("OTHER")).toBe("OTHER");
    expect(shortHash("0123456789abcdef")).toBe("01234567…");
    expect(shortHash("abc")).toBe("abc");
    expect(shortHash(null)).toBe("—");
  });
});

describe("fiscal screens (G-20, G-24, G-25, G-26)", () => {
  it("explains TEST and official sources (P-7)", () => {
    expect(sourceEnvironmentLabel("PRODUCTION")).toBe("Oficial");
    expect(sourceEnvironmentLabel("TEST")).toBe("Prueba");
    expect(sourceEnvironmentHint("TEST")).toContain("nunca activa");
  });

  it("names the e-CF types", () => {
    expect(ecfTypeLabel("31")).toBe("31 — Crédito fiscal");
    expect(ecfTypeLabel("44")).toBe("44 — Regímenes especiales");
    expect(ecfTypeLabel("99")).toBe("99");
    expect(ecfTypeLabel(null)).toBe("—");
  });

  it("turns the month picker's value into AAAAMM and names the period", () => {
    expect(monthValueToPeriod("2026-09")).toBe("202609");
    expect(monthValueToPeriod("2026-13")).toBeNull();
    expect(monthValueToPeriod("")).toBeNull();
    expect(periodLabel("202609")).toBe("septiembre 2026");
    expect(periodLabel("2026")).toBe("2026");
  });

  it("says how long an authorization has left from the server's days", () => {
    expect(expiryText(null)).toBeNull();
    expect(expiryText(0)).toBe("Vence hoy");
    expect(expiryText(1)).toBe("Vence mañana");
    expect(expiryText(12)).toBe("Vence en 12 días");
    expect(expiryText(-1)).toBe("Venció ayer");
    expect(expiryText(-5)).toBe("Venció hace 5 días");
    expect(expiryTone(30)).toBe("attention");
    expect(expiryTone(31)).toBe("neutral");
    expect(expiryTone(null)).toBe("neutral");
  });
});
