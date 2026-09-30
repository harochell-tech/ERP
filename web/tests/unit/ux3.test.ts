import { describe, expect, it } from "vitest";
import { blockingCodes, closeAvailability, groupPeriodsByMonth, monthLabel, readinessChecklist } from "@/lib/close";
import { commandLabel, documentHref, documentKindLabel, eventTypeLabel, integrityLabel, journalTypeLabel, ruleLabel } from "@/lib/explain";
import { paymentStatusLabel, paymentStatusTone } from "@/lib/payables";
import { deliveryWatermark, quoteWatermark } from "@/lib/print";
import { defaultLotFilter, isReadyToRelease, lotActions, lotQueryStatus, READY_TO_RELEASE } from "@/lib/production";
import { canReceiveMore, prefillQuantity } from "@/lib/receiving";
import { classificationText, exceptionKey, severityLabel, severityTone } from "@/lib/reconciliations";
import { creditNoteReasonLabel } from "@/lib/sales";
import { isOwnUserId } from "@/lib/scope";
import { uomCatalogueOptions, uomOptions } from "@/lib/units";

// UX3-02 (E-UX3-1…13): the pure helpers of the critical-flow screens.

const holding =
  (...permissions: string[]) =>
  (permission: string) =>
    permissions.includes(permission);

describe("guided close (E-UX3-1)", () => {
  it("groups the periods by month in calendar order, in Spanish", () => {
    const groups = groupPeriodsByMonth([{ startsOn: "2026-09-01" }, { startsOn: "2026-01-01" }, { startsOn: "2026-08-01" }]);
    expect(groups.map((g) => [g.key, g.label])).toEqual([
      ["2026-01", "Enero 2026"],
      ["2026-08", "Agosto 2026"],
      ["2026-09", "Septiembre 2026"],
    ]);
    expect(monthLabel("2026-12-01")).toBe("Diciembre 2026");
  });

  const component = {
    component: "ACR-NTX",
    status: "OPEN",
    ended: true,
    sealed: true,
    ready: false,
    reconciliations: [
      { reconCode: "TB-BALANCED", name: "Balanza cuadrada", runId: "r1", runStatus: "OK", runAt: "2026-09-30T12:00:00Z", blockingErrors: 0 },
      { reconCode: "MANUAL-EVIDENCE", name: "Evidencia de ajustes", runId: "r2", runStatus: "EXCEPTIONS", runAt: "2026-09-30T12:00:00Z", blockingErrors: 2 },
      { reconCode: "OTHER", name: "Sin correr", runId: null, runStatus: null, runAt: null, blockingErrors: null },
    ],
  };

  it("maps the readiness to a checklist: ended, sealed and each blocking reconciliation", () => {
    const items = readinessChecklist(component, () => "30/09/2026");
    expect(items.map((i) => [i.key, i.state])).toEqual([
      ["ended", "ok"],
      ["sealed", "ok"],
      ["TB-BALANCED", "ok"],
      ["MANUAL-EVIDENCE", "error"],
      ["OTHER", "pending"],
    ]);
    expect(items[3]?.detail).toBe("2 errores bloquean el cierre (verificada 30/09/2026).");
    expect(items[3]?.runId).toBe("r2");
    expect(items[4]?.detail).toContain("Verificar ahora");
    expect(readinessChecklist({ ...component, ended: false, sealed: false })[0]?.detail).toContain("Aún no termina");
    expect(blockingCodes(component)).toEqual(["TB-BALANCED", "MANUAL-EVIDENCE", "OTHER"]);
  });

  it("offers Cerrar only for an ended month the server reports ready", () => {
    expect(closeAvailability("CLOSED", component)).toBe("closed");
    expect(closeAvailability("OPEN", { ended: false, ready: false })).toBe("not-ended");
    expect(closeAvailability("OPEN", { ended: true, ready: false })).toBe("blocked");
    expect(closeAvailability("REOPENED", { ended: true, ready: true })).toBe("ready");
    expect(closeAvailability("OPEN", undefined)).toBe("blocked");
  });
});

