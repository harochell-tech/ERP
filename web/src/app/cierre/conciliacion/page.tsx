"use client";

import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { COMPONENTS, formatDate, formatDateTime, statusLabel } from "@/lib/labels";
import { classificationText, exceptionKey, severityLabel, severityTone } from "@/lib/reconciliations";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { sideLabels } from "@/lib/ux4a-auditoria";

/** A run and its exceptions. UX3-02 (E-UX3-2/3): the server's Spanish names, guidance and readable keys. */
function Run() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error } = useLoad(
    can("reconciliation:read") && id ? () => query("/api/v1/companies/{companyId}/reconciliation/runs/{runId}", { path: { companyId, runId: id } }) : null,
    [companyId, id],
  );

  if (!can("reconciliation:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const { run, exceptions } = data;
  // UX4-02 (A-22): the totals under the reconciliation's own labels; (A-21) the date-time ends in "p. m." so no period follows it.
  const labels = sideLabels(run);
  return (
    <>
      <h1>{run.name}</h1>
      <p>
        <StatusBadge status={run.status} testId="run-status" /> <span className="muted mono">{run.reconCode}</span>
      </p>
      <dl className="facts" data-testid="run-totals">
        <dt>Ejecutada</dt>
        <dd>{formatDateTime(run.asOf)}</dd>
        {run.cutoffDate ? (
          <>
            <dt>Corte al</dt>
            <dd>{formatDate(run.cutoffDate)}</dd>
          </>
        ) : null}
        {run.totalA === null && run.totalB === null ? null : (
          <>
            <dt>{labels.a}</dt>
            <dd className="mono">{formatDecimal(run.totalA)}</dd>
            <dt>{labels.b}</dt>
            <dd className="mono">{formatDecimal(run.totalB)}</dd>
            <dt>Diferencia</dt>
            <dd className="mono">{formatDecimal(run.difference)}</dd>
          </>
        )}
      </dl>
      <div className="alert-block guidance" data-testid="run-guidance">
        <strong>Qué hacer</strong>
        {run.guidance}
      </div>
      {exceptions.length === 0 ? (
        <p>Sin excepciones.</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Documento o clave</th>
                <th className="num">Valor A</th>
                <th className="num">Valor B</th>
                <th>Qué se encontró</th>
                <th>Severidad</th>
                <th>Bloquea</th>
                <th>Estado</th>
                <th>Qué hacer</th>
              </tr>
            </thead>
            <tbody>
              {exceptions.map((x) => (
                <tr key={x.exceptionId} data-testid="exception">
                  <td className="wrap" title={x.matchKey}>
                    {exceptionKey(x)}
                  </td>
                  <td className="num">{formatDecimal(x.valueA)}</td>
                  <td className="num">{formatDecimal(x.valueB)}</td>
                  <td className="wrap" title={x.classification}>
                    {classificationText(x)}
                  </td>
                  <td>
                    <span className={`badge tone-${severityTone(x.severity)}`}>{severityLabel(x.severity)}</span>
                  </td>
                  <td>{x.component ? (COMPONENTS[x.component] ?? x.component) : x.severity === "ERROR" ? "Todos sus componentes" : "—"}</td>
                  <td>{statusLabel(x.status)}</td>
                  <td className="wrap">{x.guidance ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Run />
    </Suspense>
  );
}
