import { describe, expect, it } from "vitest";
import { BOARD_COLUMNS, bytesToBase64, cancellable, nextDeliveryStep, orderCancellable, orderDispatchable } from "@/lib/sales";

describe("sales and dispatch helpers (VS3-10a)", () => {
  it("encodes a file as base64 without losing accents", () => {
    const bytes = new TextEncoder().encode("planta,ubicación\nHIGÜEY,PATIO\n");
    expect(atob(bytesToBase64(bytes.buffer as ArrayBuffer))).toBe(String.fromCharCode(...bytes));
    expect(bytesToBase64(new Uint8Array([]).buffer)).toBe("");
  });

  it("offers the next dispatch step of each status", () => {
    expect(["PLANNED", "LOADING", "LOADED", "IN_TRANSIT", "DELIVERED", "CANCELLED"].map(nextDeliveryStep)).toEqual([
      "START_LOADING",
      "CONFIRM_LOADED",
      "GATE_OUT",
      "POD",
      null,
      null,
    ]);
    expect(BOARD_COLUMNS.map((c) => c.status)).toEqual(["PLANNED", "LOADING", "LOADED", "IN_TRANSIT"]);
  });

  it("cancels deliveries only before the gate and orders only before delivery", () => {
    expect(["PLANNED", "LOADED", "IN_TRANSIT"].map(cancellable)).toEqual([true, true, false]);
    expect(["DRAFT", "CONFIRMED", "PARTIALLY_DELIVERED"].map(orderCancellable)).toEqual([true, true, false]);
    expect(["CONFIRMED", "PARTIALLY_DELIVERED", "DELIVERED"].map(orderDispatchable)).toEqual([true, true, false]);
  });
});

describe("receipt helpers (VS3-10b)", () => {
  it("groups the live applications by the command that made them", async () => {
    const { applicationGroups } = await import("@/lib/sales");
    const groups = applicationGroups([
      { eventId: "e1", live: true, reversesApplicationId: null, invoiceNo: "FA-1" },
      { eventId: "e1", live: true, reversesApplicationId: null, invoiceNo: "FA-2" },
      { eventId: "e2", live: false, reversesApplicationId: null, invoiceNo: "FA-3" },
      { eventId: "e3", live: false, reversesApplicationId: "x", invoiceNo: "FA-3" },
    ]);
    expect(groups.map((g) => `${g.eventId}:${g.items.map((i) => i.invoiceNo).join(",")}`)).toEqual(["e1:FA-1,FA-2"]);
  });
});