describe("reconciliations in words (E-UX3-2/3)", () => {
  it("names the severities", () => {
    expect(severityLabel("ERROR")).toBe("Error");
    expect(severityLabel("WARNING")).toBe("Aviso");
    expect(severityTone("ERROR")).toBe("error");
    expect(severityTone("WARNING")).toBe("attention");
  });

  it("shows the readable key, falling back to the raw one", () => {
    expect(exceptionKey({ matchKey: "0192…", matchLabel: "Proveedor Uno, S.R.L." })).toBe("Proveedor Uno, S.R.L.");
    expect(exceptionKey({ matchKey: "PAY:PAG-000001", matchLabel: null })).toBe("PAY:PAG-000001");
    expect(exceptionKey({ matchKey: "k", matchLabel: " " })).toBe("k");
    expect(classificationText({ classification: "AMOUNT_DIFFERENCE", classificationName: "Montos distintos" })).toBe("Montos distintos");
    expect(classificationText({ classification: "NEW_ONE", classificationName: null })).toBe("NEW_ONE");
  });
});

describe("Explain in words (E-UX3-4)", () => {
  it("names the codes and keeps unknown ones", () => {
    expect(journalTypeLabel("REVERSAL")).toBe("Reversa");
    expect(eventTypeLabel("GoodsReceiptPosted")).toBe("Recepción de mercancía contabilizada");
    expect(eventTypeLabel("SomethingNew")).toBe("SomethingNew");
    expect(commandLabel("Procurement.PostSupplierInvoice")).toBe("Contabilizar factura de proveedor");
    expect(commandLabel(null)).toBe("—");
    expect(ruleLabel("R-01")).toBe("R-01 · Recepción de mercancía");
    expect(ruleLabel("X-99")).toBe("X-99");
    expect(integrityLabel("SEALED")).toBe("Sellado en la cadena de integridad");
    expect(documentKindLabel("SUPPLIER_INVOICE")).toBe("Factura de proveedor (NCF)");
  });

  it("links the source document by kind and id", () => {
    expect(documentHref("GOODS_RECEIPT", "g1")).toBe("/almacen/recepcion/?id=g1");
    expect(documentHref("SUPPLIER_INVOICE", "s1")).toBe("/cxp/factura/?id=s1");
    expect(documentHref("RECEIPT_CORRECTION", "c1")).toBe("/almacen/correcciones/");
    expect(documentHref("UNKNOWN", "x")).toBeNull();
  });
});

describe("receiving (E-UX3-5)", () => {
  it("prefills the open quantity and offers Recibir while a line can take more", () => {
    expect(prefillQuantity("40.000000")).toBe("40");
    expect(prefillQuantity("1500.5")).toBe("1,500.5");
    expect(prefillQuantity("0.000000")).toBe("");
    expect(prefillQuantity(null)).toBe("");
    expect(canReceiveMore([{ maxReceivable: "0" }, { maxReceivable: "2.000000" }])).toBe(true);
    expect(canReceiveMore([{ maxReceivable: "0.000000" }])).toBe(false);
  });
});

describe("supplier invoice payment status (E-UX3-6)", () => {
  it("labels every status with its tone", () => {
    expect(["NOT_POSTED", "OPEN", "PARTIAL", "PAID", "VOIDED", "REVERSED"].map((s) => [paymentStatusLabel(s), paymentStatusTone(s)])).toEqual([
      ["Sin contabilizar", "neutral"],
      ["Pendiente de pago", "attention"],
      ["Pagada en parte", "progress"],
      ["Pagada", "done"],
      ["Anulada", "neutral"],
      ["Reversada", "reversed"],
    ]);
    expect(paymentStatusLabel("OTHER")).toBe("OTHER");
  });
});

