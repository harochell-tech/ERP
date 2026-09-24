"use client";

import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, NoPermission } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { COMPONENTS, formatDateTime, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

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
  return (
    <>
      <h1>
        {run.reconCode} — {statusLabel(run.status)}
      </h1>
      <p>
        {run.description}. Ejecutada {formatDateTime(run.asOf)}. Total A {formatDecimal(run.totalA)}, total B {formatDecimal(run.totalB)}, diferencia{" "}
        {formatDecimal(run.difference)}.
      </p>
      {exceptions.length === 0 ? (
        <p>Sin excepciones.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Clave</th>
              <th className="num">Valor A</th>
              <th className="num">Valor B</th>
              <th>Clasificación</th>
              <th>Severidad</th>
              <th>Bloquea</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {exceptions.map((x) => (
              <tr key={x.exceptionId}>
                <td>{x.matchKey}</td>
                <td className="num">{formatDecimal(x.valueA)}</td>
                <td className="num">{formatDecimal(x.valueB)}</td>
                <td>{x.classification}</td>
                <td>{x.severity}</td>
                <td>{x.component ? (COMPONENTS[x.component] ?? x.component) : x.severity === "ERROR" ? "Ambos componentes" : "—"}</td>
                <td>{statusLabel(x.status)}</td>
              </tr>
            ))}
          </tbody>
        </table>
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
