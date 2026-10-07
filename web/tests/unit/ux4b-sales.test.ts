import { describe, expect, it } from "vitest";
import {
  absDecimal,
  bankAccountLabel,
  canSeeAccounting,
  creditNoteOffered,
  creditNoteStatusLabel,
  creditPreviewSummary,
  creditReasonLabel,
  deliveryLotLabel,
  filterAging,
  hasOverdue,
  invoiceStatusLabel,
  isNegativeDecimal,
  isOpenDelivery,
  isZeroDecimal,
  pendingWithoutArithmetic,
  previewableLines,
  proformaWatermark,
  receiptStatusSummary,
  splitHistory,
  suggestionAmounts,
} from "@/lib/ux4bSales";

// UX4-03: the pure helpers of the sales, dispatch, billing and collection screens.

const holding =
  (...permissions: string[]) =>
  (permission: string) =>
    permissions.includes(permission);

describe("decimal strings without arithmetic", () => {
  it("recognises zero, negatives and drops the sign", () => {
    expect(isZeroDecimal("0")).toBe(true);
    expect(isZeroDecimal("0.000000")).toBe(true);
    expect(isZeroDecimal("-0.00")).toBe(true);
    expect(isZeroDecimal("0.01")).toBe(false);
    expect(isZeroDecimal("")).toBe(false);
    expect(isZeroDecimal(null)).toBe(false);
    expect(isNegativeDecimal("-150.00")).toBe(true);
    expect(isNegativeDecimal("-0.00")).toBe(false);
    expect(isNegativeDecimal("150.00")).toBe(false);
    expect(absDecimal("-150.00")).toBe("150.00");
    expect(absDecimal("150.00")).toBe("150.00");
  });
});

describe("accounting visibility (E-UX4-11)", () => {
  it("shows the books' matters to configuration:read or ledger:read only", () => {
    expect(canSeeAccounting(holding("configuration:read"))).toBe(true);
    expect(canSeeAccounting(holding("ledger:read"))).toBe(true);
    expect(canSeeAccounting(holding("sales:read", "invoice:issue", "audit:read"))).toBe(false);
  });
});

describe("document status labels (V-06)", () => {
  it("reads an invoice and a credit note in their own words", () => {
    expect(invoiceStatusLabel("CONFIRMED")).toBe("Emitida");
    expect(invoiceStatusLabel("PARTIALLY_PAID")).toBe("Cobrada en parte");
    expect(invoiceStatusLabel("PAID")).toBe("Cobrada");
    expect(invoiceStatusLabel("VOIDED")).toBe("Anulada");
    expect(invoiceStatusLabel("SOMETHING_NEW")).toBe("SOMETHING_NEW");
    expect(creditNoteStatusLabel("CONFIRMED")).toBe("Emitida");
    expect(creditNoteStatusLabel("DRAFT")).toBe("Borrador");
  });
});

describe("bank account label (V-35)", () => {
  it("puts the alias first and keeps the masked number", () => {
    expect(bankAccountLabel({ alias: "Cobros BHD", bankCode: "BHD", accountNumber: "••••6789" })).toBe("Cobros BHD · BHD ••••6789");
    expect(bankAccountLabel({ alias: null, bankCode: "TEST_BANK", accountNumber: "••••4321" })).toBe("TEST_BANK ••••4321");
    expect(bankAccountLabel({ alias: "  ", bankCode: "TEST_BANK", accountNumber: "••••4321" })).toBe("TEST_BANK ••••4321");
    expect(bankAccountLabel({})).toBe("—");
  });
});

describe("receipt status in one column (V-36)", () => {
  it("combines the three statuses", () => {
    expect(receiptStatusSummary({ status: "REVERSED", applicationStatus: "UNAPPLIED", bankStatus: "DEPOSITED", method: "TRANSFER" })).toEqual({ label: "Anulado", tone: "reversed" });
    expect(receiptStatusSummary({ status: "BOUNCED", applicationStatus: "APPLIED", bankStatus: "DEPOSITED", method: "CHEQUE" }).label).toBe("Cheque devuelto");
    expect(receiptStatusSummary({ status: "RECORDED", applicationStatus: "APPLIED", bankStatus: "DEPOSITED", method: "TRANSFER" })).toEqual({ label: "Aplicado", tone: "done" });
    expect(receiptStatusSummary({ status: "RECORDED", applicationStatus: "PARTIALLY_APPLIED", bankStatus: "IN_TRANSIT", method: "CHEQUE" })).toEqual({
      label: "Aplicado en parte · sin depositar",
      tone: "progress",
    });
    expect(receiptStatusSummary({ status: "RECORDED", applicationStatus: "UNAPPLIED", bankStatus: "IN_TRANSIT", method: "CASH" }).tone).toBe("attention");
  });
});

describe("credit preview (V-14, E-UX4-4)", () => {
  const base = { available: "10000.00", availableAfter: "5000.00", overdueDays: 0, overdueDaysBlock: 30, reasons: [] as string[] };
  it("says it fits", () => {
    const summary = creditPreviewSummary({ ...base, fits: true });
    expect(summary.fits).toBe(true);
    expect(summary.headline).toMatch(/^Cabe en el crédito disponible/);
    expect(summary.reasons).toEqual([]);
  });
  it("says by how much it exceeds, from the server's availableAfter without its sign", () => {
    const summary = creditPreviewSummary({ ...base, fits: false, availableAfter: "-1234.50", reasons: ["CREDIT_LIMIT_EXCEEDED"] });
    expect(summary.headline).toBe("Excede el crédito disponible por RD$ 1,234.50: irá a Crédito para su aprobación.");
    expect(summary.reasons).toEqual(["El monto excede el crédito disponible."]);
  });
  it("explains the other reasons", () => {
    const summary = creditPreviewSummary({ ...base, fits: false, overdueDays: 45, reasons: ["CREDIT_HOLD", "OVERDUE_DAYS_EXCEEDED"] });
    expect(summary.headline).toMatch(/^No se aprobará automáticamente/);
    expect(summary.reasons).toEqual(["El cliente tiene el crédito retenido.", "El cliente tiene facturas vencidas hace 45 días (se permiten hasta 30)."]);
    expect(creditReasonLabel("CUSTOMER_TERMS_REQUIRED")).toBe("El cliente no tiene términos de crédito aprobados.");
    expect(creditReasonLabel("NEW_CODE")).toBe("NEW_CODE");
  });
});

