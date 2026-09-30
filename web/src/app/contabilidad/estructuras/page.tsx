"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { REPORTS } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { effectiveFromLabel } from "@/lib/ux4a-contabilidad";

// FIN1-04 (E-FIN1-04-10): the versions of the balance sheet and income statement structures.
export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("configuration:read");
  const { data, error } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/finance/report-structures", { path: { companyId } }) : null, [companyId, allowed]);
  if (!allowed) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Estructuras de reporte</h1>
      <p className="muted">
        Definen cómo se agrupan las cuentas en el balance general y el estado de resultados. Las prepara el Controller y las aprueba el
        Aprobador de políticas contables (otra persona); los estados usan siempre la versión activa.
      </p>
      {can("account:manage") ? (
        <div className="actions">
          {Object.entries(REPORTS).map(([code, label]) => (
            <Link key={code} className="button" href={`/contabilidad/estructuras/nueva/?reporte=${code}`}>
              Nueva versión: {label}
            </Link>
          ))}
        </div>
      ) : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">Todavía no hay estructuras.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Reporte</th>
              <th>Versión</th>
              <th>Desde</th>
              <th>Estado</th>
              <th className="num">Líneas</th>
              <th className="num">Cuentas</th>
              <th>Preparó</th>
              <th>Aprobó</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((s) => (
              <tr key={s.structureVersionId}>
                <td>
                  <Link href={`/contabilidad/estructura/?id=${s.structureVersionId}`}>{REPORTS[s.report] ?? s.report}</Link>
                </td>
                <td>{s.version}</td>
                <td>
                  <span className="muted">{effectiveFromLabel(s.status)}</span> {formatDate(s.effectiveFrom)}
                </td>
                <td>
                  <StatusBadge status={s.status} />
                </td>
                <td className="num">{s.lines}</td>
                <td className="num">{s.accounts}</td>
                <td className="wrap">{s.preparedBy ?? "—"}</td>
                <td className="wrap">{s.approvedBy ?? (s.status === "DRAFT" ? "Pendiente" : "—")}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
