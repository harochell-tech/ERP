"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Loading, NoPermission, ReasonAction } from "@/components/ui";
import { COMPONENTS, formatDate, formatDateTime, statusLabel, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Period = Schemas["PeriodView"];
type ComponentState = Schemas["ComponentStateView"];

/** T-13 close, P-8 / T-14 reopen with a second approver. Close and reopen need a recent re-authentication (E-PR18b-6). */
function ComponentActions({ period, state, onDone }: { period: Period; state: ComponentState; onDone: () => void }) {
  const { can } = useSession();
  const tag = `${period.periodId}:${state.component}`;
  const close = useCommand(`close:${tag}`, "/api/v1/companies/{companyId}/reconciliation/close-component");
  const request = useCommand(`reopen:${tag}`, "/api/v1/companies/{companyId}/reconciliation/request-reopen");
  const approve = useCommand(`approve-reopen:${tag}`, "/api/v1/companies/{companyId}/reconciliation/approve-reopen");
  const reject = useCommand(`reject-reopen:${tag}`, "/api/v1/companies/{companyId}/reconciliation/reject-reopen");
  const busy = close.busy || request.busy || approve.busy || reject.busy;
  const pending = period.reopenRequests.find((r) => r.component === state.component && r.status === "REQUESTED");
  const after = (response: unknown) => {
    if (response) {
      onDone();
    }
  };

  return (
    <>
      <span className="actions">
        {(state.status === "OPEN" || state.status === "REOPENED") && can("period_component:close") ? (
          <button type="button" disabled={busy} onClick={async () => after(await close.run({ periodId: period.periodId, component: state.component }))}>
            Cerrar
          </button>
        ) : null}
        {state.status === "CLOSED" && !pending && can("period_component:reopen") ? (
          <ReasonAction
            label="Solicitar reapertura"
            busy={busy}
            onConfirm={async (reason) => after(await request.run({ periodId: period.periodId, component: state.component, reason }))}
          />
        ) : null}
        {pending && can("period_component:second_approve") ? (
          <>
            <button type="button" disabled={busy} onClick={async () => after(await approve.run({ requestId: pending.requestId }))}>
              Aprobar reapertura
            </button>
            <ReasonAction label="Rechazar reapertura" busy={busy} onConfirm={async (reason) => after(await reject.run({ requestId: pending.requestId, reason }))} />
          </>
        ) : null}
      </span>
      {pending ? (
        <div className="muted">
          Reapertura solicitada por {pending.requestedBy ?? "—"} el {formatDateTime(pending.requestedAt)}: {pending.reason}
        </div>
      ) : null}
      <ErrorBox error={close.error ?? request.error ?? approve.error ?? reject.error} />
    </>
  );
}

export default function Periods() {
  const { companyId, can } = useSession();
  const [year, setYear] = useState(() => Number(todayInDominicanRepublic().slice(0, 4)));
  const { data, error, reload } = useLoad(
    can("period:read") ? () => query("/api/v1/companies/{companyId}/reconciliation/periods", { path: { companyId }, query: { year } }) : null,
    [companyId, year],
  );

  if (!can("period:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Períodos y cierre</h1>
      <div className="actions">
        <button type="button" onClick={() => setYear((y) => y - 1)}>
          ← {year - 1}
        </button>
        <strong>{year}</strong>
        <button type="button" onClick={() => setYear((y) => y + 1)}>
          {year + 1} →
        </button>
      </div>
      <p className="muted">Antes de cerrar ejecute las conciliaciones: un error en una conciliación que bloquea el componente impide el cierre.</p>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay períodos abiertos para {year}.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Período</th>
              <th>Componente</th>
              <th>Estado</th>
              <th>Cerrado por</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.items.flatMap((period) =>
              period.components.map((state) => (
                <tr key={`${period.periodId}:${state.component}`}>
                  <td>
                    {formatDate(period.startsOn)} – {formatDate(period.endsOn)}
                  </td>
                  <td>{COMPONENTS[state.component] ?? state.component}</td>
                  <td>{statusLabel(state.status)}</td>
                  <td>{state.closedBy ? `${state.closedBy} (${formatDateTime(state.closedAt)})` : "—"}</td>
                  <td>
                    <ComponentActions period={period} state={state} onDone={reload} />
                  </td>
                </tr>
              )),
            )}
          </tbody>
        </table>
      )}
    </>
  );
}
