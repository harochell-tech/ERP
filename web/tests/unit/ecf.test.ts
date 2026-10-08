import { describe, expect, it } from "vitest";
import { ecfStatusLabel, ecfStatusTone, encfDigits, stampQr } from "@/lib/ecf";

describe("e-CF words (VS4-04)", () => {
  it("names the statuses of an attempt and gives each a tone", () => {
    expect(ecfStatusLabel("REQUIRES_ACTION")).toBe("Requiere atención");
    expect(ecfStatusTone("CONTINGENCY")).toBe("error");
    expect(ecfStatusLabel("OTHER")).toBe("OTHER");
  });

  it("reads the 10 digits of an e-NCF number however it is typed", () => {
    expect(encfDigits("E310000000041", "31")).toBe("0000000041");
    expect(encfDigits(" 41 ", "31")).toBe("0000000041");
    expect(encfDigits("0", "31")).toBeNull();
    expect(encfDigits("12345678901", "31")).toBeNull();
    expect(encfDigits("E32000001", "31")).toBeNull();
  });

  it("draws the DGII stamp as a QR only when there is a URL", () => {
    expect(stampQr(null)).toBeNull();
    const qr = stampQr("https://ecf.dgii.gov.do/ecf/ConsultaTimbre?RncEmisor=131925332&ENCF=E310000000001");
    expect(qr!.size).toBeGreaterThanOrEqual(21);
    expect(qr!.dark(0, 0)).toBe(true); // the finder pattern's corner
  });
});
