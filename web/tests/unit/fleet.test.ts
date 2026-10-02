import { describe, expect, it } from "vitest";
import { isFleetCode, licenseWarning, normalizeFleetCode, vehicleName } from "@/lib/fleet";

describe("fleet", () => {
  it("writes the ficha in capitals with single spaces", () => {
    expect([" br   09 ", "hr 114", "BR09"].map(normalizeFleetCode)).toEqual(["BR 09", "HR 114", "BR09"]);
    expect(["BR 09", "HR 114", "B", "BR-09", "BR  09", "ABCDEFGHIJKLM"].map(isFleetCode)).toEqual([true, true, false, false, false, false]);
  });

  it("names a vehicle by its ficha and plate", () => {
    expect(vehicleName({ fleetCode: "BR 09", plate: "L123456" })).toBe("BR 09 · L123456");
    expect(vehicleName({ fleetCode: null, plate: "L123456" })).toBe("Sin ficha · L123456");
  });

  it("warns about the licence from 30 days before it expires", () => {
    expect([null, 31, 30, 1, 0, -1, -5].map(licenseWarning)).toEqual([
      null,
      null,
      "Licencia: vence en 30 días",
      "Licencia: vence mañana",
      "Licencia: vence hoy",
      "Licencia: venció ayer",
      "Licencia: venció hace 5 días",
    ]);
  });
});
