"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate, formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// E-RNC-7: the DGII registry in force (imported weekly with the deployment CLI, E-RNC-2) and the customers and suppliers whose RNC
// is missing from it, not ACTIVO, or registered under another name. Read only: fixing a party is done on its own page.

export default function Page() {
  const { companyId, can } = useSession();
  const { data, error } = useLoad(
    can("rnc:read") ? () => query("/api/v1/companies/{companyId}/master-data/rnc-registry", { path: { companyId } }) : null,
    [companyId],
  );
  if (!can("rnc:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return (
      <>
        <h1>Padrón RNC</h1>
        <Loading error={error} />
      </>
    );
  }
  const last = data.lastImport;
  return (
    <>
      <h1>Padrón RNC</h1>
      {last === null ? (
        <p className="warning">Aún no se ha importado el padrón de la DGII.</p>
      ) : (
        <p className="muted" data-testid="rnc-registry-import">
          Padrón de la DGII del {formatDate(last.sourceDate)}: {last.rows.toLocaleString("es-DO")} contribuyentes ({last.skipped} filas omitidas), importado por{" "}
          {last.importedBy} el {formatDateTime(last.importedAt)}.
        </p>
      )}
      <h2>Clientes y proveedores con diferencias</h2>
      {last === null ? null : data.discrepancies.length === 0 ? (
        <p className="muted">Todos los clientes y proveedores coinciden con el padrón.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>RNC</th>
              <th>Razón social</th>
              <th>Tipo</th>
              <th>Diferencia</th>
              <th>En el padrón</th>
            </tr>
          </thead>
          <tbody>
            {data.discrepancies.map((d) => (
              <tr key={d.partyId}>
                <td className="mono">{d.rnc}</td>
                <td>
                  <Link href={d.isSupplier ? `/maestros/proveedor/?id=${d.partyId}` : `/ventas/cliente/?id=${d.partyId}`}>{d.legalName}</Link>
                </td>
                <td>{[d.isCustomer ? "Cliente" : null, d.isSupplier ? "Proveedor" : null].filter(Boolean).join(" y ")}</td>
                <td>
                  <StatusBadge status={d.issue} />
                </td>
                <td className="wrap">{d.registryName ? `${d.registryName} · ${d.registryStatus}` : "—"}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
