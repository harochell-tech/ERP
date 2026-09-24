// E-PR18b-4 / ADR-015: decimals stay strings in the browser. The UI validates their format and formats them for display
// without ever converting them to a JavaScript number, so nothing is rounded or computed on screen.

const DECIMAL = /^-?(\d+)(?:\.(\d+))?$/;

/** A decimal string with at most `maxScale` fraction digits (and at most `maxIntegerDigits` integer digits). */
export function isDecimal(value: string, maxScale: number, maxIntegerDigits = 13): boolean {
  const match = DECIMAL.exec(value.trim());
  if (!match) {
    return false;
  }
  const integer = match[1] ?? "";
  const fraction = match[2] ?? "";
  return integer.replace(/^0+(?=\d)/, "").length <= maxIntegerDigits && fraction.length <= maxScale;
}

/** A decimal string strictly greater than zero. */
export function isPositiveDecimal(value: string, maxScale: number): boolean {
  const trimmed = value.trim();
  return isDecimal(trimmed, maxScale) && !trimmed.startsWith("-") && /[1-9]/.test(trimmed);
}

/** Normalizes user input ("1,500.50 " → "1500.50") only by removing grouping separators and spaces. */
export function normalizeInput(value: string): string {
  return value.replace(/[\s,]/g, "");
}

/**
 * Display form: thousands grouped with commas, trailing fraction zeros removed down to `minFraction` digits. Lossless: a
 * non-zero digit is never dropped ("45040.0000" → "45,040.00"; "1.450000" → "1.45"; "0.125" → "0.125").
 */
export function formatDecimal(value: string | null | undefined, minFraction = 2): string {
  if (value === null || value === undefined || value === "") {
    return "—";
  }
  const match = DECIMAL.exec(value);
  if (!match) {
    return value;
  }
  const negative = value.startsWith("-");
  const integer = (match[1] ?? "0").replace(/^0+(?=\d)/, "");
  let fraction = match[2] ?? "";
  while (fraction.length > minFraction && fraction.endsWith("0")) {
    fraction = fraction.slice(0, -1);
  }
  while (fraction.length < minFraction) {
    fraction += "0";
  }
  const grouped = integer.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
  const sign = negative && /[1-9]/.test(integer + fraction) ? "-" : "";
  return sign + grouped + (fraction.length > 0 ? `.${fraction}` : "");
}

/** Quantities: no forced decimals ("40.000000" → "40"). */
export function formatQuantity(value: string | null | undefined): string {
  return formatDecimal(value, 0);
}
