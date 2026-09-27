import { describe, expect, it } from "vitest";
import { identityLabel, showIdentitySelector } from "@/lib/identities";

describe("test identities (E-B03-14)", () => {
  it("labels an identity by mailbox name and Spanish role names", () => {
    expect(identityLabel({ userId: "u", email: "comprador@staging.invalid", roles: ["COMPRADOR", "APROBADOR_COMPRAS"] })).toBe(
      "comprador — Comprador, Aprobador de compras",
    );
    expect(identityLabel({ userId: "u", email: "x@staging.invalid", roles: ["NUEVO_ROL"] })).toBe("x — NUEVO_ROL");
  });

  it("shows the selector to a tester, and to anyone acting so they can return", () => {
    expect(showIdentitySelector(["identity:act_as"], null)).toBe(true);
    expect(showIdentitySelector(["purchase_order:create"], "alex@rochell.com.do")).toBe(true);
    expect(showIdentitySelector(["purchase_order:create"], null)).toBe(false);
  });
});
