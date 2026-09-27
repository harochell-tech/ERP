"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
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
  const prepare = useCommand(`prepare-policy:${policy.policyCode}`, "/api/v1/companies/{companyId}/finance/prepare-accounting-policy-version");
  const [open, setOpen] = useState(false);
  const [values, setValues] = useState(() => initialPolicyValues(policy.definitions, policy.versions));
  const [effectiveFrom, setEffectiveFrom] = useState("");
  const [justification, setJustification] = useState("");
  if (!open) {
    return (
      <button type="button" onClick={() => setOpen(true)}>
        Preparar nueva versión
      </button>
    );
  }
  return (
    <form
      onSubmit={async (e) => {
        e.preventDefault();
        if (await prepare.run({ policyCode: policy.policyCode, effectiveFrom, parameters: values, justification: justification.trim() })) {
          setOpen(false);
          onDone();
        }
      }}
    >
      {policy.definitions.map((d) => (
        <Field key={d.paramCode} label={`${d.description} (${d.paramCode})`}>
          {d.allowedValues && d.allowedValues.length > 0 ? (
            <select value={values[d.paramCode] ?? ""} onChange={(e) => setValues({ ...values, [d.paramCode]: e.target.value })}>
              <option value="">—</option>
              {d.allowedValues.map((v) => (
                <option key={v}>{v}</option>
              ))}
            </select>
          ) : (
            <input value={values[d.paramCode] ?? ""} onChange={(e) => setValues({ ...values, [d.paramCode]: e.target.value })} required />
          )}
        </Field>
      ))}
      <Field label="Vigente desde">
        <input type="date" value={effectiveFrom} onChange={(e) => setEffectiveFrom(e.target.value)} required />
      </Field>
      <Field label="Justificación">
        <input value={justification} onChange={(e) => setJustification(e.target.value)} required />
      </Field>
      <div className="actions">
        <button type="submit" disabled={prepare.busy}>
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
  const { can, state } = useSession();
  // Four eyes: the database refuses the preparer as approver; the screen does not offer it.
  const preparedByMe = state.status === "ready" && version.preparedBy === state.session.email;
  const approve = useCommand(`approve-policy:${version.policyVersionId}`, "/api/v1/companies/{companyId}/finance/approve-accounting-policy-version");
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
      <td>{version.justification}</td>
      <td>{version.preparedBy ?? "Despliegue"}</td>
      <td>{version.approvedBy ?? "—"}</td>
      <td>
        {version.status === "DRAFT" && can("accounting_policy:approve") && !preparedByMe ? (
          <button type="button" disabled={approve.busy} onClick={async () => (await approve.run({ policyVersionId: version.policyVersionId })) && onDone()}>
            Aprobar
          </button>
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
            <table>
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
            </table>
          )}
        </section>
      ))}
    </>
  );
}
