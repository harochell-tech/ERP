"use client";

import { query } from "@/api/client";
import { NoPermission } from "@/components/ui";
import { LoadingIndicator } from "@/components/StateNotices";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// UI-01: plants and their locations, read only (created with the deployment CLI, E-B03-15-2).
export default function Page() {
  const { companyId, can, plantFor, plantName } = useSession();
  const plantId = plantFor("master_data:read");
  const { data, error } = useLoad(
    can("master_data:read") ? () => query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId }, query: { plantId } }) : null,
    [companyId, plantId],
  );
  if (!can("master_data:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Plantas y ubicaciones</h1>
      <p className="muted">Consulta de las plantas de la empresa y de sus ubicaciones (patio, curado, tránsito…). Para abrir una planta o una ubicación nueva, pídalo al equipo de sistemas; el nombre de cada planta se cambia en Configuración › Empresa.</p>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Planta</th>
              <th>Área de valuación</th>
              <th>Ubicaciones</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((p) => (
              <tr key={p.plantId}>
                <td>{plantName(p.plantId, p.code)}</td>
                <td className="mono">{p.valuationAreaCode}</td>
                <td className="wrap">{p.locations.map((l) => l.code).join(", ") || "—"}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
