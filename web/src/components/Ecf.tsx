"use client";

import { useState } from "react";
import { ErrorBox, Field, useFieldErrors } from "./ui";
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
      <td className="mono wrap">{value ?? "—"}</td>
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
  /** null on an e-CF of the final consumer that carries no receiver, or a passport (E-CF1-6). */
  receiverRnc: string | null;
  receiverPassport: string | null;
  netTotal: string;
  taxTotal: string;
  total: string;
}

/**
 * The e-CF as the portal issued it. The totals are typed from the portal (not copied from the document), so the server's check
 * compares both; the XML is identified by its SHA-256 computed here, not uploaded. UX1-01b (E-UX1-01-6): every field is
 * mandatory and a wrong one says so under it.
 */
export function RecordEcfForm({
  prefix,
  busy,
  error,
  onSubmit,
  consumer = false,
}: {
  /** CF1-05 (E-CF1-6): the e-CF of a final consumer — the receiver is optional: nobody, a cédula or RNC, or a passport. */
  consumer?: boolean;
  prefix: "E31" | "E32" | "E34" | "E44";
  busy: boolean;
  error: unknown;
  onSubmit: (values: EcfValues) => Promise<unknown>;
}) {
  type Typed = { [K in keyof EcfValues]: string };
  const [form, setForm] = useState<Typed>({ encf: prefix, issuedAt: "", securityCode: "", evidenceRef: "", evidenceSha256: "", receiverRnc: "", receiverPassport: "", netTotal: "", taxTotal: "", total: "" });
  const fe = useFieldErrors<keyof EcfValues>();
  const set = (key: keyof EcfValues) => (e: { target: { value: string } }) => setForm({ ...form, [key]: e.target.value });
  const amount = "Monto del portal, hasta 2 decimales.";
  return (
    <form
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const totals = { netTotal: normalizeInput(form.netTotal), taxTotal: normalizeInput(form.taxTotal), total: normalizeInput(form.total) };
        const valid = fe.check({
          encf: !new RegExp(`^${prefix}[0-9]{10}$`).test(form.encf.trim().toUpperCase()) && `El e-NCF es ${prefix} seguido de 10 dígitos.`,
          issuedAt: !form.issuedAt && "Indique la fecha y hora de emisión.",
          securityCode: form.securityCode.trim() === "" && "Indique el código de seguridad.",
          evidenceRef: form.evidenceRef.trim() === "" && "Adjunte el archivo o escriba su referencia.",
          evidenceSha256: !/^[0-9a-fA-F]{64}$/.test(form.evidenceSha256.trim()) && "El SHA-256 tiene 64 caracteres hexadecimales (se calcula al adjuntar el archivo).",
          receiverRnc: consumer
            ? form.receiverRnc.trim() !== "" && form.receiverPassport.trim() !== "" && "El e-CF lleva una sola identificación: cédula o RNC, o pasaporte."
            : form.receiverRnc.trim() === "" && "Indique el RNC del receptor según el portal.",
          netTotal: !isDecimal(totals.netTotal, 2) && amount,
          taxTotal: !isDecimal(totals.taxTotal, 2) && amount,
          total: !isDecimal(totals.total, 2) && amount,
        });
        if (!valid) {
          return;
        }
        const optional = (v: string) => (v.trim() === "" ? null : v.trim());
        await onSubmit({
          ...form,
          ...totals,
          encf: form.encf.trim().toUpperCase(),
          issuedAt: new Date(form.issuedAt).toISOString(),
          receiverRnc: consumer ? optional(form.receiverRnc) : form.receiverRnc,
          receiverPassport: consumer ? optional(form.receiverPassport.toUpperCase()) : null,
        });
      }}
    >
      <Field label="e-NCF" required error={fe.errors.encf}>
        <input value={form.encf} onChange={set("encf")} />
      </Field>
      <Field label="Emitido el" required error={fe.errors.issuedAt}>
        <input type="datetime-local" value={form.issuedAt} onChange={set("issuedAt")} />
      </Field>
      <Field label="Código de seguridad" required error={fe.errors.securityCode}>
        <input value={form.securityCode} onChange={set("securityCode")} />
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
      <Field label="Referencia del archivo" required error={fe.errors.evidenceRef}>
        <input value={form.evidenceRef} onChange={set("evidenceRef")} />
      </Field>
      <Field label="SHA-256" required error={fe.errors.evidenceSha256} wide>
        <input className="mono" value={form.evidenceSha256} onChange={set("evidenceSha256")} />
      </Field>
      {consumer ? (
        <>
          <Field
            label="Cédula o RNC del receptor (si el e-CF lo lleva)"
            error={fe.errors.receiverRnc}
            hint="Déjelo vacío si el e-CF de consumo salió sin receptor: debe coincidir con la identificación del comprador en la venta."
          >
            <input value={form.receiverRnc} onChange={set("receiverRnc")} />
          </Field>
          <Field label="Pasaporte del receptor (si el e-CF lo lleva)">
            <input value={form.receiverPassport} onChange={set("receiverPassport")} />
          </Field>
        </>
      ) : (
        <Field label="RNC del receptor (según el portal)" required error={fe.errors.receiverRnc}>
          <input value={form.receiverRnc} onChange={set("receiverRnc")} />
        </Field>
      )}
      <Field label="Neto (según el portal)" required error={fe.errors.netTotal}>
        <input inputMode="decimal" value={form.netTotal} onChange={set("netTotal")} />
      </Field>
      <Field label="ITBIS (según el portal)" required error={fe.errors.taxTotal}>
        <input inputMode="decimal" value={form.taxTotal} onChange={set("taxTotal")} />
      </Field>
      <Field label="Total (según el portal)" required error={fe.errors.total}>
        <input inputMode="decimal" value={form.total} onChange={set("total")} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={busy}>
          Registrar e-CF
        </button>
      </div>
      <ErrorBox error={error} />
    </form>
  );
}
