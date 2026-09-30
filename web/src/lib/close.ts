// UX3-02 (E-UX3-1): the guided close. Pure helpers, unit-tested: the periods grouped by month and each component's checklist from
// the server's close readiness (the rules are the server's; the screen only words them).
import type { Schemas } from "@/api/client";

type Period = Schemas["PeriodView"];
type ReadinessComponent = Schemas["CloseReadinessComponent"];

const MONTHS = ["Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio", "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre"];

/** "2026-08-01" → "Agosto 2026". */
export function monthLabel(isoDate: string): string {
  const [year, month] = isoDate.split("-");
  const name = MONTHS[Number(month) - 1];
  return name ? `${name} ${year}` : isoDate;
}

export interface MonthGroup<P> {
  /** yyyy-MM */
  key: string;
  label: string;
  periods: P[];
}

/** One card per month (by the period's first day), in calendar order. */
export function groupPeriodsByMonth<P extends Pick<Period, "startsOn">>(periods: readonly P[]): MonthGroup<P>[] {
  const groups = new Map<string, MonthGroup<P>>();
  for (const period of [...periods].sort((a, b) => a.startsOn.localeCompare(b.startsOn))) {
    const key = period.startsOn.slice(0, 7);
    const group = groups.get(key) ?? { key, label: monthLabel(period.startsOn), periods: [] };
    group.periods.push(period);
    groups.set(key, group);
  }
  return [...groups.values()];
}

export type CheckState = "ok" | "pending" | "error";

export interface CheckItem {
  key: string;
  label: string;
  state: CheckState;
  detail: string;
  /** The run to open (a blocking reconciliation's latest run for the period). */
  runId?: string | null;
}

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

/** The checklist of one component: ended, sealed and each blocking reconciliation with its latest run for the period. */
export function readinessChecklist(component: ReadinessComponent, formatRunAt: (value: string) => string = (v) => v): CheckItem[] {
  const items: CheckItem[] = [
    {
      key: "ended",
      label: "El mes terminó",
      state: component.ended ? "ok" : "pending",
      detail: component.ended ? "Sí" : "Aún no termina: se cierra cuando pase el último día del mes.",
    },
    {
      key: "sealed",
      label: "Registros sellados",
      state: component.sealed ? "ok" : "pending",
      detail: component.sealed ? "Sí" : "Hay registros del mes sin sellar todavía; el sellado es automático, vuelva a verificar en unos minutos.",
    },
  ];
  for (const recon of component.reconciliations) {
    let state: CheckState;
    let detail: string;
    if (recon.runId === null || recon.blockingErrors === null) {
      state = "pending";
      detail = "Sin verificar para este mes: pulse «Verificar ahora».";
    } else if (recon.blockingErrors > 0) {
      state = "error";
      detail = `${plural(recon.blockingErrors, "error bloquea", "errores bloquean")} el cierre (verificada ${formatRunAt(recon.runAt ?? "")}).`;
    } else {
      state = "ok";
      detail = `Sin errores que bloqueen (verificada ${formatRunAt(recon.runAt ?? "")}).`;
    }
    items.push({ key: recon.reconCode, label: recon.name, state, detail, runId: recon.runId });
  }
  return items;
}

export type CloseAvailability = "closed" | "not-ended" | "ready" | "blocked";

/** Whether "Cerrar" is offered: only for an OPEN / REOPENED component of an ended month that the server reports ready. */
export function closeAvailability(status: string, readiness: Pick<ReadinessComponent, "ended" | "ready"> | undefined): CloseAvailability {
  if (status !== "OPEN" && status !== "REOPENED") {
    return "closed";
  }
  if (readiness === undefined) {
    return "blocked";
  }
  if (!readiness.ended) {
    return "not-ended";
  }
  return readiness.ready ? "ready" : "blocked";
}

/** The codes "Verificar ahora" runs for a component (its blocking reconciliations). */
export function blockingCodes(component: Pick<ReadinessComponent, "reconciliations">): string[] {
  return component.reconciliations.map((r) => r.reconCode);
}
