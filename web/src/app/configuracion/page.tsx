"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { Loading, NoPermission } from "@/components/ui";
import { areaLabel, areaLight, LIGHT_LABELS, missingLabel, SETUP_AREAS, setupProgress, STEP_STATUS_LABELS, stepInfo, type SetupNames } from "@/lib/setup";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// UX2-02 (E-UX2-9/10): the Centro de configuración. The 19 steps and their status are the server's (Reconciliation.GetSetupStatus,
// computed from the data); the screen names them in Spanish, lights each area and links each step to the screen that resolves it.

const STEP_TONES: Readonly<Record<string, string>> = { DONE: "done", WARNING: "attention", PENDING: "error" };

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("configuration:read");
  const status = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/reconciliation/setup-status", { path: { companyId } }) : null, [companyId]);
  const roles = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/finance/account-roles", { path: { companyId } }) : null, [companyId]);
  const policies = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/finance/accounting-policies", { path: { companyId } }) : null, [companyId]);

  if (!allowed) {
    return <NoPermission />;
  }
  if (status.data === null) {
    return <Loading error={status.error} />;
  }
  const names: SetupNames = {
    accountRoles: Object.fromEntries((roles.data?.items ?? []).map((r) => [r.roleCode, r.name ?? r.roleCode])),
    policies: Object.fromEntries((policies.data?.items ?? []).map((p) => [p.policyCode, p.name ?? p.policyCode])),
  };
  const steps = [...status.data.steps].sort((a, b) => a.order - b.order);
  const progress = setupProgress(status.data);
  const areas = SETUP_AREAS.map((a) => ({ ...a, steps: steps.filter((s) => s.area === a.code) })).filter((a) => a.steps.length > 0);
  // An area the server adds later still shows.
  for (const s of steps) {
    if (!areas.some((a) => a.code === s.area)) {
      areas.push({ code: s.area, label: areaLabel(s.area), steps: steps.filter((x) => x.area === s.area) });
    }
  }
  return (
    <>
      <h1>Centro de configuración</h1>
      <p>
        {status.data.complete ? (
          <strong data-testid="setup-complete">La puesta en marcha está completa.</strong>
        ) : (
          <>
            <strong data-testid="setup-progress">
              {progress.done} de {progress.total} pasos listos
            </strong>{" "}
            <span className="muted">— cada paso se calcula con los datos del sistema; se marca listo cuando no falta nada.</span>
          </>
        )}
      </p>
      <progress value={progress.done} max={progress.total} aria-label="Pasos listos" style={{ width: "100%" }} />
      <h2>Áreas</h2>
      <div className="areas">
        {areas.map((a) => {
          const light = areaLight(a.steps);
          return (
            <div key={a.code} className="stat" data-testid={`area:${a.code}`} data-light={light}>
              <span>{a.label}</span>
              <div>
                <span className={`light light-${light}`} aria-hidden="true" />
                {LIGHT_LABELS[light]}
              </div>
            </div>
          );
        })}
      </div>
      <h2>Pasos</h2>
      <ol className="plain-list" style={{ paddingLeft: 0, listStyle: "none" }}>
        {steps.map((s) => {
          const info = stepInfo(s.code);
          return (
            <li key={s.code} className="card" data-testid={`step:${s.code}`}>
              <div className="inline-form" style={{ alignItems: "center", justifyContent: "space-between", width: "100%" }}>
                <strong>
                  {s.order}. {info.title}
                </strong>
                <span className={`badge tone-${STEP_TONES[s.status] ?? "neutral"}`}>{STEP_STATUS_LABELS[s.status] ?? s.status}</span>
              </div>
              <div className="muted" style={{ fontSize: 13, margin: "4px 0" }}>
                {areaLabel(s.area)}
              </div>
              {s.missing.length > 0 ? (
                <>
                  <div style={{ fontSize: 14 }}>Falta:</div>
                  <ul className="plain-list">
                    {s.missing.map((m) => (
                      <li key={m}>{missingLabel(s.code, m, names)}</li>
                    ))}
                  </ul>
                </>
              ) : null}
              <div style={{ marginTop: 6 }}>
                <Link href={info.href}>{s.status === "DONE" ? `Ver en ${info.screen}` : `Resolver en ${info.screen}`}</Link>
              </div>
            </li>
          );
        })}
      </ol>
    </>
  );
}
