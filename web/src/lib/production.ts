// MFG1-07: small helpers of the production screens (no arithmetic on quantities or money: those stay strings).
import type { Schemas } from "@/api/client";

type Location = Schemas["LocationView"];

/** System locations by code (their flags are enforced by CHECKs on md.location: TRANSITO is the transit, CURADO the curing). */
export const TRANSIT_LOCATION = "TRANSITO";
export const CURING_LOCATION = "CURADO";

/** Stock locations of a plant: what materials are consumed from and what a lot is released to (neither transit nor curing). */
export function stockLocations(locations: readonly Location[]): Location[] {
  return locations.filter((l) => l.code !== TRANSIT_LOCATION && l.code !== CURING_LOCATION);
}

/** Locations a lot can be scrapped from: every non-transit location, curing included. */
export function scrapLocations(locations: readonly Location[]): Location[] {
  return locations.filter((l) => l.code !== TRANSIT_LOCATION);
}

/** An <input type="time"> value ("07:00" or "07:00:30") as the API's time ("07:00:00"); null when it is not a time. */
export function toApiTime(value: string): string | null {
  const match = /^([01]\d|2[0-3]):([0-5]\d)(?::([0-5]\d))?$/.exec(value.trim());
  if (!match) {
    return null;
  }
  return `${match[1]}:${match[2]}:${match[3] ?? "00"}`;
}

/** The API's time ("07:00:00") as shown and as an <input type="time"> value ("07:00"). */
export function formatTime(value: string | null | undefined): string {
  return value ? value.slice(0, 5) : "—";
}

/** A whole number of hours / batches typed by the user, or null. */
export function parseWholeNumber(value: string): number | null {
  const trimmed = value.trim();
  if (!/^\d{1,6}$/.test(trimmed)) {
    return null;
  }
  return Number.parseInt(trimmed, 10);
}

