"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmAction, ErrorBox, Loading, NoPermission, ReasonAction } from "@/components/ui";
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
  const componentName = COMPONENTS[state.component] ?? state.component;
  const periodName = `${formatDate(period.startsOn)} – ${formatDate(period.endsOn)}`;
  const close = useCommand(`close:${tag}`, "/api/v1/companies/{companyId}/reconciliation/close-component", `${componentName} cerrado para ${periodName}.`);
  const request = useCommand(
    `reopen:${tag}`,
    "/api/v1/companies/{companyId}/reconciliation/request-reopen",
    `Reapertura de ${componentName} (${periodName}) solicitada; falta la aprobación de otra persona.`,
  );
  const approve = useCommand(`approve-reopen:${tag}`, "/api/v1/companies/{companyId}/reconciliation/approve-reopen", `${componentName} reabierto para ${periodName}.`);
  const reject = useCommand(`reject-reopen:${tag}`, "/api/v1/companies/{companyId}/reconciliation/reject-reopen", `Reapertura de ${componentName} (${periodName}) rechazada.`);
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
          <ConfirmAction
            label="Cerrar"
            title={`¿Cerrar ${componentName} de ${periodName}?`}
            consequence="El componente queda cerrado y el sistema guarda su foto sellada: ya no se registran documentos de ese mes en él. Solo se reabre con una solicitud y la aprobación de otra persona."
            stepUp
            busy={busy}
            onConfirm={async () => after(await close.run({ periodId: period.periodId, component: state.component }))}
          />
        ) : null}
        {state.status === "CLOSED" && !pending && can("period_component:reopen") ? (
          <ReasonAction
            label="Solicitar reapertura"
            consequence={`Se pide reabrir ${componentName} de ${periodName}; otra persona debe aprobarlo.`}
            stepUp
            busy={busy}
            onConfirm={async (reason) => after(await request.run({ periodId: period.periodId, component: state.component, reason }))}
          />
        ) : null}
        {pending && can("period_component:second_approve") ? (
          <>
            <ConfirmAction
              label="Aprobar reapertura"
              title={`¿Reabrir ${componentName} de ${periodName}?`}
              consequence="El componente vuelve a quedar abierto y se pueden registrar o corregir documentos del mes; deberá cerrarse otra vez."
              stepUp
              busy={busy}
              onConfirm={async () => after(await approve.run({ requestId: pending.requestId }))}
            />
            <ReasonAction
              label="Rechazar reapertura"
              consequence="La solicitud queda rechazada y el componente sigue cerrado."
              stepUp
              busy={busy}
              onConfirm={async (reason) => after(await reject.run({ requestId: pending.requestId, reason }))}
            />
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
        <div className="table-wrap"><table>
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
                  <td className="wrap">{state.closedBy ? `${state.closedBy} (${formatDateTime(state.closedAt)})` : "—"}</td>
                  <td>
                    <ComponentActions period={period} state={state} onDone={reload} />
                  </td>
                </tr>
              )),
            )}
          </tbody>
        </table></div>
      )}
    </>
  );
}
