import { describe, expect, it } from "vitest";
import { bankAccountLabel, lastFour } from "@/lib/ux4a";

// UX4-02 (E-UX4-1…17): helpers shared by the screens of Compras, Almacén, CxP, Tesorería, Contabilidad, Cierre, Auditoría, Fiscal.

describe("bank account label (E-UX4-6, C-27)", () => {
  it("reads alias · bank ••••last four", () => {
    expect(bankAccountLabel({ alias: "Operativa", bankCode: "TEST_BANK", accountNumber: "0123456789" })).toBe("Operativa · TEST_BANK ••••6789");
  });

  it("drops the alias when there is none or it is blank", () => {
    expect(bankAccountLabel({ alias: null, bankCode: "TEST_BANK", accountNumber: "0123456789" })).toBe("TEST_BANK ••••6789");
    expect(bankAccountLabel({ alias: "  ", bankCode: "TEST_BANK", accountNumber: "0123456789" })).toBe("TEST_BANK ••••6789");
  });

  it("accepts an account number the server already masked", () => {
    expect(lastFour("••••4321")).toBe("4321");
    expect(bankAccountLabel({ alias: "Cobros", bankCode: "BPD", accountNumber: "••••4321" })).toBe("Cobros · BPD ••••4321");
  });

  it("shows a dash when nothing is known", () => {
    expect(bankAccountLabel({})).toBe("—");
  });
});
