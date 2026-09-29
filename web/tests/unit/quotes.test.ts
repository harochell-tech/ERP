import { describe, expect, it } from "vitest";
import { ERROR_MESSAGES } from "@/lib/errors";
import { statusLabel, statusTone } from "@/lib/labels";
import { compareDecimals, isSpecialPrice, QUOTE_STATUSES, quoteActions, quoteStatusLabel } from "@/lib/quotes";

// QUO1-04 (E-QUO1-04-1…10): the sales quotation screens' helpers.
const holding =
  (...permissions: string[]) =>
  (permission: string) =>
    permissions.includes(permission);

describe("exact comparison of decimal strings", () => {
  it("compares without floating point, whatever the scale", () => {
    expect(compareDecimals("45.00", "50.0000")).toBe(-1);
    expect(compareDecimals("50", "50.0000")).toBe(0);
    expect(compareDecimals("050.10", "50.1")).toBe(0);
    expect(compareDecimals("49.9999", "50")).toBe(-1);
    expect(compareDecimals("50.0001", "50")).toBe(1);
    expect(compareDecimals("100", "99.9999")).toBe(1);
    expect(compareDecimals("9", "10")).toBe(-1);
    expect(compareDecimals("0.3", "0.29999999999999999999")).toBe(1);
    expect(compareDecimals("12345678901234567890.01", "12345678901234567890.1")).toBe(-1);
  });

  it("handles signs, zero and invalid input", () => {
    expect(compareDecimals("-0", "0.00")).toBe(0);
    expect(compareDecimals("-1", "0")).toBe(-1);
    expect(compareDecimals("-2", "-10")).toBe(1);
    expect(compareDecimals("1", "-1")).toBe(1);
    expect(compareDecimals("1,000", "1")).toBeNull();
    expect(compareDecimals("", "1")).toBeNull();
    expect(compareDecimals("1.", "1")).toBeNull();
  });

  it("flags a special price only below the list", () => {
    expect(isSpecialPrice("45.00", "50.0000")).toBe(true);
    expect(isSpecialPrice("50", "50.0000")).toBe(false);
    expect(isSpecialPrice("55", "50.0000")).toBe(false);
    expect(isSpecialPrice("", "50.0000")).toBe(false);
    expect(isSpecialPrice("abc", "50.0000")).toBe(false);
    expect(isSpecialPrice("45", undefined)).toBe(false);
  });
});

describe("quote actions", () => {
  const seller = holding("quote:manage", "sales:read");
  const approver = holding("quote:approve_price", "sales:read");

  it("lets the Vendedor edit a draft and send it, or submit it when a special price is not approved", () => {
    expect(quoteActions("DRAFT", false, false, seller)).toEqual(["EDIT", "SEND", "CANCEL", "COPY"]);
    expect(quoteActions("DRAFT", false, true, seller)).toEqual(["EDIT", "SUBMIT", "CANCEL", "COPY"]);
    expect(quoteActions("DRAFT", false, true, approver)).toEqual([]);
  });

  it("lets only the Aprobador de políticas approve or return a pending quote", () => {
    expect(quoteActions("PENDING_APPROVAL", false, true, approver)).toEqual(["APPROVE", "RETURN"]);
    expect(quoteActions("PENDING_APPROVAL", false, true, seller)).toEqual(["COPY"]);
  });

  it("converts a sent quote only while it is valid", () => {
    expect(quoteActions("SENT", false, false, seller)).toEqual(["CONVERT", "LOST", "CANCEL", "COPY"]);
    expect(quoteActions("SENT", true, false, seller)).toEqual(["LOST", "CANCEL", "COPY"]);
    expect(quoteActions("SENT", false, false, approver)).toEqual([]);
  });

  it("only copies a closed quote", () => {
    for (const status of ["CONVERTED", "LOST", "CANCELLED"]) {
      expect(quoteActions(status, false, false, seller)).toEqual(["COPY"]);
    }
  });
});

describe("quote labels and errors", () => {
  it("names the six statuses and the derived Vencida", () => {
    expect(QUOTE_STATUSES.map((s) => quoteStatusLabel(s))).toEqual(["Borrador", "Pendiente de aprobación", "Enviada", "Convertida en pedido", "Perdida", "Cancelada"]);
    expect(quoteStatusLabel("SENT", true)).toBe("Vencida");
    expect(quoteStatusLabel("CONVERTED", true)).toBe("Convertida en pedido");
    expect(["SENT", "CONVERTED", "LOST"].map(statusLabel)).toEqual(["Enviada", "Convertida en pedido", "Perdida"]);
    expect(QUOTE_STATUSES.map(statusTone)).toEqual(["neutral", "progress", "progress", "done", "neutral", "neutral"]);
  });

  it("explains every QUOTE_* error in Spanish", () => {
    const codes = [
      "QUOTE_NOT_FOUND",
      "QUOTE_INVALID_STATE",
      "QUOTE_VERSION_CONFLICT",
      "QUOTE_VALIDITY_INVALID",
      "QUOTE_NOTHING_TO_APPROVE",
      "QUOTE_PRICE_APPROVAL_REQUIRED",
      "QUOTE_SAME_PERSON",
      "QUOTE_REASON_REQUIRED",
      "QUOTE_EXPIRED",
    ];
    expect(codes.filter((c) => !ERROR_MESSAGES[c])).toEqual([]);
  });
});