describe("watermarks (E-UX3-7, E-UX3-10)", () => {
  it("marks a delivery note until the gate-out", () => {
    expect(deliveryWatermark(null)).toBe("BORRADOR – NO DESPACHADO");
    expect(deliveryWatermark("2026-09-30T14:00:00Z")).toBeNull();
  });

  it("marks a quote by status", () => {
    expect(quoteWatermark("DRAFT", false)).toBe("BORRADOR");
    expect(quoteWatermark("PENDING_APPROVAL", false)).toBe("BORRADOR");
    expect(quoteWatermark("SENT", false)).toBeNull();
    expect(quoteWatermark("SENT", true)).toBe("VENCIDA");
    expect(quoteWatermark("LOST", false)).toBe("PERDIDA");
    expect(quoteWatermark("CANCELLED", false)).toBe("CANCELADA");
    expect(quoteWatermark("CONVERTED", true)).toBeNull();
  });
});

describe("credit note (E-UX3-9)", () => {
  it("recognises the invoice's issuer, never for a superadministrator", () => {
    expect(isOwnUserId("u1", "u1", false)).toBe(true);
    expect(isOwnUserId("u1", "u1", true)).toBe(false);
    expect(isOwnUserId("u1", "u2", false)).toBe(false);
    expect(isOwnUserId(null, "u1", false)).toBe(false);
    expect(creditNoteReasonLabel("ERROR_DE_PRECIO")).toBe("Error de precio");
    expect(creditNoteReasonLabel("OTRO")).toBe("Otro");
  });
});

describe("units (E-UX3-11)", () => {
  it("offers the base unit and the units converted into it, without repeats", () => {
    const conversions = [
      { fromUom: "t", toUom: "kg" },
      { fromUom: "m3", toUom: "kg" },
      { fromUom: "l", toUom: "un" },
      { fromUom: "t", toUom: "kg" },
    ];
    expect(uomOptions("kg", conversions)).toEqual(["kg", "t", "m3"]);
    expect(uomOptions("kg", [], "t")).toEqual(["kg", "t"]);
    expect(uomOptions("kg")).toEqual(["kg"]);
  });

  it("lists the catalogue by code with its dimension", () => {
    expect(uomCatalogueOptions([{ code: "un", dimension: "COUNT" }, { code: "kg", dimension: "MASS" }, { code: "m3", dimension: "VOLUME" }])).toEqual([
      { value: "kg", label: "kg (masa)" },
      { value: "m3", label: "m3 (volumen)" },
      { value: "un", label: "un (unidades)" },
    ]);
  });
});

describe("lots (E-UX3-12)", () => {
  it("opens on Listos para liberar for Calidad and reads CURING for it", () => {
    expect(defaultLotFilter(true)).toBe(READY_TO_RELEASE);
    expect(defaultLotFilter(false)).toBe("");
    expect(lotQueryStatus(READY_TO_RELEASE)).toBe("CURING");
    expect(lotQueryStatus("BLOCKED")).toBe("BLOCKED");
    expect(isReadyToRelease({ status: "CURING", curingDone: true })).toBe(true);
    expect(isReadyToRelease({ status: "CURING", curingDone: false })).toBe(false);
    expect(isReadyToRelease({ status: "RELEASED", curingDone: true })).toBe(false);
  });

  it("offers the actions by status and permission", () => {
    expect(lotActions("CURING", holding("fg_lot:release"))).toEqual(["release", "block"]);
    expect(lotActions("BLOCKED", holding("fg_lot:release", "fg_lot:scrap"))).toEqual(["unblock", "scrap"]);
    expect(lotActions("RELEASED", holding("fg_lot:release"))).toEqual([]);
    expect(lotActions("RELEASED", holding("fg_lot:scrap"))).toEqual(["scrap"]);
    expect(lotActions("SCRAPPED", holding("fg_lot:release", "fg_lot:scrap"))).toEqual([]);
  });
});
