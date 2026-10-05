"use client";

import { query } from "@/api/client";
import { Field } from "@/components/ui";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// PRS-05 (E-PRS-05-5, E-SRV1-11, E-PRS-04-10): the delivery zone of an own-truck document. Required once the company has zones; the
// freight of each product to it comes from the customer's own list.

/** The ACTIVE zones (empty while the company has none: the zone is not asked then). */
export function useZones(): { zones: { zoneId: string; name: string }[]; loaded: boolean } {
  const { companyId } = useSession();
  const { data } = useLoad(() => query("/api/v1/companies/{companyId}/sales/delivery-zones", { path: { companyId }, query: { status: "ACTIVE" } }), [companyId]);
  return { zones: data?.items ?? [], loaded: data !== null };
}

export function ZoneField({ value, onChange, error, zones }: { value: string; onChange: (zoneId: string) => void; error?: string; zones: { zoneId: string; name: string }[] }) {
  if (zones.length === 0) {
    return null;
  }
  return (
    <Field label="Zona de entrega" required error={error} hint="El flete a la zona sale de la lista de precios del cliente.">
      <select aria-label="Zona de entrega" value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Seleccione…</option>
        {zones.map((z) => (
          <option key={z.zoneId} value={z.zoneId}>
            {z.name}
          </option>
        ))}
      </select>
    </Field>
  );
}
