"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { ErrorBox, Loading, NoPermission } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { formatDateTime, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

export default function Reconciliations() {
  const { companyId, can } = useSession();
  const { data, error, reload } = useLoad(
    can("reconciliation:read") ? () => query("/api/v1/companies/{companyId}/reconciliation/runs", { path: { companyId }, query: { limit: 100 } }) : null,
    [companyId],
  );
  const run = useCommand("run-reconciliation", "/api/v1/companies/{companyId}/reconciliation/run-reconciliation");

  if (!can("reconciliation:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Conciliaciones</h1>
      {can("reconciliation:run") ? (
        <div className="actions">
          <button
            type="button"
            disabled={run.busy}
            onClick={async () => {
              if (await run.run({ reconCodes: null })) {
                reload();
              }
            }}
          >
            Ejecutar todas las conciliaciones
          </button>
        </div>
      ) : null}
      <ErrorBox error={run.error} />
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No se han ejecutado conciliaciones.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Conciliación</th>
              <th>Fecha</th>
              <th className="num">Total A</th>
              <th className="num">Total B</th>
              <th className="num">Diferencia</th>
              <th>Resultado</th>
              <th className="num">Excepciones</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((r) => (
              <tr key={r.runId}>
                <td>
                  <Link href={`/cierre/conciliacion/?id=${r.runId}`}>{r.reconCode}</Link> <span className="muted">{r.description}</span>
                </td>
                <td>{formatDateTime(r.asOf)}</td>
                <td className="num">{formatDecimal(r.totalA)}</td>
                <td className="num">{formatDecimal(r.totalB)}</td>
                <td className="num">{formatDecimal(r.difference)}</td>
                <td>{statusLabel(r.status)}</td>
                <td className="num">{r.exceptionCount}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
