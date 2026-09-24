import { describe, expect, it } from "vitest";
import { formatDecimal, formatQuantity, isDecimal, isPositiveDecimal, normalizeInput } from "@/lib/decimal";

describe("decimals stay strings (ADR-015, E-PR18b-4)", () => {
  it("formats losslessly: grouping and trailing zeros only", () => {
    expect(formatDecimal("45040.0000")).toBe("45,040.00");
    expect(formatDecimal("1000.000000")).toBe("1,000.00");
    expect(formatDecimal("0.125")).toBe("0.125");
    expect(formatDecimal("-2160.0000")).toBe("-2,160.00");
    expect(formatDecimal("-0.0000")).toBe("0.00");
    expect(formatDecimal("9999999999999.999999")).toBe("9,999,999,999,999.999999");
    expect(formatDecimal(null)).toBe("—");
  });

  it("formats quantities without forced decimals", () => {
    expect(formatQuantity("40.000000")).toBe("40");
    expect(formatQuantity("1.450000")).toBe("1.45");
  });

  it("validates format and scale, never parsing to a number", () => {
    expect(isDecimal("40", 6)).toBe(true);
    expect(isDecimal("1000.123456", 6)).toBe(true);
    expect(isDecimal("1000.1234567", 6)).toBe(false);
    expect(isDecimal("1e3", 6)).toBe(false);
    expect(isDecimal("12345678901234", 6)).toBe(false);
    expect(isPositiveDecimal("0.000", 6)).toBe(false);
    expect(isPositiveDecimal("-1", 6)).toBe(false);
    expect(isPositiveDecimal("0.000001", 6)).toBe(true);
  });

  it("only strips grouping separators and spaces from input", () => {
    expect(normalizeInput(" 1,500.50 ")).toBe("1500.50");
  });
});
