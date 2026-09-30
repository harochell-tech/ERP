"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { COMPONENTS, formatDate, formatDateTime } from "@/lib/labels";
import { severityLabel, severityTone } from "@/lib/reconciliations";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { sideLabels } from "@/lib/ux4a-auditoria";

/**
 * Reconciliation runs. UX3-02 (E-UX3-2): names and guidance from the server (`ListReconciliationDefinitions`). UX4-02 (A-22, A-23):
 * first the latest run of every reconciliation with its cutoff (`/reconciliation/runs/latest`), the totals under their own labels;
 * the full history of runs folded below.
 */
export default function Reconciliations() {
  const { companyId, can } = useSession();
  const [code, setCode] = useState("");
  const { data, error, reload } = useLoad(
    can("reconciliation:read")
      ? () => query("/api/v1/companies/{companyId}/reconciliation/runs", { path: { companyId }, query: { limit: 100, reconCode: code || undefined } })
      : null,
    [companyId, code],
  );
  const definitions = useLoad(
    can("reconciliation:read") ? () => query("/api/v1/companies/{companyId}/reconciliation/definitions", { path: { companyId } }) : null,
    [companyId],
  );
  const latest = useLoad(
    can("reconciliation:read") ? () => query("/api/v1/companies/{companyId}/reconciliation/runs/latest", { path: { companyId } }) : null,
    [companyId],
  );
  const chosen = definitions.data?.items.find((d) => d.reconCode === code);
  const run = useCommand("run-reconciliation", "/api/v1/companies/{companyId}/reconciliation/run-reconciliation", "Conciliaciones ejecutadas: revise el resultado de cada una.");

  if (!can("reconciliation:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Conciliaciones</h1>
      <div className="inline-form">
        <Field label="Conciliación">
          <select aria-label="Conciliación" value={code} onChange={(e) => setCode(e.target.value)}>
            <option value="">Todas</option>
            {(definitions.data?.items ?? []).map((d) => (
              <option key={d.reconCode} value={d.reconCode}>
                {d.name}
              </option>
            ))}
          </select>
        </Field>
        {can("reconciliation:run") ? (
          <div className="actions">
            <button
              type="button"
              disabled={run.busy}
              onClick={async () => {
                if (await run.run({ reconCodes: code ? [code] : null }, undefined, chosen ? `Conciliación «${chosen.name}» ejecutada: revise el resultado.` : undefined)) {
                  reload();
                  latest.reload();
                }
              }}
            >
              {chosen ? "Ejecutar esta conciliación" : "Ejecutar todas las conciliaciones"}
            </button>
          </div>
        ) : null}
      </div>
      {chosen ? (
        <div className="alert-block guidance">
          <strong>Qué hacer con sus hallazgos</strong>
          {chosen.guidance}
        </div>
      ) : null}
      <ErrorBox error={run.error} />
      <h2>Último resultado de cada conciliación</h2>
      {latest.data === null ? (
        <Loading error={latest.error} />
      ) : (
        <div className="table-wrap">
          <table data-testid="latest-runs">
            <thead>
              <tr>
                <th>Conciliación</th>
                <th>Ejecutada</th>
                <th>Corte al</th>
                <th>Totales</th>
                <th className="num">Diferencia</th>
                <th>Resultado</th>
                <th className="num">Excepciones</th>
              </tr>
            </thead>
            <tbody>
              {latest.data.items
                .filter((l) => code === "" || l.reconCode === code)
                .map((l) => {
                  const r = l.latestRun;
                  const labels = sideLabels(l);
                  return (
                    <tr key={l.reconCode} data-testid={`latest-${l.reconCode}`}>
                      <td className="wrap">
                        {r ? <Link href={`/cierre/conciliacion/?id=${r.runId}`}>{l.name}</Link> : l.name} <span className="muted mono">{l.reconCode}</span>
                      </td>
                      {r ? (
                        <>
                          <td>{formatDateTime(r.asOf)}</td>
                          <td>{r.cutoffDate ? formatDate(r.cutoffDate) : "—"}</td>
                          <td className="wrap">
                            {r.totalA === null && r.totalB === null ? (
                              <span className="muted">Sin totales</span>
                            ) : (
                              <>
                                {labels.a}: <span className="mono">{formatDecimal(r.totalA)}</span>
                                <br />
                                {labels.b}: <span className="mono">{formatDecimal(r.totalB)}</span>
                              </>
                            )}
                          </td>
                          <td className="num">{formatDecimal(r.difference)}</td>
                          <td>
                            <StatusBadge status={r.status} />
                          </td>
                          <td className="num">{r.exceptionCount}</td>
                        </>
                      ) : (
                        <td colSpan={6} className="muted">
                          Nunca se ha ejecutado.
                        </td>
                      )}
                    </tr>
                  );
                })}
            </tbody>
          </table>
        </div>
      )}
      <details className="card" data-testid="run-history">
        <summary>Historial completo de ejecuciones</summary>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No se han ejecutado conciliaciones.</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Conciliación</th>
                <th>Fecha</th>
                <th>Corte al</th>
                <th>Totales</th>
                <th className="num">Diferencia</th>
                <th>Resultado</th>
                <th className="num">Excepciones</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((r) => (
                <tr key={r.runId}>
                  <td className="wrap">
                    <Link href={`/cierre/conciliacion/?id=${r.runId}`}>{r.name}</Link> <span className="muted mono">{r.reconCode}</span>
                  </td>
                  <td>{formatDateTime(r.asOf)}</td>
                  <td>{r.cutoffDate ? formatDate(r.cutoffDate) : "—"}</td>
                  <td className="wrap">
                    {r.totalA === null && r.totalB === null ? (
                      <span className="muted">Sin totales</span>
                    ) : (
                      <>
                        {sideLabels(r).a}: <span className="mono">{formatDecimal(r.totalA)}</span>
                        <br />
                        {sideLabels(r).b}: <span className="mono">{formatDecimal(r.totalB)}</span>
                      </>
                    )}
                  </td>
                  <td className="num">{formatDecimal(r.difference)}</td>
                  <td>
                    <StatusBadge status={r.status} />
                  </td>
                  <td className="num">{r.exceptionCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      </details>
      <details className="card">
        <summary>Qué revisa cada conciliación</summary>
        {definitions.data === null ? (
          <Loading error={definitions.error} />
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Conciliación</th>
                  <th>Severidad</th>
                  <th>Bloquea el cierre de</th>
                  <th>Qué hacer</th>
                </tr>
              </thead>
              <tbody>
                {definitions.data.items.map((d) => (
                  <tr key={d.reconCode}>
                    <td className="wrap">
                      {d.name} <span className="muted mono">{d.reconCode}</span>
                    </td>
                    <td>
                      <span className={`badge tone-${severityTone(d.severity)}`}>{severityLabel(d.severity)}</span>
                    </td>
                    <td className="wrap">{d.blockingComponents.length === 0 ? "—" : d.blockingComponents.map((c) => COMPONENTS[c] ?? c).join(", ")}</td>
                    <td className="wrap">{d.guidance}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </details>
    </>
  );
}
