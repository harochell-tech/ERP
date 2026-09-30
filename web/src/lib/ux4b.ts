// UX4-03: helpers shared by the screens of part 2 of UX wave 4 (pure where possible, unit-tested in tests/unit/ux4b.test.ts).
import { ApiError, buildUrl } from "@/api/client";
import type { paths } from "@/api/schema";

type PostQueryPath = {
  [P in keyof paths]: P extends `${string}/preview` ? (paths[P] extends { post: object } ? P : never) : never;
}[keyof paths];

type PostQueryBody<P extends PostQueryPath> = paths[P] extends { post: { requestBody?: { content: { "application/json": infer B } } } } ? B : never;

type PostQueryResult<P extends PostQueryPath> = paths[P] extends {
  post: { responses: { 200: { content: { "application/json": infer R } } } };
}
  ? R
  : never;

/**
 * E-UX4-3: a preview is a query whose input (lines) travels in a JSON body, so it is a POST: the anti-CSRF header as every POST,
 * no Idempotency-Key (nothing is written). Decimals travel as strings.
 */
export async function previewQuery<P extends PostQueryPath>(path: P, companyId: string, body: PostQueryBody<P>): Promise<PostQueryResult<P>> {
  const response = await fetch(buildUrl(path, { path: { companyId } }), {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", Accept: "application/json", "X-Rochell-Csrf": "1" },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    let code = `HTTP_${response.status}`;
    let detail = response.statusText;
    let correlationId: string | undefined;
    if (response.headers.get("Content-Type")?.includes("json")) {
      const problem = (await response.json()) as { code?: string; detail?: string; correlationId?: string };
      code = problem.code ?? code;
      detail = problem.detail ?? detail;
      correlationId = problem.correlationId;
    }
    throw new ApiError(response.status, code, detail, correlationId);
  }
  return (await response.json()) as PostQueryResult<P>;
}

// ---------------------------------------------------------------------------------------------------------------------------
// Lists (C-32, V-40, A-19): a search box that ignores case and accents; every word must appear in one of the fields.

export function foldText(text: string): string {
  return text.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
}

export function matchesSearch(search: string, ...fields: readonly (string | null | undefined)[]): boolean {
  const words = foldText(search).split(/\s+/).filter(Boolean);
  if (words.length === 0) {
    return true;
  }
  const haystack = fields.filter((f): f is string => Boolean(f)).map(foldText);
  return words.every((w) => haystack.some((f) => f.includes(w)));
}

/** "CEM-GRIS — CEM-GRIS" reads once (P-37, C-32): the description only when it says more than the code. */
export function codeAndName(code: string, name: string | null | undefined): string {
  const described = name?.trim();
  return described && foldText(described) !== foldText(code) ? `${code} — ${described}` : code;
}

// ---------------------------------------------------------------------------------------------------------------------------
// e-CF types (G-25): "31 — Crédito fiscal". UX4-02 has the same helper (ecfTypeLabel in lib/ux4a.ts); one goes when both merge.

const ECF_TYPES: Readonly<Record<string, string>> = {
  "31": "Crédito fiscal",
  "32": "Consumo",
  "33": "Nota de débito",
  "34": "Nota de crédito",
  "41": "Compras",
  "43": "Gastos menores",
  "44": "Regímenes especiales",
  "45": "Gubernamental",
  "46": "Exportaciones",
  "47": "Pagos al exterior",
};

export function ecfTypeLabel(ecfType: string | null | undefined): string {
  if (!ecfType) {
    return "—";
  }
  const name = ECF_TYPES[ecfType];
  return name ? `${ecfType} — ${name}` : ecfType;
}

// ---------------------------------------------------------------------------------------------------------------------------
// Inicio (G-15): configuration waiting for its approver. Counts, not amounts: the drafts someone else prepared (four eyes).

type IsMine = (actor: string | null | undefined) => boolean;

/** DRAFT items the reader did not prepare (account role maps, report structures). */
export function countDraftsToApprove(items: readonly { status: string; preparedBy: string | null }[], isMine: IsMine): number {
  return items.filter((i) => i.status === "DRAFT" && !isMine(i.preparedBy)).length;
}

/** DRAFT policy versions the reader did not prepare, over every policy. */
export function countPolicyDraftsToApprove(policies: readonly { versions: readonly { status: string; preparedBy: string | null }[] }[], isMine: IsMine): number {
  return policies.reduce((n, p) => n + countDraftsToApprove(p.versions, isMine), 0);
}

/** Fiscal rule versions READY to activate (source linked, tests passed) that the reader did not configure. */
export function countFiscalRulesToActivate(rules: readonly { versions: readonly { status: string; configuredBy: string | null }[] }[], isMine: IsMine): number {
  return rules.reduce((n, r) => n + r.versions.filter((v) => v.status === "READY" && !isMine(v.configuredBy)).length, 0);
}

/**
 * The REVENUE_ACCOUNTING policy's authorization_expiry_alert_days in force today (a whole number of days), or null when no
 * version in force carries it.
 */
export function authorizationAlertDays(
  policies: readonly { policyCode: string; versions: readonly { status: string; effectiveFrom: string; effectiveTo: string | null; parameters: Record<string, string> }[] }[],
  today: string,
): number | null {
  const policy = policies.find((p) => p.policyCode === "REVENUE_ACCOUNTING");
  const inForce = policy?.versions.find((v) => v.status === "ACTIVE" && v.effectiveFrom <= today && (v.effectiveTo === null || v.effectiveTo > today));
  const value = inForce?.parameters["authorization_expiry_alert_days"];
  return value !== undefined && /^\d+$/.test(value) ? Number.parseInt(value, 10) : null;
}

/**
 * ACTIVE authorizations whose validity ends within the alert window (the server's `daysToExpiry`: 0 on the last day, negative
 * once past). None without a window: the policy decides, not the screen.
 */
export function countExpiringAuthorizations(items: readonly { status: string; daysToExpiry: number | null }[], alertDays: number | null): number {
  if (alertDays === null) {
    return 0;
  }
  return items.filter((a) => a.status === "ACTIVE" && a.daysToExpiry !== null && a.daysToExpiry <= alertDays).length;
}

// ---------------------------------------------------------------------------------------------------------------------------
// Políticas (G-22): the policies grouped by theme, and inside a policy its parameters by theme (web grouping only).

/** The page's themes, in order, with the policies each gathers. A policy not listed falls into "Otras". */
export const POLICY_THEMES: readonly { theme: string; policies: readonly string[] }[] = [
  { theme: "Compras e inventario", policies: ["PURCHASING", "INVENTORY"] },
  { theme: "Ventas y crédito", policies: ["CREDIT", "REVENUE_ACCOUNTING"] },
  { theme: "Producción", policies: ["PRODUCTION"] },
  { theme: "Contabilización y tesorería", policies: ["POSTING", "TREASURY"] },
];

/** Parameters shown under a theme of their own inside their policy (authorization_expiry_alert_days is a fiscal matter). */
const PARAMETER_THEMES: Readonly<Record<string, string>> = {
  authorization_expiry_alert_days: "Fiscal",
  unbilled_delivery_presentation: "Presentación contable",
  delivery_open_alert_hours: "Alertas de despacho y facturación",
  unbilled_aging_alert_days: "Alertas de despacho y facturación",
  po_approval_limit: "Aprobación de órdenes",
  po_approval_step_up_threshold: "Aprobación de órdenes",
  match_price_tolerance_pct: "Cuadre de facturas con la orden y la recepción",
  match_qty_tolerance_pct: "Cuadre de facturas con la orden y la recepción",
  match_amount_tolerance_abs: "Cuadre de facturas con la orden y la recepción",
  receipt_tolerance_pct: "Recepción",
};

export function groupPoliciesByTheme<P extends { policyCode: string }>(policies: readonly P[]): { theme: string; policies: P[] }[] {
  const groups = POLICY_THEMES.map((t) => ({ theme: t.theme, policies: policies.filter((p) => t.policies.includes(p.policyCode)) }));
  const known = new Set(POLICY_THEMES.flatMap((t) => t.policies));
  groups.push({ theme: "Otras", policies: policies.filter((p) => !known.has(p.policyCode)) });
  return groups.filter((g) => g.policies.length > 0);
}

/**
 * A policy's parameters by theme, in the order of their first appearance; one group (theme null) when none of them has a theme
 * of its own, so a small policy keeps its plain table.
 */
export function groupParametersByTheme<D extends { paramCode: string }>(definitions: readonly D[]): { theme: string | null; definitions: D[] }[] {
  if (!definitions.some((d) => PARAMETER_THEMES[d.paramCode])) {
    return [{ theme: null, definitions: [...definitions] }];
  }
  const groups: { theme: string | null; definitions: D[] }[] = [];
  for (const d of definitions) {
    const theme = PARAMETER_THEMES[d.paramCode] ?? "General";
    const group = groups.find((g) => g.theme === theme);
    if (group) {
      group.definitions.push(d);
    } else {
      groups.push({ theme, definitions: [d] });
    }
  }
  return groups;
}