describe("draft preview (V-11)", () => {
  it("asks only when every line has a product and a positive quantity", () => {
    expect(previewableLines([{ itemId: "a", uom: "un", quantity: " 100 " }])).toEqual([{ itemId: "a", uom: "un", quantity: "100" }]);
    expect(previewableLines([{ itemId: "a", uom: "un", quantity: "100" }, { itemId: "", uom: "", quantity: "" }])).toBeNull();
    expect(previewableLines([{ itemId: "a", uom: "un", quantity: "0" }])).toBeNull();
    expect(previewableLines([{ itemId: "a", uom: "un", quantity: "1,5" }])).toBeNull();
    expect(previewableLines([])).toBeNull();
  });
});

describe("history split (V-05)", () => {
  it("keeps the accounting changes apart", () => {
    const history = [
      { statusKind: "DOCUMENT", to: "CONFIRMED" },
      { statusKind: "ACCOUNTING", to: "POSTED" },
      { statusKind: "DOCUMENT", to: "PAID" },
    ];
    const { commercial, accounting } = splitHistory(history);
    expect(commercial.map((h) => h.to)).toEqual(["CONFIRMED", "PAID"]);
    expect(accounting.map((h) => h.to)).toEqual(["POSTED"]);
  });
});

describe("plan dispatch (V-25)", () => {
  it("prefills the ordered quantity only when nothing was delivered or planned", () => {
    expect(pendingWithoutArithmetic({ qtyOrdered: "100.000000", qtyDelivered: "0.000000" }, false)).toBe("100.000000");
    expect(pendingWithoutArithmetic({ qtyOrdered: "100", qtyDelivered: "40" }, false)).toBeNull();
    expect(pendingWithoutArithmetic({ qtyOrdered: "100", qtyDelivered: "0" }, true)).toBeNull();
    expect(isOpenDelivery("LOADED")).toBe(true);
    expect(isOpenDelivery("DELIVERED")).toBe(false);
    expect(isOpenDelivery("CANCELLED")).toBe(false);
  });
});

describe("receipt application suggestion (V-34, E-UX4-10)", () => {
  it("turns the suggestion into the form's amounts, leaving zero suggestions empty", () => {
    expect(
      suggestionAmounts([
        { invoiceId: "a", suggested: "500.00" },
        { invoiceId: "b", suggested: "0.00" },
      ]),
    ).toEqual({ a: "500.00" });
  });
});

describe("AR aging filters (V-18)", () => {
  const customers = [
    { customerName: "Constructora Uno", bucket1: "0.00", bucket2: "0.00", bucket3: "0.00", over: "0.00" },
    { customerName: "Ferretería Él Sol", bucket1: "0.00", bucket2: "150.00", bucket3: "0.00", over: "0.00" },
  ];
  it("filters by name (accents and case ignored) and by past due", () => {
    expect(hasOverdue(customers[0]!)).toBe(false);
    expect(hasOverdue(customers[1]!)).toBe(true);
    expect(filterAging(customers, "el sol", false).map((c) => c.customerName)).toEqual(["Ferretería Él Sol"]);
    expect(filterAging(customers, "", true).map((c) => c.customerName)).toEqual(["Ferretería Él Sol"]);
    expect(filterAging(customers, "", false)).toHaveLength(2);
  });
});

describe("proforma, credit note and lots (V-20, V-31, V-27)", () => {
  it("marks a proforma of an unconfirmed or cancelled order", () => {
    expect(proformaWatermark("DRAFT")).toBe("BORRADOR");
    expect(proformaWatermark("PENDING_CREDIT")).toBe("BORRADOR");
    expect(proformaWatermark("CANCELLED")).toBe("CANCELADO");
    expect(proformaWatermark("CONFIRMED")).toBeNull();
  });
  it("offers a credit note only on an accepted invoice with something still owed", () => {
    expect(creditNoteOffered("CONFIRMED", "ACCEPTED_EXTERNAL")).toBe(true);
    expect(creditNoteOffered("PARTIALLY_PAID", "ACCEPTED_EXTERNAL")).toBe(true);
    expect(creditNoteOffered("PAID", "ACCEPTED_EXTERNAL")).toBe(false);
    expect(creditNoteOffered("CONFIRMED", "ECF_ACCEPTED")).toBe(true); // VS4-03: accepted through the gateway
    expect(creditNoteOffered("CONFIRMED", "ECF_SENDING")).toBe(false);
    expect(creditNoteOffered("CONFIRMED", "PENDING_EXTERNAL")).toBe(false);
  });
  it("reads a delivery lot", () => {
    expect(deliveryLotLabel({ lotCode: "L-0001", baseQuantity: "100.000000", sourceLocationCode: "PATIO" }, (v) => v.replace(/\.?0+$/, ""))).toBe("Lote L-0001: 100 (sale de PATIO)");
    expect(deliveryLotLabel({ lotCode: "L-0002", baseQuantity: "5", sourceLocationCode: null }, (v) => v)).toBe("Lote L-0002: 5");
  });
});
