"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmAction, ErrorBox, Loading, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { blockingCodes, closeAvailability, groupPeriodsByMonth, readinessChecklist, type CheckState } from "@/lib/close";
import { COMPONENTS, formatDate, formatDateTime, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Period = Schemas["PeriodView"];
type ComponentState = Schemas["ComponentStateView"];
type Readiness = Schemas["CloseReadiness"];
type ReadinessComponent = Schemas["CloseReadinessComponent"];

const CHECK_MARK: Readonly<Record<CheckState, string>> = { ok: "✓", pending: "…", error: "✕" };
const CHECK_TONE: Readonly<Record<CheckState, string>> = { ok: "done", pending: "attention", error: "error" };

/**
 * T-13 close, P-8 / T-14 reopen with a second approver. Close and reopen need a recent re-authentication (E-PR18b-6).
 * UX3-02 (E-UX3-1): "Cerrar" only for an ended month the server reports ready; "Verificar ahora" runs the blocking
 * reconciliations at the period's end.
 */
function ComponentActions({
  period,
  state,
  readiness,
  onDone,
}: {
  period: Period;
  state: ComponentState;
  readiness: ReadinessComponent | undefined;
  onDone: () => void;
}) {
  const { can } = useSession();
  const tag = `${period.periodId}:${state.component}`;
  const componentName = COMPONENTS[state.component] ?? state.component;
  const periodName = `${formatDate(period.startsOn)} – ${formatDate(period.endsOn)}`;
  const close = useCommand(`close:${tag}`, "/api/v1/companies/{companyId}/reconciliation/close-component", `${componentName} cerrado para ${periodName}.`);
  const verify = useCommand(
    `verify:${tag}`,
    "/api/v1/companies/{companyId}/reconciliation/run-reconciliation",
    `Conciliaciones de ${componentName} verificadas al ${formatDate(period.endsOn)}.`,
  );
  const request = useCommand(
    `reopen:${tag}`,
    "/api/v1/companies/{companyId}/reconciliation/request-reopen",
    `Reapertura de ${componentName} (${periodName}) solicitada; falta la aprobación de otra persona.`,
  );
  const approve = useCommand(`approve-reopen:${tag}`, "/api/v1/companies/{companyId}/reconciliation/approve-reopen", `${componentName} reabierto para ${periodName}.`);
  const reject = useCommand(`reject-reopen:${tag}`, "/api/v1/companies/{companyId}/reconciliation/reject-reopen", `Reapertura de ${componentName} (${periodName}) rechazada.`);
  const busy = close.busy || verify.busy || request.busy || approve.busy || reject.busy;
  const pending = period.reopenRequests.find((r) => r.component === state.component && r.status === "REQUESTED");
  const availability = closeAvailability(state.status, readiness);
  const codes = readiness ? blockingCodes(readiness) : [];
  const after = (response: unknown) => {
    if (response) {
      onDone();
    }
  };

  return (
    <>
      <span className="actions">
        {availability !== "closed" && readiness?.ended && codes.length > 0 && can("reconciliation:run") ? (
          <button
            type="button"
            disabled={busy}
            onClick={async () => after(await verify.run({ reconCodes: codes, cutoffDate: period.endsOn }))}
          >
            {verify.busy ? "Verificando…" : "Verificar ahora"}
          </button>
        ) : null}
        {availability === "ready" && can("period_component:close") ? (
          <ConfirmAction
            label="Cerrar"
            title={`¿Cerrar ${componentName} de ${periodName}?`}
            consequence="El componente queda cerrado y el sistema guarda su foto sellada: ya no se registran documentos de ese mes en él. Solo se reabre con una solicitud y la aprobación de otra persona."
            stepUp
            busy={busy}
            className="primary"
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
      {availability === "not-ended" ? <p className="muted">Aún no termina: el mes se cierra cuando haya pasado su último día.</p> : null}
      {availability === "blocked" && readiness ? <p className="muted">Todavía no se puede cerrar: resuelva los puntos pendientes de la lista.</p> : null}
      {pending ? (
        <div className="muted">
          Reapertura solicitada por {pending.requestedBy ?? "—"} el {formatDateTime(pending.requestedAt)}: {pending.reason}
        </div>
      ) : null}
      <ErrorBox error={close.error ?? verify.error ?? request.error ?? approve.error ?? reject.error} />
    </>
  );
}

function ReadinessSummary({ status, readiness, pendingReopen }: { status: string; readiness: ReadinessComponent | undefined; pendingReopen: boolean }) {
  if (pendingReopen) {
    return <StatusBadge status="REQUESTED" label="Reapertura solicitada" />;
  }
  switch (closeAvailability(status, readiness)) {
    case "closed":
      return <StatusBadge status={status} />;
    case "not-ended":
      return <StatusBadge status="PLANNED" label="Aún no termina" />;
    case "ready":
      return <StatusBadge status="APPROVED" label="Listo para cerrar" />;
    default:
      return readiness ? <StatusBadge status="BLOCKED" label="Pendiente de verificar" /> : <StatusBadge status={status} />;
  }
}

function ComponentRow({ period, state, readiness, onDone }: { period: Period; state: ComponentState; readiness: ReadinessComponent | undefined; onDone: () => void }) {
  const name = COMPONENTS[state.component] ?? state.component;
  const pendingReopen = period.reopenRequests.some((r) => r.component === state.component && r.status === "REQUESTED");
  const open = state.status === "OPEN" || state.status === "REOPENED";
  return (
    <details className="close-component" data-testid={`component-${state.component}`}>
      <summary>
        <span className="close-component-name">{name}</span> <ReadinessSummary status={state.status} readiness={readiness} pendingReopen={pendingReopen} />
      </summary>
      <div className="close-component-body">
        {state.closedBy ? (
          <p className="muted">
            Cerrado por {state.closedBy} ({formatDateTime(state.closedAt)})
          </p>
        ) : null}
        {open && readiness ? (
          <ul className="checklist" aria-label={`Lista de cierre: ${name}`}>
            {readinessChecklist(readiness, formatDateTime).map((item) => (
              <li key={item.key} data-testid={`check-${item.key}`}>
                <span className={`badge tone-${CHECK_TONE[item.state]}`} aria-hidden="true">
                  {CHECK_MARK[item.state]}
                </span>{" "}
                <strong>{item.label}</strong>: {item.detail}
                {item.runId ? (
                  <>
                    {" "}
                    <Link href={`/cierre/conciliacion/?id=${item.runId}`}>Ver resultado</Link>
                  </>
                ) : null}
              </li>
            ))}
          </ul>
        ) : null}
        <ComponentActions period={period} state={state} readiness={readiness} onDone={onDone} />
      </div>
    </details>
  );
}

function PeriodBlock({ period, today, onDone }: { period: Period; today: string; onDone: () => void }) {
  const { companyId } = useSession();
  // The readiness of a month that has begun (a future month has not ended; nothing to verify).
  const started = period.startsOn <= today;
  const hasOpen = period.components.some((c) => c.status === "OPEN" || c.status === "REOPENED");
  const readiness = useLoad<Readiness>(
    started && hasOpen
      ? () => query("/api/v1/companies/{companyId}/reconciliation/periods/{periodId}/close-readiness", { path: { companyId, periodId: period.periodId } })
      : null,
    [companyId, period.periodId],
  );
  const reload = () => {
    readiness.reload();
    onDone();
  };
  const byComponent = (component: string): ReadinessComponent | undefined =>
    started ? readiness.data?.components.find((c) => c.component === component) : { component, status: "OPEN", ended: false, sealed: false, ready: false, reconciliations: [] };
  return (
    <>
      <p className="muted">
        {formatDate(period.startsOn)} – {formatDate(period.endsOn)}
      </p>
      <ErrorBox error={readiness.error} />
      {period.components.map((state) => (
        <ComponentRow key={state.component} period={period} state={state} readiness={byComponent(state.component)} onDone={reload} />
      ))}
    </>
  );
}

export default function Periods() {
  const { companyId, can } = useSession();
  const today = todayInDominicanRepublic();
  const [year, setYear] = useState(() => Number(today.slice(0, 4)));
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
      <p className="muted">
        Cada componente muestra su lista de cierre: el mes terminó, los registros están sellados y las conciliaciones que lo bloquean no tienen
        errores. «Verificar ahora» ejecuta esas conciliaciones al último día del mes.
      </p>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay períodos abiertos para {year}.</p>
      ) : (
        groupPeriodsByMonth(data.items).map((month) => (
          <section key={month.key} className="card" data-testid={`month-${month.key}`} aria-label={month.label}>
            <h2>{month.label}</h2>
            {month.periods.map((period) => (
              <PeriodBlock key={period.periodId} period={period} today={today} onDone={reload} />
            ))}
          </section>
        ))
      )}
    </>
  );
}
