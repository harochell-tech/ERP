"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { COMPONENTS, formatDateTime } from "@/lib/labels";
import { severityLabel, severityTone } from "@/lib/reconciliations";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

/** Reconciliation runs. UX3-02 (E-UX3-2): names and guidance from the server (`ListReconciliationDefinitions`). */
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
                  <td className="wrap">
                    <Link href={`/cierre/conciliacion/?id=${r.runId}`}>{r.name}</Link> <span className="muted mono">{r.reconCode}</span>
                  </td>
                  <td>{formatDateTime(r.asOf)}</td>
                  <td className="num">{formatDecimal(r.totalA)}</td>
                  <td className="num">{formatDecimal(r.totalB)}</td>
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
