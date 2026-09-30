"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission, useFieldErrors } from "@/components/ui";
import { sha256Hex } from "@/lib/configuration";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// E-B03-15-3: an official source is registered with the SHA-256 of the document consulted, computed here from the chosen file;
// the file itself is not uploaded (document storage comes with WORM for documents). TEST sources never activate a rule in a
// PRODUCTION database (P-7).

type SourceField =
  | "officialSource"
  | "documentTitle"
  | "documentVersion"
  | "publicationDate"
  | "consultedAt"
  | "effectiveFrom"
  | "effectiveTo"
  | "urlOrReference"
  | "fileReference"
  | "fileSha256";

function RegisterSource({ onDone }: { onDone: () => void }) {
  const register = useCommand("register-fiscal-source", "/api/v1/companies/{companyId}/tax/register-fiscal-source");
  const [form, setForm] = useState({
    officialSource: "",
    documentTitle: "",
    documentVersion: "",
    publicationDate: "",
    consultedAt: "",
    effectiveFrom: "",
    effectiveTo: "",
    urlOrReference: "",
    fileReference: "",
    fileSha256: "",
    environment: "TEST",
  });
  const fe = useFieldErrors<SourceField>();
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [key]: e.target.value });
  const blank = (value: string) => value.trim() === "";

  return (
    <form
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const valid = fe.check({
          officialSource: blank(form.officialSource) && "Indique la fuente oficial.",
          documentTitle: blank(form.documentTitle) && "Indique el título del documento.",
          documentVersion: blank(form.documentVersion) && "Indique la versión del documento.",
          publicationDate: !form.publicationDate && "Indique la fecha de publicación.",
          consultedAt: !form.consultedAt && "Indique cuándo consultó el documento.",
          effectiveFrom: !form.effectiveFrom && "Indique desde cuándo rige.",
          effectiveTo: form.effectiveTo !== "" && form.effectiveFrom !== "" && form.effectiveTo < form.effectiveFrom && "El fin de la vigencia es posterior a su inicio.",
          urlOrReference: blank(form.urlOrReference) && "Indique la URL o la referencia.",
          fileReference: blank(form.fileReference) && "Adjunte el documento o escriba su referencia.",
          fileSha256: !/^[0-9a-fA-F]{64}$/.test(form.fileSha256.trim()) && "El SHA-256 tiene 64 caracteres hexadecimales (se calcula al adjuntar el documento).",
        });
        if (!valid) {
          return;
        }
        const response = await register.run(
          {
            ...form,
            consultedAt: new Date(form.consultedAt).toISOString(),
            effectiveTo: form.effectiveTo === "" ? null : form.effectiveTo,
          },
          undefined,
          `Fuente fiscal registrada: ${form.documentTitle.trim()} (${form.documentVersion.trim()}).`,
        );
        if (response) {
          onDone();
        }
      }}
    >
      <Field label="Fuente oficial (p. ej. DGII)" required error={fe.errors.officialSource}>
        <input value={form.officialSource} onChange={set("officialSource")} />
      </Field>
      <Field label="Título del documento" required error={fe.errors.documentTitle}>
        <input value={form.documentTitle} onChange={set("documentTitle")} />
      </Field>
      <Field label="Versión del documento" required error={fe.errors.documentVersion}>
        <input value={form.documentVersion} onChange={set("documentVersion")} />
      </Field>
      <Field label="Fecha de publicación" required error={fe.errors.publicationDate}>
        <input type="date" value={form.publicationDate} onChange={set("publicationDate")} />
      </Field>
      <Field label="Consultado el" required error={fe.errors.consultedAt}>
        <input type="datetime-local" value={form.consultedAt} onChange={set("consultedAt")} />
      </Field>
      <Field label="Vigente desde" required error={fe.errors.effectiveFrom}>
        <input type="date" value={form.effectiveFrom} onChange={set("effectiveFrom")} />
      </Field>
      <Field label="Vigente hasta (opcional)" error={fe.errors.effectiveTo}>
        <input type="date" value={form.effectiveTo} onChange={set("effectiveTo")} />
      </Field>
      <Field label="URL o referencia" required error={fe.errors.urlOrReference}>
        <input value={form.urlOrReference} onChange={set("urlOrReference")} />
      </Field>
      <Field label="Documento consultado">
        <input
          type="file"
          aria-label="Documento consultado"
          onChange={async (e) => {
            const file = e.target.files?.[0];
            if (file) {
              setForm({ ...form, fileReference: file.name, fileSha256: await sha256Hex(await file.arrayBuffer()) });
            }
          }}
        />
      </Field>
      <Field label="Referencia del archivo" required error={fe.errors.fileReference}>
        <input value={form.fileReference} onChange={set("fileReference")} />
      </Field>
      <Field label="SHA-256" required error={fe.errors.fileSha256} wide>
        <input className="mono" value={form.fileSha256} onChange={set("fileSha256")} />
      </Field>
      <Field label="Tipo de fuente" required>
        <select value={form.environment} onChange={set("environment")}>
          <option value="TEST">Prueba (TEST)</option>
          <option value="PRODUCTION">Oficial (PRODUCTION)</option>
        </select>
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={register.busy}>
          Registrar fuente
        </button>
      </div>
      <ErrorBox error={register.error} />
    </form>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("configuration:read");
  const { data, error, reload } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/tax/fiscal-sources", { path: { companyId } }) : null, [companyId]);

  if (!allowed) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Fuentes fiscales</h1>
      {can("fiscal_rule_source:register") ? (
        <details>
          <summary>Registrar una fuente</summary>
          <RegisterSource onDone={reload} />
        </details>
      ) : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay fuentes registradas.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Documento</th>
              <th>Fuente</th>
              <th>Publicación</th>
              <th>Vigencia</th>
              <th>Tipo</th>
              <th>SHA-256</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((s) => (
              <tr key={s.sourceId}>
                <td>
                  {s.documentTitle} ({s.documentVersion})
                </td>
                <td>
                  {s.officialSource} — {s.urlOrReference}
                </td>
                <td>{formatDate(s.publicationDate)}</td>
                <td>
                  {formatDate(s.effectiveFrom)}
                  {s.effectiveTo ? ` – ${formatDate(s.effectiveTo)}` : ""}
                </td>
                <td>{s.environment === "PRODUCTION" ? "Oficial" : "Prueba"}</td>
                <td className="muted mono" title={s.fileSha256}>
                  {s.fileSha256.slice(0, 12)}…
                </td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
