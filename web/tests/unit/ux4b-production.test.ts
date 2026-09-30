import { describe, expect, it } from "vitest";
import { lotActions } from "@/lib/production";
import {
  compareRecipeLines,
  compareRecipeParameters,
  curingHoursErrors,
  curingRemainingText,
  dayFromQuery,
  dayHref,
  itemDisplay,
  lotHref,
  monthEnded,
  monthName,
  previousMonth,
  previousRecipeVersion,
  productionStatus,
  quantityWithUnit,
  runNextStep,
  signedPercent,
  signedQuantity,
  toleranceText,
  uomLabel,
  varianceMark,
} from "@/lib/ux4bProduction";

const holding =
  (...permissions: string[]) =>
  (p: string) =>
    permissions.includes(p);

describe("UX4-03 production: units and labels (P-05, P-06, P-37)", () => {
  it("writes the litre as L and keeps other units", () => {
    expect(uomLabel("l")).toBe("L");
    expect(uomLabel("t")).toBe("t");
    expect(uomLabel(null)).toBe("");
  });

  it("puts the unit beside the quantity, trailing zeros dropped", () => {
    expect(quantityWithUnit("15.000000", "l")).toBe("15 L");
    expect(quantityWithUnit("1480", "un")).toBe("1,480 un");
    expect(quantityWithUnit(null, "t")).toBe("—");
  });

  it("does not repeat the code when the description is the code", () => {
    expect(itemDisplay("ADITIVO-P", "ADITIVO-P", "l")).toBe("ADITIVO-P (L)");
    expect(itemDisplay("ADOQUIN-H", "Adoquín holandés", "un")).toBe("ADOQUIN-H — Adoquín holandés (un)");
    expect(itemDisplay("X", "", null)).toBe("X");
  });
});

describe("UX4-03 production: variance (P-16)", () => {
  it("signs the server's difference without computing it", () => {
    expect(signedQuantity("0.350000")).toBe("+0.35");
    expect(signedQuantity("-0.2")).toBe("-0.2");
    expect(signedQuantity("0.000000")).toBe("0");
    expect(signedPercent("2.78")).toBe("+2.78 %");
    expect(signedPercent("-1.50")).toBe("-1.5 %");
    expect(signedPercent(null)).toBe("—");
  });

  it("reads the tolerance fraction as ± percent, none without policy", () => {
    expect(toleranceText("0.05")).toBe("±5 %");
    expect(toleranceText("0.025")).toBe("±2.5 %");
    expect(toleranceText(null)).toBeNull();
  });

  it("marks out-of-tolerance only when the server says so", () => {
    expect(varianceMark(true)).toEqual({ label: "Fuera de tolerancia", tone: "attention" });
    expect(varianceMark(false)).toEqual({ label: "Dentro de tolerancia", tone: "done" });
    expect(varianceMark(null)).toBeNull();
  });
});

describe("UX4-03 production: statuses (P-12, E-UX4-14)", () => {
  it("shows a released lot as done and a recipe in the feminine", () => {
    expect(productionStatus("lot", "RELEASED")).toEqual({ label: "Liberado", tone: "done" });
    expect(productionStatus("recipe", "ACTIVE")).toEqual({ label: "Activa", tone: "done" });
    expect(productionStatus("summary", "POSTED")).toEqual({ label: "Cerrado", tone: "done" });
    expect(productionStatus("lot", "UNKNOWN")).toBeNull();
  });
});

describe("UX4-03 production: curing and lots (P-26, P-29, P-17, P-32)", () => {
  it("says the remaining curing hours from the server's whole hours", () => {
    expect(curingRemainingText("CURING", 5)).toBe("Faltan 5 horas de curado");
    expect(curingRemainingText("CURING", 1)).toBe("Falta 1 hora de curado");
    expect(curingRemainingText("CURING", 0)).toBe("Curado cumplido: se puede liberar");
    expect(curingRemainingText("RELEASED", 0)).toBeNull();
  });

  it("offers Liberar only once the curing is done", () => {
    const quality = holding("fg_lot:release");
    expect(lotActions("CURING", quality, false)).toEqual(["block"]);
    expect(lotActions("CURING", quality, true)).toEqual(["release", "block"]);
  });

  it("links a lot and a day", () => {
    expect(lotHref("L-0001")).toBe("/produccion/lotes/?lote=L-0001");
    expect(dayHref("2026-09-29")).toBe("/produccion/dia/?dia=2026-09-29");
    expect(dayFromQuery("2026-09-29")).toBe("2026-09-29");
    expect(dayFromQuery("ayer")).toBeNull();
    expect(dayFromQuery(null)).toBeNull();
  });
});

