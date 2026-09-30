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

/** UX3-02 (E-UX3-12): the filter "Listos para liberar" — lots in curing whose curing the API reports done. */
export const READY_TO_RELEASE = "READY";

export function isReadyToRelease(lot: { status: string; curingDone: boolean }): boolean {
  return lot.status === "CURING" && lot.curingDone;
}

/** The lots screen opens on "Listos para liberar" for whoever releases lots (Calidad), on every status otherwise. */
export function defaultLotFilter(canRelease: boolean): string {
  return canRelease ? READY_TO_RELEASE : "";
}

/** The status the lots query is asked for: "Listos para liberar" reads CURING and keeps the cured ones. */
export function lotQueryStatus(filter: string): string {
  return filter === READY_TO_RELEASE ? "CURING" : filter;
}

export type LotAction = "release" | "block" | "unblock" | "scrap";

/** UX3-02 (E-UX3-12): what the lot's "Acciones" dialog offers, by its status and the user's permissions. */
export function lotActions(status: string, can: (permission: string) => boolean): LotAction[] {
  const actions: LotAction[] = [];
  if (status === "CURING" && can("fg_lot:release")) {
    actions.push("release", "block");
  }
  if (status === "BLOCKED" && can("fg_lot:release")) {
    actions.push("unblock");
  }
  if ((status === "CURING" || status === "BLOCKED" || status === "RELEASED") && can("fg_lot:scrap")) {
    actions.push("scrap");
  }
  return actions;
}
