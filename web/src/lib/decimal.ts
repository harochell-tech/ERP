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

/** "007.500" → "7.5", "-0.0" → "0": leading integer zeros and trailing fraction zeros removed, on the text alone. */
function normalizeDecimal(negative: boolean, integer: string, fraction: string): string {
  const int = integer.replace(/^0+(?=\d)/, "") || "0";
  const frac = fraction.replace(/0+$/, "");
  const zero = /^0*$/.test(int + frac);
  return (negative && !zero ? "-" : "") + int + (frac ? `.${frac}` : "");
}

/**
 * Moves the decimal point `places` positions (positive: to the right, × 10^places; negative: to the left) on the text alone, so no
 * digit is rounded or lost ("0.005", 2 → "0.5"; "12.5", -2 → "0.125"). Null when the text is not a plain decimal.
 */
export function shiftDecimalPoint(value: string, places: number): string | null {
  const match = /^(-)?(\d*)(?:\.(\d*))?$/.exec(value.trim());
  if (!match || ((match[2] ?? "") === "" && (match[3] ?? "") === "") || !Number.isInteger(places)) {
    return null;
  }
  const negative = match[1] === "-";
  const integer = match[2] ?? "";
  const fraction = match[3] ?? "";
  const digits = integer + fraction;
  const point = integer.length + places;
  if (point <= 0) {
    return normalizeDecimal(negative, "0", "0".repeat(-point) + digits);
  }
  if (point >= digits.length) {
    return normalizeDecimal(negative, digits + "0".repeat(point - digits.length), "");
  }
  return normalizeDecimal(negative, digits.slice(0, point), digits.slice(point));
}

/**
 * E-UX2-1: a fraction as the server keeps it ("0.05") as a percentage ("5"); "1" → "100", "0.005" → "0.5", "0.125" → "12.5".
 * Text only, no JavaScript number. Null when the value is not a decimal.
 */
export function fractionToPercent(fraction: string | null | undefined): string | null {
  return fraction === null || fraction === undefined ? null : shiftDecimalPoint(fraction, 2);
}

/**
 * E-UX2-1: a percentage typed by the user ("5", "12.5", "5 %") as the fraction the server expects ("0.05", "0.125"). A comma is
 * not a decimal separator here ("1,5" is refused). Null when the text is not a decimal.
 */
export function percentToFraction(percent: string | null | undefined): string | null {
  if (percent === null || percent === undefined) {
    return null;
  }
  const text = percent.replace(/%/g, "").replace(/\s/g, "");
  return text === "" ? null : shiftDecimalPoint(text, -2);
}

/** A fraction shown as a percentage: "0.05" → "5 %"; the value as is when it is not a decimal; "—" when empty. */
export function formatPercent(fraction: string | null | undefined): string {
  if (fraction === null || fraction === undefined || fraction === "") {
    return "—";
  }
  const percent = fractionToPercent(fraction);
  return percent === null ? fraction : `${percent} %`;
}
