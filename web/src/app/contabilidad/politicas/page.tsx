"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmDialog, ErrorBox, Field, Loading, NoPermission, StatusBadge, SuffixInput, useFieldErrors } from "@/components/ui";
import { initialPolicyValues } from "@/lib/configuration";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import {
  formatParameterValue,
  fromInputValue,
  missingParameters,
  parameterLabel,
  parameterLabelWithUnit,
  policyDiff,
  policyInForce,
  policyUnit,
  toInputValue,
  UNIT_SUFFIX,
  validateParameter,
} from "@/lib/policies";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { groupParametersByTheme, groupPoliciesByTheme } from "@/lib/ux4b";
import "@/components/states.css";

type Policy = Schemas["AccountingPolicyView"];
type Version = Schemas["PolicyVersionView"];
type Definition = Schemas["PolicyParameterDefinitionView"];

// E-B03-15-4 / UX2-02 (E-UX2-1…4): accounting policies — each parameter by its Spanish label and unit, percentages typed and shown
// in % (the server keeps the fraction, E-UX2-1); prepare a version with every parameter (accounting_policy:prepare) and approve it
// (accounting_policy:approve, someone else, step-up) seeing "en vigor → propuesta" per parameter (E-UX2-3).

function policyTitle(policy: Policy): string {
  return `${policy.name ?? policy.description} (${policy.policyCode})`;
}

type AriaProps = { "aria-required"?: boolean; "aria-invalid"?: boolean; "aria-describedby"?: string };

/** The input of one parameter; it passes the Field's ARIA attributes (required, invalid, message) to the control. */
function ParameterInput({ definition, value, onChange, ...fieldAria }: { definition: Definition; value: string; onChange: (value: string) => void } & AriaProps) {
  const unit = policyUnit(definition);
  const aria = { "aria-label": parameterLabelWithUnit(definition), ...fieldAria };
  if (definition.allowedValues && definition.allowedValues.length > 0) {
    return (
      <select {...aria} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Elegir…</option>
        {definition.allowedValues.map((v) => (
          <option key={v}>{v}</option>
        ))}
      </select>
    );
  }
  if (definition.valueType === "BOOLEAN") {
    return (
      <select {...aria} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Elegir…</option>
        <option value="true">Sí</option>
        <option value="false">No</option>
      </select>
    );
  }
  return (
    <SuffixInput
      suffix={unit === "AMOUNT" ? "" : UNIT_SUFFIX[unit]}
      inputMode={unit === "DAYS" || unit === "HOURS" || unit === "MINUTES" ? "numeric" : "decimal"}
      placeholder={definition.example ?? undefined}
      value={value}
      onChange={onChange}
      {...fieldAria}
    />
  );
}

function PrepareVersion({ policy, onDone }: { policy: Policy; onDone: () => void }) {
  const prepare = useCommand(
    `prepare-policy:${policy.policyCode}`,
    "/api/v1/companies/{companyId}/finance/prepare-accounting-policy-version",
    `Borrador de la política ${policy.name ?? policy.policyCode} guardado; falta su aprobación.`,
  );
  const [open, setOpen] = useState(false);
  // The inputs hold what the user sees (a percentage in %); the fractions are rebuilt when sending.
  const [values, setValues] = useState(() => {
    const stored = initialPolicyValues(policy.definitions, policy.versions);
    return Object.fromEntries(policy.definitions.map((d) => [d.paramCode, toInputValue(d, stored[d.paramCode])]));
  });
  const [effectiveFrom, setEffectiveFrom] = useState("");
  const [justification, setJustification] = useState("");
  const fe = useFieldErrors();
  if (!open) {
    return (
      <button type="button" onClick={() => setOpen(true)}>
        Preparar nueva versión
      </button>
    );
  }
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const found: Record<string, string | false | null> = {
          effectiveFrom: !effectiveFrom && "Indique desde cuándo rige la versión.",
          justification: !justification.trim() && "Explique por qué cambia la política.",
        };
        for (const d of policy.definitions) {
          found[`param-${d.paramCode}`] = validateParameter(d, values[d.paramCode] ?? "");
        }
        if (!fe.check(found)) {
          return;
        }
        const parameters = Object.fromEntries(policy.definitions.map((d) => [d.paramCode, fromInputValue(d, values[d.paramCode] ?? "")]));
        if (await prepare.run({ policyCode: policy.policyCode, effectiveFrom, parameters, justification: justification.trim() })) {
          setOpen(false);
          onDone();
        }
      }}
    >
      <h3 style={{ marginTop: 0 }}>Nueva versión de {policy.name ?? policy.policyCode}</h3>
      {policy.definitions.map((d) => (
        <Field
          key={d.paramCode}
          label={parameterLabelWithUnit(d)}
          required
          error={fe.errors[`param-${d.paramCode}`]}
          hint={[d.affects, d.example ? `Ejemplo: ${d.example}.` : null].filter(Boolean).join(" ")}
        >
          <ParameterInput definition={d} value={values[d.paramCode] ?? ""} onChange={(v) => setValues({ ...values, [d.paramCode]: v })} />
        </Field>
      ))}
      <Field label="Vigente desde" required error={fe.errors.effectiveFrom}>
        <input type="date" value={effectiveFrom} onChange={(e) => setEffectiveFrom(e.target.value)} />
      </Field>
      <Field label="Justificación" required error={fe.errors.justification}>
        <input value={justification} onChange={(e) => setJustification(e.target.value)} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={prepare.busy}>
          Guardar borrador
        </button>
        <button type="button" onClick={() => setOpen(false)}>
          Cancelar
        </button>
      </div>
      <ErrorBox error={prepare.error} />
    </form>
  );
}

