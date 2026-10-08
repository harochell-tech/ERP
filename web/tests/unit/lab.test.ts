import { describe, expect, it } from "vitest";
import { parameterText, specimenBody, todayIso } from "@/lib/lab";

describe("lab helpers (LAB1-01)", () => {
  it("sends empty measures as null so the server uses the item's nominal ones, and never computes", () => {
    expect(specimenBody({ widthCm: "", heightCm: " ", lengthCm: "39.4", weightKg: "", loadKg: "75,187", blockCondition: "", failureType: "CONICA", notes: "  " })).toEqual({
      loadKg: "75187",
      widthCm: null,
      heightCm: null,
      lengthCm: "39.4",
      weightKg: null,
      blockCondition: null,
      failureType: "CONICA",
      notes: null,
    });
  });

  it("takes today from the Dominican clock, not the browser's", () => {
    // 02:30 UTC on the 9th is still the 8th in Santo Domingo (UTC−4).
    expect(todayIso(new Date("2026-10-09T02:30:00Z"))).toBe("2026-10-08");
    expect(todayIso(new Date("2026-10-09T04:00:00Z"))).toBe("2026-10-09");
  });

  it("shows a parameter without trailing zeros", () => {
    expect([
      parameterText({ kind: "NUMBER", number: "0.15000000", text: null }),
      parameterText({ kind: "NUMBER", number: "26.00000000", text: null }),
      parameterText({ kind: "NUMBER", number: "0.09806650", text: null }),
      parameterText({ kind: "TEXT", number: null, text: "TEST MARK" }),
    ]).toEqual(["0.15", "26", "0.0980665", "TEST MARK"]);
  });
});
