import { describe, expect, it } from "vitest";
import { can, hasPlantScope, queryPlant, scopeOf, type SessionCompany } from "@/lib/scope";

const company = (assignments: { plantId: string | null; permissions: string[] }[]): SessionCompany => ({
  companyId: "c",
  legalName: "Empresa",
  permissions: [...new Set(assignments.flatMap((a) => a.permissions))],
  assignments: assignments.map((a, i) => ({ roleCode: `R${i}`, roleName: `Rol ${i}`, ...a })),
});

describe("plant scope (E-PR18b-8)", () => {
  it("a company-wide assignment sends no plant", () => {
    const c = company([{ plantId: null, permissions: ["purchase_order:read"] }]);
    expect(scopeOf(c, "purchase_order:read")).toEqual({ companyWide: true, plants: [] });
    expect(queryPlant(c, "purchase_order:read", "p1")).toBeUndefined();
    expect(hasPlantScope(c)).toBe(false);
  });

  it("a plant-scoped reader sends its selected plant, or its first one", () => {
    const c = company([
      { plantId: "p1", permissions: ["goods_receipt:read"] },
      { plantId: "p2", permissions: ["goods_receipt:read"] },
    ]);
    expect(queryPlant(c, "goods_receipt:read", "p2")).toBe("p2");
    expect(queryPlant(c, "goods_receipt:read", "otra")).toBe("p1");
    expect(queryPlant(c, "goods_receipt:read", null)).toBe("p1");
    expect(hasPlantScope(c)).toBe(true);
  });

  it("permissions come only from the assignments that grant them", () => {
    const c = company([{ plantId: "p1", permissions: ["goods_receipt:post"] }]);
    expect(can(c, "goods_receipt:post")).toBe(true);
    expect(can(c, "audit:read")).toBe(false);
    expect(can(undefined, "goods_receipt:post")).toBe(false);
  });
});
