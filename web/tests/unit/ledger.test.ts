import { describe, expect, it } from "vitest";
import { accountClassLabel, csvUrl, monthStart, sha256Hex } from "@/lib/ledger";

describe("ledger helpers", () => {
  it("builds the CSV link of the same report", () => {
    expect(csvUrl("/api/v1/companies/{companyId}/finance/trial-balance", { path: { companyId: "c1" }, query: { from: "2026-09-01", to: "2026-09-27", plantId: "" } })).toBe(
      "/api/v1/companies/c1/finance/trial-balance?from=2026-09-01&to=2026-09-27&format=csv",
    );
  });

  it("names the classes in Spanish and flags a missing one", () => {
    expect([accountClassLabel("LIABILITY"), accountClassLabel(null)]).toEqual(["Pasivo", "Sin clase"]);
  });

  it("starts the month", () => {
    expect(monthStart("2026-09-27")).toBe("2026-09-01");
  });

  it("hashes the support document as 64 hex characters", async () => {
    expect(await sha256Hex(new Blob([""]))).toBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
  });
});
