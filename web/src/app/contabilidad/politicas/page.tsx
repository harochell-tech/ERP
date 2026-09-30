"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmAction, ErrorBox, Field, Loading, NoPermission, useFieldErrors } from "@/components/ui";
import { initialPolicyValues } from "@/lib/configuration";
import { formatDate, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Policy = Schemas["AccountingPolicyView"];
type Version = Schemas["PolicyVersionView"];

// E-B03-15-4: accounting policies — prepare a version with every parameter (accounting_policy:prepare) and approve it
// (accounting_policy:approve, someone else, step-up). Values stay as typed strings; the server checks types and bounds.

function PrepareVersion({ policy, onDone }: { policy: Policy; onDone: () => void }) {
  const prepare = useCommand(
    `prepare-policy:${policy.policyCode}`,
    "/api/v1/companies/{companyId}/finance/prepare-accounting-policy-version",
    `Borrador de la política ${policy.policyCode} guardado; falta su aprobación.`,
  );
  const [open, setOpen] = useState(false);
  const [values, setValues] = useState(() => initialPolicyValues(policy.definitions, policy.versions));
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
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const found: Record<string, string | false> = {
          effectiveFrom: !effectiveFrom && "Indique desde cuándo rige la versión.",
          justification: !justification.trim() && "Explique por qué cambia la política.",
        };
        for (const d of policy.definitions) {
          const optional = (d.allowedValues?.length ?? 0) > 0;
          found[`param-${d.paramCode}`] = !optional && !(values[d.paramCode] ?? "").trim() && `Indique el valor de ${d.paramCode}.`;
        }
        if (!fe.check(found)) {
          return;
        }
        if (await prepare.run({ policyCode: policy.policyCode, effectiveFrom, parameters: values, justification: justification.trim() })) {
          setOpen(false);
          onDone();
        }
      }}
    >
      {policy.definitions.map((d) => (
        <Field key={d.paramCode} label={`${d.description} (${d.paramCode})`} required={!d.allowedValues?.length} error={fe.errors[`param-${d.paramCode}`]}>
          {d.allowedValues && d.allowedValues.length > 0 ? (
            <select value={values[d.paramCode] ?? ""} onChange={(e) => setValues({ ...values, [d.paramCode]: e.target.value })}>
              <option value="">—</option>
              {d.allowedValues.map((v) => (
                <option key={v}>{v}</option>
              ))}
            </select>
          ) : (
            <input value={values[d.paramCode] ?? ""} onChange={(e) => setValues({ ...values, [d.paramCode]: e.target.value })} />
          )}
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

function VersionRow({ version, onDone }: { version: Version; onDone: () => void }) {
  const { can, isMine } = useSession();
  // Four eyes: the database refuses the preparer as approver; the screen does not offer it.
  // UX1-01b: the API returns the preparer's display name (its e-mail until the first sign-in brings a name, E-UX1-01-3).
  const preparedByMe = isMine(version.preparedBy);
  const approve = useCommand(`approve-policy:${version.policyVersionId}`, "/api/v1/companies/{companyId}/finance/approve-accounting-policy-version", `Versión ${version.version} de la política aprobada y activa.`);
  return (
    <tr>
      <td className="num">{version.version}</td>
      <td>{statusLabel(version.status)}</td>
      <td>
        {formatDate(version.effectiveFrom)}
        {version.effectiveTo ? ` – ${formatDate(version.effectiveTo)}` : ""}
      </td>
      <td>
        {Object.entries(version.parameters).map(([code, value]) => (
          <div key={code}>
            {code}: <strong>{value}</strong>
          </div>
        ))}
      </td>
      <td className="wrap">{version.justification}</td>
      <td className="wrap">{version.preparedBy ?? "Despliegue"}</td>
      <td className="wrap">{version.approvedBy ?? "—"}</td>
      <td>
        {version.status === "DRAFT" && can("accounting_policy:approve") && !preparedByMe ? (
          <ConfirmAction
            label="Aprobar"
            title={`¿Aprobar la versión ${version.version} de la política?`}
            stepUp
            busy={approve.busy}
            consequence={`La versión ${version.version} queda activa desde ${formatDate(version.effectiveFrom)} y sus valores rigen la contabilización desde esa fecha; la versión anterior termina. No se puede volver a borrador.`}
            onConfirm={async () => (await approve.run({ policyVersionId: version.policyVersionId })) && onDone()}
          />
        ) : null}
        <ErrorBox error={approve.error} />
      </td>
    </tr>
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
      {data.items.map((policy) => (
        <section key={policy.policyCode}>
          <h2>
            {policy.policyCode} — {policy.description}
          </h2>
          {can("accounting_policy:prepare") ? <PrepareVersion key={policy.versions.length} policy={policy} onDone={reload} /> : null}
          {policy.versions.length === 0 ? (
            <p className="muted">Sin versiones.</p>
          ) : (
            <div className="table-wrap"><table>
              <thead>
                <tr>
                  <th className="num">Versión</th>
                  <th>Estado</th>
                  <th>Vigencia</th>
                  <th>Parámetros</th>
                  <th>Justificación</th>
                  <th>Preparada por</th>
                  <th>Aprobada por</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {policy.versions.map((v) => (
                  <VersionRow key={`${v.policyVersionId}:${v.status}`} version={v} onDone={reload} />
                ))}
              </tbody>
            </table></div>
          )}
        </section>
      ))}
    </>
  );
}
