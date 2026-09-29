import { describe, expect, it } from "vitest";
import { statusLabel, statusTone } from "@/lib/labels";
import { formatTime, parseWholeNumber, scrapLocations, stockLocations, toApiTime } from "@/lib/production";

describe("production helpers (MFG1-07)", () => {
  const locations = [
    { locationId: "1", code: "PATIO-A" },
    { locationId: "2", code: "TRANSITO" },
    { locationId: "3", code: "CURADO" },
    { locationId: "4", code: "PATIO-B" },
  ];

  it("consumes from and releases to stock locations only; scraps from any non-transit location", () => {
    expect(stockLocations(locations).map((l) => l.code)).toEqual(["PATIO-A", "PATIO-B"]);
    expect(scrapLocations(locations).map((l) => l.code)).toEqual(["PATIO-A", "CURADO", "PATIO-B"]);
  });

  it("sends shift times as HH:mm:ss and shows them as HH:mm", () => {
    expect([toApiTime("07:00"), toApiTime("19:00:30"), toApiTime("24:00"), toApiTime("7:00"), toApiTime("")]).toEqual(["07:00:00", "19:00:30", null, null, null]);
    expect([formatTime("07:00:00"), formatTime(null)]).toEqual(["07:00", "—"]);
  });

  it("reads whole numbers only", () => {
    expect([parseWholeNumber("10"), parseWholeNumber(" 0 "), parseWholeNumber("1.5"), parseWholeNumber("-1"), parseWholeNumber("")]).toEqual([10, 0, null, null, null]);
  });

  it("names the run, lot and collector statuses in Spanish", () => {
    expect(["IN_PROGRESS", "COMPLETED", "CURING", "RELEASED", "BLOCKED", "SCRAPPED", "SETTLED"].map(statusLabel)).toEqual([
      "En proceso",
      "Completada",
      "En curado",
      "Liberado",
      "Bloqueado",
      "Desechado",
      "Liquidado",
    ]);
    expect([statusTone("IN_PROGRESS"), statusTone("COMPLETED"), statusTone("SCRAPPED")]).toEqual(["progress", "done", "error"]);
  });
});
