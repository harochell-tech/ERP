// FIS1b-07 (E-FIS1b-9): the proforma as a collection document, in words. Pure, unit-tested in tests/unit/proformas.test.ts.
import type { Schemas } from "@/api/client";

export type Proforma = Schemas["ProformaSummary"];

const STATUS: Record<string, string> = { OPEN: "Abierta", INVOICED: "Facturada", VOIDED: "Anulada" };

const CERTIFICATION: Record<string, string> = { NONE: "Sin certificación", IN_PROCESS: "Certificación en trámite", CERTIFIED: "Certificada" };

export function proformaStatusLabel(status: string): string {
  return STATUS[status] ?? status;
}

export function certificationLabel(certification: string): string {
  return CERTIFICATION[certification] ?? certification;
}

/** The badge tone: a certified proforma reads as ready, one in process as pending, none as neutral. */
export function certificationStatus(certification: string): string {
  return certification === "CERTIFIED" ? "ACTIVE" : certification === "IN_PROCESS" ? "PENDING_VERIFICATION" : "DRAFT";
}

/** What the proforma collects, in words. */
export function collectsLabel(collectsItbis: boolean): string {
  return collectsItbis ? "Se cobra con ITBIS" : "Se cobra sin ITBIS";
}

/** "Vencida hace N días" / "Vence el …" / "Cobrada" for an open proforma. */
export function dueText(proforma: Pick<Proforma, "status" | "balance" | "daysOverdue">, dueDate: string): string {
  if (proforma.status !== "OPEN") {
    return "—";
  }
  if (Number(proforma.balance) === 0) {
    return "Cobrada";
  }
  return proforma.daysOverdue > 0 ? `Vencida hace ${proforma.daysOverdue} día${proforma.daysOverdue === 1 ? "" : "s"}` : `Vence el ${dueDate}`;
}

/**
 * The authorizations that can exempt exactly these proformas: those that cite every one of them. `cited` maps an authorization to
 * the proformas it cites.
 */
export function authorizationsFor(chosen: readonly string[], cited: ReadonlyMap<string, ReadonlySet<string>>): string[] {
  if (chosen.length === 0) {
    return [];
  }
  return [...cited].filter(([, proformas]) => chosen.every((id) => proformas.has(id))).map(([authorizationId]) => authorizationId);
}
