import { describe, expect, it } from "vitest";
import { fold, searchMenu, toggleFavorite } from "@/lib/menu";

const entries = [
  { href: "/contabilidad/balanza/", label: "Balanza", group: "Contabilidad" },
  { href: "/compras/dua/", label: "DUA (aduana)", group: "Compras" },
  { href: "/tesoreria/conciliacion/", label: "Conciliación bancaria", group: "Tesorería" },
  { href: "/cierre/conciliaciones/", label: "Conciliaciones", group: "Contabilidad" },
  { href: "/maestros/cuentas-bancarias/", label: "Cuentas bancarias de la empresa", group: "Tesorería" },
];

describe("menu search (E-NAV-12)", () => {
  it("ignores case and accents", () => {
    expect(fold("Conciliación")).toBe("conciliacion");
    expect(searchMenu(entries, "dua").map((e) => e.href)).toEqual(["/compras/dua/"]);
    expect(searchMenu(entries, "CONCILIACIÓN").length).toBe(2);
  });

  it("needs every word, in the label or the group", () => {
    expect(searchMenu(entries, "conciliacion tesoreria").map((e) => e.href)).toEqual(["/tesoreria/conciliacion/"]);
    expect(searchMenu(entries, "bancaria").map((e) => e.label)).toEqual(["Conciliación bancaria", "Cuentas bancarias de la empresa"]);
  });

  it("puts labels that start with the text first and finds nothing for blank text", () => {
    expect(searchMenu(entries, "cuentas")[0]?.label).toBe("Cuentas bancarias de la empresa");
    expect(searchMenu(entries, "  ")).toEqual([]);
  });
});

describe("favourites (E-NAV-13)", () => {
  it("adds at the end and removes", () => {
    expect(toggleFavorite(["/a/"], "/b/")).toEqual(["/a/", "/b/"]);
    expect(toggleFavorite(["/a/", "/b/"], "/a/")).toEqual(["/b/"]);
  });
});