/** E-UX2-3: "En vigor → Propuesta" per parameter, the changed ones highlighted (text comparison only). */
function DiffTable({ policy, inForce, proposed, testId }: { policy: Policy; inForce: Version | undefined; proposed: Version; testId?: string }) {
  const rows = policyDiff(policy.definitions, inForce?.parameters, proposed.parameters);
  return (
    <div className="table-wrap">
      <table data-testid={testId}>
        <thead>
          <tr>
            <th>Parámetro</th>
            <th>En vigor</th>
            <th>Propuesta</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.paramCode} className={r.changed ? "changed" : undefined} data-changed={r.changed ? "true" : undefined}>
              <td className="wrap">
                {r.label}
                {r.changed ? (
                  <>
                    {" "}
                    <span className="badge tone-attention">Cambia</span>
                  </>
                ) : null}
              </td>
              <td className="mono">{formatParameterValue(r.definition, r.current)}</td>
              <td className="mono">
                <strong>{formatParameterValue(r.definition, r.proposed)}</strong>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ApprovalCard({ policy, version, inForce, onDone }: { policy: Policy; version: Version; inForce: Version | undefined; onDone: () => void }) {
  const { can, isMine } = useSession();
  const [confirming, setConfirming] = useState(false);
  // Four eyes: the database refuses the preparer as approver; the screen does not offer it (UX1-01b: name or e-mail, isMine).
  const preparedByMe = isMine(version.preparedBy);
  const approve = useCommand(
    `approve-policy:${version.policyVersionId}`,
    "/api/v1/companies/{companyId}/finance/approve-accounting-policy-version",
    `Versión ${version.version} de la política ${policy.name ?? policy.policyCode} aprobada y activa.`,
  );
  const changed = policyDiff(policy.definitions, inForce?.parameters, version.parameters).filter((r) => r.changed).length;
  return (
    <div className="card" data-testid={`approval:${policy.policyCode}:${version.version}`}>
      <h3 style={{ marginTop: 0 }}>
        Versión {version.version} por aprobar — rige desde {formatDate(version.effectiveFrom)}
      </h3>
      <p className="muted">
        Preparada por {version.preparedBy ?? "Despliegue"}: {version.justification}. {changed === 0 ? "No cambia ningún valor." : `Cambia ${changed} ${changed === 1 ? "valor" : "valores"}.`}
      </p>
      <DiffTable policy={policy} inForce={inForce} proposed={version} />
      {can("accounting_policy:approve") && !preparedByMe ? (
        <>
          <button type="button" className="primary" disabled={approve.busy} onClick={() => setConfirming(true)}>
            Aprobar
          </button>
          <ConfirmDialog
            open={confirming}
            title={`¿Aprobar la versión ${version.version} de ${policy.name ?? policy.policyCode}?`}
            confirmLabel="Confirmar: Aprobar"
            stepUp
            busy={approve.busy}
            onCancel={() => setConfirming(false)}
            onConfirm={async () => {
              setConfirming(false);
              if (await approve.run({ policyVersionId: version.policyVersionId })) {
                onDone();
              }
            }}
          >
            <p>
              La versión {version.version} queda activa desde {formatDate(version.effectiveFrom)} y sus valores rigen la contabilización desde esa fecha; la
              versión anterior termina. No se puede volver a borrador.
            </p>
            <DiffTable policy={policy} inForce={inForce} proposed={version} testId="approval-diff" />
          </ConfirmDialog>
        </>
      ) : preparedByMe ? (
        <p className="muted">La aprueba otra persona con el permiso de aprobar políticas.</p>
      ) : null}
      <ErrorBox error={approve.error} />
    </div>
  );
}

function PolicySection({ policy, onDone }: { policy: Policy; onDone: () => void }) {
  const { can } = useSession();
  const today = todayInDominicanRepublic();
  const inForce = policyInForce(policy.versions, today);
  const missing = missingParameters(policy.definitions, inForce);
  const drafts = policy.versions.filter((v) => v.status === "DRAFT");
  const definitionOf = (code: string) => policy.definitions.find((d) => d.paramCode === code);
  return (
    <section data-testid={`policy:${policy.policyCode}`}>
      <h2>{policyTitle(policy)}</h2>
      <p className="muted">
        Prepara: {policy.preparerRoles?.length ? policy.preparerRoles.join(", ") : "—"} · Aprueba: {policy.approverRoles?.length ? policy.approverRoles.join(", ") : "—"}
      </p>
      {!inForce ? (
        <div className="alert-block" role="status">
          <strong>Sin versión en vigor</strong>
          Lo que dependa de esta política no se puede contabilizar hasta que se apruebe una versión que rija hoy.
        </div>
      ) : null}
      {missing.map((d) => (
        <div key={d.paramCode} className="alert-block" role="status">
          <strong>Falta el parámetro {parameterLabel(d)}: prepare una versión nueva</strong>
          La versión en vigor es anterior a este parámetro; lo que lo necesite no se contabiliza hasta aprobar una versión que lo incluya.
        </div>
      ))}
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Parámetro</th>
              <th>En vigor{inForce ? ` (v${inForce.version})` : ""}</th>
              <th>Qué afecta</th>
            </tr>
          </thead>
          {/* UX4-03 (G-22): the parameters by theme (a fiscal alert under "Fiscal"); a policy without themes keeps one body. */}
          {groupParametersByTheme(policy.definitions).map((group) => (
            <tbody key={group.theme ?? "all"} data-testid={group.theme ? `parameter-group:${group.theme}` : undefined}>
              {group.theme ? (
                <tr className="group-row">
                  <th colSpan={3} scope="colgroup">
                    {group.theme}
                  </th>
                </tr>
              ) : null}
              {group.definitions.map((d) => (
                <tr key={d.paramCode}>
                  <td className="wrap">{parameterLabelWithUnit(d)}</td>
                  <td className="mono">{formatParameterValue(d, inForce?.parameters[d.paramCode])}</td>
                  <td className="wrap muted">{d.affects ?? d.description}</td>
                </tr>
              ))}
            </tbody>
          ))}
        </table>
      </div>
      {drafts.map((v) => (
        <ApprovalCard key={`${v.policyVersionId}:${v.status}`} policy={policy} version={v} inForce={inForce} onDone={onDone} />
      ))}
      {can("accounting_policy:prepare") ? <PrepareVersion key={policy.versions.length} policy={policy} onDone={onDone} /> : null}
      {policy.versions.length === 0 ? (
        <p className="muted">Sin versiones.</p>
      ) : (
        <details>
          <summary>Historial de versiones ({policy.versions.length})</summary>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th className="num">Versión</th>
                  <th>Estado</th>
                  <th>Vigencia</th>
                  <th>Valores</th>
                  <th>Justificación</th>
                  <th>Preparada por</th>
                  <th>Aprobada por</th>
                </tr>
              </thead>
              <tbody>
                {policy.versions.map((v) => (
                  <tr key={`${v.policyVersionId}:${v.status}`}>
                    <td className="num">{v.version}</td>
                    <td>
                      <StatusBadge status={v.status} />
                    </td>
                    <td>
                      {formatDate(v.effectiveFrom)}
                      {v.effectiveTo ? ` – ${formatDate(v.effectiveTo)}` : ""}
                    </td>
                    <td className="wrap">
                      {Object.entries(v.parameters).map(([code, value]) => {
                        const d = definitionOf(code);
                        return (
                          <div key={code}>
                            {d ? parameterLabel(d) : code}: <strong>{formatParameterValue(d, value)}</strong>
                          </div>
                        );
                      })}
                    </td>
                    <td className="wrap">{v.justification}</td>
                    <td className="wrap">{v.preparedBy ?? "Despliegue"}</td>
                    <td className="wrap">{v.approvedBy ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </details>
      )}
    </section>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("configuration:read");
  const { data, error, reload } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/finance/accounting-policies", { path: { companyId } }) : null, [companyId]);

  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <h1>Políticas contables</h1>
      <p className="muted">Los porcentajes se escriben y se muestran en % (5 = 5 %); los montos en RD$.</p>
      {groupPoliciesByTheme(data.items).map((group) => (
        <div key={group.theme} className="policy-theme" data-testid={`policy-theme:${group.theme}`}>
          <h2 className="theme-title">{group.theme}</h2>
          {group.policies.map((policy) => (
            <PolicySection key={policy.policyCode} policy={policy} onDone={reload} />
          ))}
        </div>
      ))}
    </>
  );
}
