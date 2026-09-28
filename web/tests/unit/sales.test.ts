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
