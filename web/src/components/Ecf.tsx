"use client";

import { useState } from "react";
import { ErrorBox, Field } from "./ui";
import { isDecimal, normalizeInput } from "@/lib/decimal";
import { sha256Hex } from "@/lib/ledger";

// VS3-10b (E-VS3-10-6): pieces shared by the invoice and the credit note — the fiscal package the user copies into the provider's
// portal, and the form that records the e-CF the portal issued (checked against the document by the server).

/** A value with a button that copies it (for the provider's portal). */
export function CopyField({ label, value }: { label: string; value: string | null | undefined }) {
  const [copied, setCopied] = useState(false);
  return (
    <tr>
      <th>{label}</th>
      <td className="mono">{value ?? "—"}</td>
      <td>
        {value ? (
          <button
            type="button"
            onClick={async () => {
              try {
                await navigator.clipboard.writeText(value);
                setCopied(true);
              } catch {
                setCopied(false);
              }
            }}
          >
            {copied ? "Copiado" : "Copiar"}
          </button>
        ) : null}
      </td>
    </tr>
  );
}

export interface EcfValues {
  encf: string;
  issuedAt: string;
  securityCode: string;
  evidenceRef: string;
  evidenceSha256: string;
  receiverRnc: string;
  netTotal: string;
  taxTotal: string;
  total: string;
}

/**
 * The e-CF as the portal issued it. The totals are typed from the portal (not copied from the document), so the server's check
 * compares both; the XML is identified by its SHA-256 computed here, not uploaded.
 */
export function RecordEcfForm({
  prefix,
  busy,
  error,
  onSubmit,
}: {
  prefix: "E31" | "E32" | "E34";
  busy: boolean;
  error: unknown;
  onSubmit: (values: EcfValues) => Promise<unknown>;
}) {
  const [form, setForm] = useState<EcfValues>({ encf: prefix, issuedAt: "", securityCode: "", evidenceRef: "", evidenceSha256: "", receiverRnc: "", netTotal: "", taxTotal: "", total: "" });
  const [invalid, setInvalid] = useState<string | null>(null);
  const set = (key: keyof EcfValues) => (e: { target: { value: string } }) => setForm({ ...form, [key]: e.target.value });
  return (
    <form
      onSubmit={async (e) => {
        e.preventDefault();
        const totals = { netTotal: normalizeInput(form.netTotal), taxTotal: normalizeInput(form.taxTotal), total: normalizeInput(form.total) };
        if (!new RegExp(`^${prefix}[0-9]{10}$`).test(form.encf.trim().toUpperCase()) || !form.issuedAt || Object.values(totals).some((t) => !isDecimal(t, 2))) {
          setInvalid(`El e-NCF es ${prefix} seguido de 10 dígitos; indique la fecha de emisión y los totales del portal (hasta 2 decimales).`);
          return;
        }
        setInvalid(null);
        await onSubmit({ ...form, ...totals, encf: form.encf.trim().toUpperCase(), issuedAt: new Date(form.issuedAt).toISOString() });
      }}
    >
      <Field label="e-NCF">
        <input value={form.encf} onChange={set("encf")} required />
      </Field>
      <Field label="Emitido el">
        <input type="datetime-local" value={form.issuedAt} onChange={set("issuedAt")} required />
      </Field>
      <Field label="Código de seguridad">
        <input value={form.securityCode} onChange={set("securityCode")} required />
      </Field>
      <Field label="XML o PDF del e-CF">
        <input
          type="file"
          aria-label="XML o PDF del e-CF"
          onChange={async (e) => {
            const file = e.target.files?.[0];
            if (file) {
              setForm({ ...form, evidenceRef: file.name, evidenceSha256: await sha256Hex(file) });
            }
          }}
        />
      </Field>
      <Field label="Referencia del archivo">
        <input value={form.evidenceRef} onChange={set("evidenceRef")} required />
      </Field>
      <Field label="SHA-256">
        <input value={form.evidenceSha256} onChange={set("evidenceSha256")} required pattern="[0-9a-fA-F]{64}" size={66} />
      </Field>
      <Field label="RNC del receptor (según el portal)">
        <input value={form.receiverRnc} onChange={set("receiverRnc")} required />
      </Field>
      <Field label="Neto (según el portal)">
        <input inputMode="decimal" value={form.netTotal} onChange={set("netTotal")} required />
      </Field>
      <Field label="ITBIS (según el portal)">
        <input inputMode="decimal" value={form.taxTotal} onChange={set("taxTotal")} required />
      </Field>
      <Field label="Total (según el portal)">
        <input inputMode="decimal" value={form.total} onChange={set("total")} required />
      </Field>
      <button type="submit" className="primary" disabled={busy}>
        Registrar e-CF
      </button>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={error} />
    </form>
  );
}