describe("UX4-03 production: next step per run (P-14)", () => {
  const run = { runId: "r1", status: "IN_PROGRESS", summaryStatus: null, lotCode: null, lotStatus: null };
  it("asks the supervisor to record the summary and the manager to close it", () => {
    expect(runNextStep(run, holding("shift_summary:record"))).toEqual({ label: "Registrar resumen", href: "/produccion/corrida/?id=r1", primary: true });
    expect(runNextStep({ ...run, summaryStatus: "DRAFT" }, holding("shift_summary:post"))?.label).toBe("Cerrar resumen del turno");
    expect(runNextStep({ ...run, summaryStatus: "DRAFT" }, holding("shift_summary:record"))?.label).toBe("Revisar resumen");
    expect(runNextStep(run, holding())?.label).toBe("Ver corrida");
  });

  it("sends Calidad to the lot in curing", () => {
    const done = { ...run, status: "COMPLETED", summaryStatus: "POSTED", lotCode: "L-1", lotStatus: "CURING" };
    expect(runNextStep(done, holding("fg_lot:release"))).toEqual({ label: "Ver lote en curado", href: "/produccion/lotes/?lote=L-1", primary: false });
    expect(runNextStep(done, holding())?.label).toBe("Ver corrida");
  });
});

describe("UX4-03 production: cost months (P-40, P-41)", () => {
  it("opens on the previous month", () => {
    expect(previousMonth("2026-09-30")).toBe("2026-08");
    expect(previousMonth("2026-01-15")).toBe("2025-12");
  });

  it("settles only ended months", () => {
    expect(monthEnded("2026-08-01", "2026-09-30")).toBe(true);
    expect(monthEnded("2026-09-01", "2026-09-30")).toBe(false);
    expect(monthName("2026-08-01")).toBe("agosto 2026");
  });
});

describe("UX4-03 production: recipes (P-34, P-36, E-UX4-9)", () => {
  it("requires at least one curing hour and a larger maximum", () => {
    expect(curingHoursErrors(0, 168).min).toBe("El curado mínimo es de al menos 1 hora.");
    expect(curingHoursErrors(1, 1).max).toBe("El máximo debe ser mayor que el mínimo.");
    expect(curingHoursErrors(1, 168)).toEqual({ min: null, max: null });
    expect(curingHoursErrors(null, null).min).toBe("Horas en número entero.");
  });

  it("compares the materials by text: changed, added and removed", () => {
    const line = (id: string, qty: string) => ({ materialItemId: id, materialCode: id.toUpperCase(), baseUom: "t", qtyPerBatch: qty });
    const rows = compareRecipeLines([line("a", "0.180000"), line("b", "2"), line("c", "1")], [line("a", "0.18"), line("b", "1.8"), line("d", "5")]);
    expect(rows.map((r) => [r.materialCode, r.previous, r.current, r.changed])).toEqual([
      ["A", "0.18", "0.180000", false],
      ["B", "1.8", "2", true],
      ["C", null, "1", true],
      ["D", "5", null, true],
    ]);
  });

  it("compares the parameters and finds the previous version", () => {
    const base = { unitsPerBatch: "150", unitsPerCycle: "6", unitsPerRack: "600", minCuringHours: 1, maxCuringHours: 168 };
    const rows = compareRecipeParameters({ ...base, unitsPerBatch: "160.000000", minCuringHours: 8 }, base);
    expect(rows.filter((r) => r.changed).map((r) => [r.label, r.previous, r.current])).toEqual([
      ["Unidades por tanda", "150", "160"],
      ["Curado mínimo", "1 h", "8 h"],
    ]);
    const versions = [
      { itemId: "i", machineId: "m", version: 1 },
      { itemId: "i", machineId: "m", version: 3 },
      { itemId: "i", machineId: "x", version: 2 },
    ];
    expect(previousRecipeVersion(versions, { itemId: "i", machineId: "m", version: 3 })?.version).toBe(1);
    expect(previousRecipeVersion(versions, { itemId: "i", machineId: "m", version: 1 })).toBeUndefined();
  });
});
