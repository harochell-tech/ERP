import { describe, expect, it } from "vitest";
import { identityLabel, personLabel, showIdentitySelector } from "@/lib/identities";

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

describe("people by name (E-UX1-01-3)", () => {
  it("reads 'Name · e-mail', the e-mail alone without a name, the fallback without either", () => {
    expect(personLabel("Ana Pérez", "ana@rochell.com.do")).toBe("Ana Pérez · ana@rochell.com.do");
    expect(personLabel("  ", "ana@rochell.com.do")).toBe("ana@rochell.com.do");
    expect(personLabel(null, "ana@rochell.com.do")).toBe("ana@rochell.com.do");
    expect(personLabel("ana@rochell.com.do", "ana@rochell.com.do")).toBe("ana@rochell.com.do");
    expect(personLabel(null, null, "u-1")).toBe("u-1");
  });
});
