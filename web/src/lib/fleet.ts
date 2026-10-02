import { expiryText } from "@/lib/ux4a-auditoria";

// FLT-01 (E-FLT-1…5): the vehicle's «ficha» ("BR 09") and the driver's licence expiry. An expired licence only warns.

/** How the vehicle's «ficha» is typed: capitals with single spaces ("br  09" → "BR 09"). */
export function normalizeFleetCode(value: string): string {
  return value.trim().toUpperCase().split(/\s+/).filter(Boolean).join(" ");
}

/** Letters, digits and single spaces, 2 to 12 characters. */
export function isFleetCode(value: string): boolean {
  return /^[A-Z0-9]+( [A-Z0-9]+)*$/.test(value) && value.length >= 2 && value.length <= 12;
}

/** The vehicle as the plant names it: its ficha, then the plate; vehicles from before the ficha show only the plate (E-FLT-5). */
export function vehicleName(vehicle: { fleetCode?: string | null; plate: string }): string {
  return vehicle.fleetCode ? `${vehicle.fleetCode} · ${vehicle.plate}` : `Sin ficha · ${vehicle.plate}`;
}

/** E-FLT-4: the warning from 30 days before the licence expires ("Licencia: vence en 10 días"); null before that or without a date. */
export function licenseWarning(daysToLicenseExpiry: number | null | undefined): string | null {
  if (daysToLicenseExpiry === null || daysToLicenseExpiry === undefined || daysToLicenseExpiry > 30) {
    return null;
  }
  const text = expiryText(daysToLicenseExpiry);
  return text === null ? null : `Licencia: ${text.charAt(0).toLowerCase()}${text.slice(1)}`;
}
