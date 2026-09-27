"use client";

import { query } from "@/api/client";
import { Loading, NoPermission } from "@/components/ui";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// UI-01: the chart of accounts, read only (loaded with the deployment CLI `import-accounts`, E-B03-15-2).
export default function Page() {
  const { companyId, can } = useSession();
  const { data, error } = useLoad(can("configuration:read") ? () => query("/api/v1/companies/{companyId}/finance/accounts", { path: { companyId } }) : null, [companyId]);
  if (!can("configuration:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Catálogo de cuentas</h1>
      <p className="muted">Solo lectura: el catálogo se carga con la herramienta de despliegue. Las cuentas de control no admiten asientos manuales.</p>
      {data === null ? (
        <Loading error={error} />
      ) : (
        <table>
          <thead>
            <tr>
              <th>Código</th>
              <th>Nombre</th>
              <th>Tipo</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((a) => (
              <tr key={a.accountId}>
                <td className="mono">{a.code}</td>
                <td>{a.name}</td>
                <td>{a.isControl ? "Control" : "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
