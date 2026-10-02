import { describe, expect, it } from "vitest";
import { amountToAssign, buyerIdError, cashCustomerName, cashSaleStep, normalizeBuyerId, paymentAmountError, paymentPendingReason } from "@/lib/cashSales";

describe("cash sales", () => {
  it("checks the buyer's identification by its kind", () => {
    expect(normalizeBuyerId("CEDULA", "001-1234567-8")).toBe("00112345678");
    expect(normalizeBuyerId("PASAPORTE", " ab 12345 ")).toBe("AB12345");
    expect(buyerIdError("", "")).toBeNull();
    expect(buyerIdError("", "00112345678")).toBe("Elija el tipo de identificación.");
    expect([buyerIdError("CEDULA", "001-1234567-8"), buyerIdError("CEDULA", "123")]).toEqual([null, "La cédula tiene 11 dígitos."]);
    expect([buyerIdError("RNC", "131925332"), buyerIdError("RNC", "13192533")]).toEqual([null, "El RNC tiene 9 dígitos."]);
    expect([buyerIdError("PASAPORTE", "AB12345"), buyerIdError("PASAPORTE", "A1")]).toEqual([null, "El pasaporte tiene de 5 a 20 letras y números."]);
  });

  it("names the customer with its buyer", () => {
    expect(cashCustomerName("Consumidor final", "María Pérez")).toBe("Consumidor final — María Pérez");
    expect(cashCustomerName("Consumidor final", null)).toBe("Consumidor final");
  });

  it("takes cash and cheques for what is still to pay and transfers for more", () => {
    expect(paymentAmountError("CASH", "5900.00", "5900.00")).toBeNull();
    expect(paymentAmountError("CASH", "6000", "5900.00")).toContain("vuelto");
    expect(paymentAmountError("CHEQUE", "5900.01", "5900.00")).toContain("cheque");
    expect(paymentAmountError("TRANSFER", "6490.00", "5900.00")).toBeNull();
    expect([amountToAssign("6490.00", "5900.00"), amountToAssign("1000.00", "5900.00")]).toEqual(["5900.00", "1000.00"]);
  });

  it("explains a payment that does not count yet", () => {
    expect(paymentPendingReason({ counts: true, method: "CASH", receiptStatus: "RECORDED" })).toBeNull();
    expect(paymentPendingReason({ counts: false, method: "CHEQUE", receiptStatus: "RECORDED" })).toBe("Pendiente: el cheque cuenta cuando el banco lo acredite.");
    expect(paymentPendingReason({ counts: false, method: "CHEQUE", receiptStatus: "BOUNCED" })).toContain("devuelto");
  });

  it("places the sale in its step", () => {
    expect(["DRAFT", "PENDING_PAYMENT", "CONFIRMED", "PARTIALLY_DELIVERED", "DELIVERED", "CANCELLED"].map(cashSaleStep)).toEqual([
      "PRODUCTS",
      "PAYMENT",
      "DISPATCH",
      "DISPATCH",
      "DONE",
      "CANCELLED",
    ]);
  });
});

describe("consumer identification rule form (E-CF1-05-7)", () => {
  it("builds the amount the server expects and reads it back in words", async () => {
    const { buildFiscalDefinition, describeFiscalDefinition, parseFiscalDefinition, validateFiscalForm } = await import("@/lib/fiscalRuleForm");
    const { ruleKindRunsTests } = await import("@/lib/configuration");
    const parsed = parseFiscalDefinition("CONSUMER_ID_THRESHOLD", '{"amount":"250000.00"}');
    expect(parsed.form?.amount).toBe("250000.00");
    expect(buildFiscalDefinition("CONSUMER_ID_THRESHOLD", { ...parsed.form!, amount: " 250,000.00 " })).toBe(JSON.stringify({ amount: "250000.00" }, null, 2));
    expect(validateFiscalForm("CONSUMER_ID_THRESHOLD", { ...parsed.form!, amount: "" }).amount).toContain("mayor que cero");
    expect(validateFiscalForm("CONSUMER_ID_THRESHOLD", parsed.form!)).toEqual({});
    expect(describeFiscalDefinition("CONSUMER_ID_THRESHOLD", '{"amount":"250000.00"}')).toEqual(["Identificación del comprador obligatoria desde RD$ 250,000.00 (total con ITBIS)"]);
    expect(parseFiscalDefinition("CONSUMER_ID_THRESHOLD", '{"amount":1}').error).toContain("amount");
    expect(ruleKindRunsTests("CONSUMER_ID_THRESHOLD")).toBe(false);
  });
});
