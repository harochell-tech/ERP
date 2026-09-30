"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { Field, StatusBadge } from "./ui";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { itemDisplay, productionStatus, quantityWithUnit, type ProductionStatusKind } from "@/lib/ux4bProduction";
import "./ux4bProduction.css";

// MFG1-07: what the production screens share — the plants (with their locations, from master data) and the items.

export type PlantOption = Pick<Schemas["PlantView"], "plantId" | "code"> & { locations: Schemas["LocationView"][] };

/**
 * The plants the user can pick: from master data (with locations) for a master_data:read holder, otherwise the plants of the
 * production machines (without locations).
 */
export function usePlants() {
  const { companyId, can, plantFor } = useSession();
  return useLoad<PlantOption[]>(
    companyId
      ? async () => {
          if (can("master_data:read")) {
            const plants = await query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId }, query: { plantId: plantFor("master_data:read") } });
            return plants.items;
          }
          const machines = await query("/api/v1/companies/{companyId}/manufacturing/machines", { path: { companyId }, query: { plantId: plantFor("production:read") } });
          const seen = new Map<string, PlantOption>();
          for (const m of machines.items) {
            seen.set(m.plantId, { plantId: m.plantId, code: m.plantCode, locations: [] });
          }
          return [...seen.values()];
        }
      : null,
    [companyId],
  );
}

/** Items of the company (finished goods and raw materials), when the user can read master data. */
export function useItems() {
  const { companyId, can } = useSession();
  return useLoad<Schemas["ItemView"][]>(
    companyId && can("master_data:read")
      ? async () => (await query("/api/v1/companies/{companyId}/master-data/items", { path: { companyId }, query: { limit: 200 } })).items
      : companyId
        ? async () => []
        : null,
    [companyId],
  );
}

/** The chosen plant: the one the user picked, else the header's plant when listed, else the first. */
export function useChosenPlant(plants: readonly PlantOption[] | null) {
  const { plantId: headerPlant } = useSession();
  const [picked, setPicked] = useState<string>("");
  const list = plants ?? [];
  const chosen = list.find((p) => p.plantId === picked) ?? list.find((p) => p.plantId === headerPlant) ?? list[0];
  return { plant: chosen, setPlant: setPicked };
}

export function PlantSelect({ plants, value, onChange }: { plants: readonly PlantOption[]; value: string; onChange: (plantId: string) => void }) {
  const { plantName } = useSession();
  return (
    <Field label="Planta de producción">
      <select aria-label="Planta de producción" value={value} onChange={(e) => onChange(e.target.value)}>
        {plants.map((p) => (
          <option key={p.plantId} value={p.plantId}>
            {plantName(p.plantId, p.code)}
          </option>
        ))}
      </select>
    </Field>
  );
}

/** P-37: "CODE — Description (unit)", the code once when the description repeats it; "L" for litre (P-06). */
export function itemLabel(item: Pick<Schemas["ItemView"], "code" | "description" | "baseUom">): string {
  return itemDisplay(item.code, item.description, item.baseUom);
}

/** P-12 / E-UX4-14: a production status in words with its own tone (a released lot is done, a recipe "Activa"). */
export function ProductionBadge({ kind, status, testId }: { kind: ProductionStatusKind; status: string | null | undefined; testId?: string }) {
  const known = productionStatus(kind, status);
  if (!known) {
    return <StatusBadge status={status} testId={testId} />;
  }
  return (
    <span className={`badge tone-${known.tone}`} data-testid={testId}>
      {known.label}
    </span>
  );
}

/** P-05: a quantity with its unit, in the figures' typeface. */
export function Quantity({ value, uom, testId }: { value: string | null | undefined; uom?: string | null; testId?: string }) {
  return (
    <span className="mono" data-testid={testId}>
      {quantityWithUnit(value, uom)}
    </span>
  );
}
