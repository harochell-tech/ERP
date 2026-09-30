import { describe, expect, it } from "vitest";
import { formatDecimal, formatQuantity } from "@/lib/decimal";
import { environmentBadge } from "@/lib/environment";
import { formatDate, formatDateTime } from "@/lib/labels";
import { documentNumber, successText } from "@/lib/notices";
import { plantLabel } from "@/lib/plants";
import { screenTitle } from "@/components/Shell";

// UX1-01b (E-UX1-01-4/5/9/10): the shared helpers of the wave-1 screens.

describe("one date format (E-UX1-01-5)", () => {
  it("shows dates as dd/mm/aaaa", () => {
    expect([formatDate("2026-09-29"), formatDate("2026-01-05"), formatDate(null), formatDate("")]).toEqual(["29/09/2026", "05/01/2026", "—", "—"]);
  });

  it("shows date-times as dd/mm/aaaa hh:mm a. m./p. m. in Dominican time", () => {
    // 03:15 UTC on 30/09 is 23:15 on 29/09 in Santo Domingo (UTC−4, no daylight saving).
    expect(formatDateTime("2026-09-30T03:15:00Z")).toBe("29/09/2026 11:15 p. m.");
    expect(formatDateTime("2026-09-29T13:05:00Z")).toBe("29/09/2026 09:05 a. m.");
    expect(formatDateTime("2026-09-29T16:00:00Z")).toBe("29/09/2026 12:00 p. m.");
    expect(formatDateTime("2026-09-29T04:30:00Z")).toBe("29/09/2026 12:30 a. m.");
    expect(formatDateTime(null)).toBe("—");
    expect(formatDateTime("not a date")).toBe("not a date");
  });

  it("gives a date-time value its Dominican calendar day", () => {
    expect(formatDate("2026-09-30T03:15:00Z")).toBe("29/09/2026");
  });
});

describe("quantities without trailing zeros (E-UX1-01-5)", () => {
  it("drops only zeros, by string manipulation", () => {
    expect([formatQuantity("20.000000"), formatQuantity("1.450000"), formatQuantity("12000.500000"), formatQuantity("0.000001"), formatQuantity("-3.000000")]).toEqual([
      "20",
      "1.45",
      "12,000.5",
      "0.000001",
      "-3",
    ]);
  });

  it("keeps money at two decimals", () => {
    expect([formatDecimal("5000.0000"), formatDecimal("0.125")]).toEqual(["5,000.00", "0.125"]);
  });
});

describe("plants by name (E-UX1-01-4)", () => {
  const plants = [
    { plantId: "p1", code: "P1A8F47", name: "Planta Higüey" },
    { plantId: "p2", code: "P2", name: null },
    { plantId: "p3", code: "P3", name: "  " },
  ];

  it("reads 'Name (CODE)' by id or by code", () => {
    expect(plantLabel(plants, "p1")).toBe("Planta Higüey (P1A8F47)");
    expect(plantLabel(plants, "P1A8F47")).toBe("Planta Higüey (P1A8F47)");
  });

  it("falls back to the code, then to the given fallback or the key", () => {
    expect(plantLabel(plants, "p2")).toBe("P2");
    expect(plantLabel(plants, "p3")).toBe("P3");
    expect(plantLabel(plants, "unknown-id", "PX")).toBe("PX");
    expect(plantLabel(plants, "PZ")).toBe("PZ");
    expect(plantLabel(undefined, "P9")).toBe("P9");
    expect(plantLabel(plants, null)).toBe("—");
  });
});

describe("environment badge (E-UX1-01-10)", () => {
  it("names staging and local hosts, and nothing in production", () => {
    expect(environmentBadge("staging.industriasrochell.com.do")).toEqual({ label: "STAGING", tone: "staging" });
    expect(environmentBadge("localhost")).toEqual({ label: "PRUEBA", tone: "test" });
    expect(environmentBadge("127.0.0.1")).toEqual({ label: "PRUEBA", tone: "test" });
    expect(environmentBadge("sistema.industriasrochell.com.do")).toBeNull();
  });
});

describe("success notices (E-UX1-01-9)", () => {
  const response = (result: unknown) => ({ commandId: "c", resultRef: "r", replayed: false, result: result as never });

  it("finds the document number of a command result", () => {
    expect(documentNumber({ salesOrderId: "x", orderNo: "PV-000012", status: "DRAFT" })).toBe("PV-000012");
    expect(documentNumber({ lineNo: "1", invoiceNo: "FA-000001" })).toBe("FA-000001");
    expect(documentNumber({ status: "POSTED" })).toBeNull();
    expect(documentNumber(null)).toBeNull();
    expect(documentNumber(["PV-1"])).toBeNull();
  });

  it("uses the caller's text, built with the document number when it is a function", () => {
    expect(successText("Pedido enviado.", response({ orderNo: "PV-1" }))).toBe("Pedido enviado.");
    expect(successText((_, doc) => `Pedido ${doc} enviado a crédito.`, response({ orderNo: "PV-000012" }))).toBe("Pedido PV-000012 enviado a crédito.");
    expect(successText(undefined, response({ orderNo: "PV-000012" }))).toBe("Listo: PV-000012 guardado.");
    expect(successText(undefined, response({}))).toBe("Listo: operación completada.");
  });
});

describe("mobile top bar title (E-UX1-01-1)", () => {
  it("names the screen by its menu item, detail pages by their list", () => {
    expect(screenTitle("/")).toBe("Inicio");
    expect(screenTitle("/ventas/pedidos/")).toBe("Pedidos");
    expect(screenTitle("/ventas/pedido/")).toBe("Pedidos");
    expect(screenTitle("/tesoreria/pago/")).toBe("Pagos");
    expect(screenTitle("/nowhere/")).toBe("Rochell Core");
  });
});
