// UX3-02 (E-UX3-11): units offered in selects instead of typed. Pure, unit-tested.

interface Conversion {
  fromUom: string;
  toUom: string;
}

/**
 * The units a quantity of an item may be entered in: its base unit first, then every unit with a conversion into it (as the purchase
 * order form offers them), without repeats. `current` keeps a unit already chosen (e.g. a recorded consumption) in the list.
 */
export function uomOptions(baseUom: string, conversions: readonly Conversion[] = [], current?: string | null): string[] {
  const units = [baseUom, ...conversions.filter((c) => c.toUom === baseUom).map((c) => c.fromUom)];
  if (current && current.trim() !== "") {
    units.push(current);
  }
  return [...new Set(units.filter((u) => u !== ""))];
}

/** The `md.uom` catalogue as select options, "kg (masa)"; an unknown dimension is shown as it comes. */
const DIMENSIONS: Readonly<Record<string, string>> = {
  MASS: "masa",
  VOLUME: "volumen",
  COUNT: "unidades",
  LENGTH: "longitud",
  AREA: "área",
};

export function uomCatalogueOptions(uoms: readonly { code: string; dimension: string }[]): { value: string; label: string }[] {
  return [...uoms]
    .sort((a, b) => a.code.localeCompare(b.code))
    .map((u) => ({ value: u.code, label: `${u.code} (${DIMENSIONS[u.dimension] ?? u.dimension.toLowerCase()})` }));
}
