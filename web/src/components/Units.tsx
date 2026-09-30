"use client";

import { query } from "@/api/client";
import { useSession } from "@/lib/session";
import { uomCatalogueOptions } from "@/lib/units";
import { useLoad } from "@/lib/useQuery";

/** UX3-02 (E-UX3-11): the `md.uom` catalogue (`GET /master-data/uoms`) as select options; empty while it loads. */
export function useUomCatalogue(): { value: string; label: string }[] {
  const { companyId, can, plantFor } = useSession();
  const allowed = can("master_data:read");
  const plantId = plantFor("master_data:read");
  const { data } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/master-data/uoms", { path: { companyId }, query: { plantId } }) : null, [companyId, plantId, allowed]);
  return data ? uomCatalogueOptions(data.items) : [];
}
