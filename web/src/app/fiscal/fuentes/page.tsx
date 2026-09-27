"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
import { sha256Hex } from "@/lib/configuration";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// E-B03-15-3: an official source is registered with the SHA-256 of the document consulted, computed here from the chosen file;
// the file itself is not uploaded (document storage comes with WORM for documents). TEST sources never activate a rule in a
// PRODUCTION database (P-7).

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
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [key]: e.target.value });

  return (
    <form
      onSubmit={async (e) => {
        e.preventDefault();
        const response = await register.run({
          ...form,
          consultedAt: new Date(form.consultedAt).toISOString(),
          effectiveTo: form.effectiveTo === "" ? null : form.effectiveTo,
        });
        if (response) {
          onDone();
        }
      }}
    >
      <Field label="Fuente oficial (p. ej. DGII)">
        <input value={form.officialSource} onChange={set("officialSource")} required />
      </Field>
      <Field label="Título del documento">
        <input value={form.documentTitle} onChange={set("documentTitle")} required />
      </Field>
      <Field label="Versión del documento">
        <input value={form.documentVersion} onChange={set("documentVersion")} required />
      </Field>
      <Field label="Fecha de publicación">
        <input type="date" value={form.publicationDate} onChange={set("publicationDate")} required />
      </Field>
      <Field label="Consultado el">
        <input type="datetime-local" value={form.consultedAt} onChange={set("consultedAt")} required />
      </Field>
      <Field label="Vigente desde">
        <input type="date" value={form.effectiveFrom} onChange={set("effectiveFrom")} required />
      </Field>
      <Field label="Vigente hasta (opcional)">
        <input type="date" value={form.effectiveTo} onChange={set("effectiveTo")} />
      </Field>
      <Field label="URL o referencia">
        <input value={form.urlOrReference} onChange={set("urlOrReference")} required />
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
      <Field label="Referencia del archivo">
        <input value={form.fileReference} onChange={set("fileReference")} required />
      </Field>
      <Field label="SHA-256">
        <input value={form.fileSha256} onChange={set("fileSha256")} required pattern="[0-9a-fA-F]{64}" size={66} />
      </Field>
      <Field label="Tipo de fuente">
        <select value={form.environment} onChange={set("environment")}>
          <option value="TEST">Prueba (TEST)</option>
          <option value="PRODUCTION">Oficial (PRODUCTION)</option>
        </select>
      </Field>
      <button type="submit" disabled={register.busy}>
        Registrar fuente
      </button>
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
        <table>
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
                <td className="muted">{s.fileSha256.slice(0, 12)}…</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
