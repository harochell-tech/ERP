// E-UX1-01-4: a plant reads "Name (CODE)" when the session knows its name, otherwise its code. The session description lists each
// company's plants (UX1-01a); a plant the list does not know shows what the screen had (code, or the short id).
import type { Schemas } from "@/api/client";

export type SessionPlant = Schemas["SessionPlant"];

/** `key` is a plant id or a plant code; `fallback` is shown when the plant is unknown (defaults to the key). */
export function plantLabel(plants: readonly SessionPlant[] | null | undefined, key: string | null | undefined, fallback?: string): string {
  if (!key) {
    return fallback ?? "—";
  }
  const plant = (plants ?? []).find((p) => p.plantId === key || p.code === key);
  if (!plant) {
    return fallback ?? key;
  }
  const name = plant.name?.trim();
  return name ? `${name} (${plant.code})` : plant.code;
}
