"use client";

import Link from "next/link";
import { useState, type ReactNode } from "react";
import { describeError } from "@/lib/errors";
import { statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";

export function ErrorBox({ error }: { error: unknown }) {
  if (error === null || error === undefined) {
    return null;
  }
  const described = describeError(error);
  return (
    <div role="alert" className="error">
      <strong>{described.message}</strong>
      {described.detail ? <div>{described.detail}</div> : null}
      {described.correlationId ? <div className="muted">Referencia: {described.correlationId}</div> : null}
    </div>
  );
}

export function Loading({ error, children }: { error?: unknown; children?: ReactNode }) {
  if (error) {
    return <ErrorBox error={error} />;
  }
  return <p className="muted">{children ?? "Cargando…"}</p>;
}

/**
 * E-11 / E-PR18b-5: accounting status as the API reports it, never changed from the UI. The link to the journals (Explain)
 * appears only for users with audit:read.
 */
export function AccountingStatus({ status, eventId }: { status: string; eventId?: string | null }) {
  const { can } = useSession();
  return (
    <span data-testid="accounting-status">
      {statusLabel(status)}
      {eventId && can("audit:read") ? (
        <>
          {" "}
          (<Link href={`/auditoria/asientos/?evento=${eventId}`}>ver asientos</Link>)
        </>
      ) : null}
    </span>
  );
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="field">
      <span>{label}</span>
      {children}
    </label>
  );
}

/** A button that asks for a mandatory reason before running (reject, cancel, reverse…). */
export function ReasonAction({ label, busy, onConfirm }: { label: string; busy: boolean; onConfirm: (reason: string) => void }) {
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState("");
  if (!open) {
    return (
      <button type="button" onClick={() => setOpen(true)}>
        {label}
      </button>
    );
  }
  return (
    <span className="inline-form">
      <input aria-label={`Motivo: ${label}`} placeholder="Motivo" value={reason} onChange={(e) => setReason(e.target.value)} />
      <button type="button" disabled={busy || reason.trim().length === 0} onClick={() => onConfirm(reason.trim())}>
        Confirmar: {label}
      </button>
      <button type="button" onClick={() => setOpen(false)}>
        Cancelar
      </button>
    </span>
  );
}

export function NoPermission() {
  return <p className="muted">No tiene permiso para ver esta página.</p>;
}
