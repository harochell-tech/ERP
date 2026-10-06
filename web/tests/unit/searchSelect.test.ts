import { describe, expect, it } from "vitest";
import { matches, partyOption } from "@/components/SearchSelect";

// UX5-01 (E-UX5-1): every word typed, anywhere in the name, the RNC or the code, without accents or capitals.
describe("SearchSelect filter", () => {
  const hotel = partyOption("1", "Hotel Playa Bávaro, S.A.", "101000001");
  const forklift = partyOption("2", "Forklift Parts Inc.", null, "US");

  it("matches any part of the name regardless of accents and capitals", () => {
    expect(matches(hotel, "bavaro")).toBe(true);
    expect(matches(hotel, "PLAYA")).toBe(true);
    expect(matches(hotel, "bávaro hotel")).toBe(true);
    expect(matches(hotel, "playa cabrera")).toBe(false);
  });

  it("matches the RNC, and a foreign supplier by its country", () => {
    expect(matches(hotel, "101000")).toBe(true);
    expect(forklift.hint).toBe("Exterior · US");
    expect(matches(forklift, "exterior")).toBe(true);
  });

  it("matches the keywords too", () => {
    expect(matches({ value: "x", label: "Bloque de 6 pulgadas", keywords: "BLOQUE-6" }, "bloque-6")).toBe(true);
  });
});
