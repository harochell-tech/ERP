"use client";

import { useEffect, useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, StatusBadge } from "@/components/ui";
import { formatDateTime } from "@/lib/labels";
import { deliveryText, mailModeNotice, mailStatusLabel, mailStatusTone, recipientsError, recipientsOf } from "@/lib/mail";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// MAIL-03 (E-MAIL-5, 6, 7, E-MAIL-01-4, 6, 9, 10): «Enviar por correo» on a document and the history of its sendings. The saved
// e-mails of the customer come ticked, others are typed, and an optional message goes in the body; the server builds the PDF and
// the fixed text. Each sending shows its state, the exact PDF sent and, when it failed, «Reintentar».

type Send = (recipients: string[], message: string | null) => Promise<boolean>;

function useMailMode(): string | null {
  const [mode, setMode] = useState<string | null>(null);
  useEffect(() => {
    let live = true;
    fetch("/api/v1/environment", { credentials: "same-origin" })
      .then((r) => (r.ok ? r.json() : null))
      .then((body: { mailMode?: string } | null) => {
        if (live && body?.mailMode) {
          setMode(body.mailMode);
        }
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, []);
  return mode;
}

function Retry({ mailId, onDone }: { mailId: string; onDone: () => void }) {
  const retry = useCommand(`retry-mail:${mailId}`, "/api/v1/companies/{companyId}/sales/retry-document-email", "Correo puesto en cola de nuevo.");
  return (
    <>
      <button
        type="button"
        disabled={retry.busy}
        onClick={async () => {
          if (await retry.run({ mailId })) {
            onDone();
          }
        }}
      >
        Reintentar
      </button>
      <ErrorBox error={retry.error} />
    </>
  );
}

function DownloadPdf({ mailId }: { mailId: string }) {
  const { companyId } = useSession();
  const [error, setError] = useState<unknown>(null);
  return (
    <>
      <button
        type="button"
        onClick={async () => {
          try {
            setError(null);
            const pdf = await query("/api/v1/companies/{companyId}/sales/mail/{mailId}/pdf", { path: { companyId, mailId } });
            const bytes = Uint8Array.from(atob(pdf.contentBase64), (c) => c.charCodeAt(0));
            const url = URL.createObjectURL(new Blob([bytes], { type: "application/pdf" }));
            const link = document.createElement("a");
            link.href = url;
            link.download = pdf.fileName;
            link.click();
            URL.revokeObjectURL(url);
          } catch (caught) {
            setError(caught);
          }
        }}
      >
        Descargar PDF
      </button>
      <ErrorBox error={error} />
    </>
  );
}

function MailPanel({
  documentType,
  documentId,
  what,
  title = "Correo",
  button = "Enviar por correo",
  canSend,
  blocked,
  send,
  busy,
  error,
}: {
  documentType: string;
  documentId: string;
  /** "la cotización COT-000001" */
  what: string;
  title?: string;
  button?: string;
  canSend: boolean;
  /** Why the document cannot be sent yet (E-MAIL-01-7), or null. */
  blocked: string | null;
  send: Send;
  busy: boolean;
  error: unknown;
}) {
  const { companyId, can } = useSession();
  const mode = useMailMode();
  const [open, setOpen] = useState(false);
  const [unticked, setUnticked] = useState<ReadonlySet<string>>(new Set());
  const [typed, setTyped] = useState("");
  const [message, setMessage] = useState("");
  const [invalid, setInvalid] = useState<string | null>(null);
  const history = useLoad(
    can("sales:read") && documentId ? () => query("/api/v1/companies/{companyId}/sales/mail", { path: { companyId }, query: { documentType, documentId } }) : null,
    [companyId, documentType, documentId],
  );
  const items = history.data?.items ?? [];
  const saved = history.data?.savedEmails ?? [];
  const waiting = items.some((m) => m.status === "QUEUED");
  const reload = history.reload;

  // A queued message leaves within seconds: look again until none waits.
  useEffect(() => {
    if (!waiting) {
      return undefined;
    }
    const timer = window.setTimeout(reload, 3000);
    return () => window.clearTimeout(timer);
  }, [waiting, reload, history.data]);

  if (!can("sales:read") || (!canSend && items.length === 0)) {
    return null;
  }
  const notice = mailModeNotice(mode);
  return (
    <section className="no-print" data-testid="document-mail">
      <h2>{title}</h2>
      {canSend && !open ? (
        <div className="actions">
          <button type="button" disabled={blocked !== null || mode === "OFF"} onClick={() => setOpen(true)}>
            {button}
          </button>
          {blocked ? <span className="muted">{blocked}</span> : mode === "OFF" ? <span className="muted">{notice}</span> : null}
        </div>
      ) : null}
      {canSend && open ? (
        <form
          className="card"
          noValidate
          onSubmit={async (e) => {
            e.preventDefault();
            const recipients = recipientsOf(saved, unticked, typed);
            const problem = recipientsError(recipients);
            setInvalid(problem);
            if (problem) {
              return;
            }
            if (await send(recipients, message.trim() === "" ? null : message.trim())) {
              setOpen(false);
              setTyped("");
              setMessage("");
              reload();
            }
          }}
        >
          <h3 style={{ marginTop: 0 }}>Enviar {what} por correo</h3>
          {notice ? (
            <p className="notice" data-testid="mail-mode-notice">
              {notice}
            </p>
          ) : null}
          {saved.length > 0 ? (
            <fieldset>
              <legend>Correos guardados del cliente</legend>
              {saved.map((address) => (
                <label key={address} className="check">
                  <input
                    type="checkbox"
                    checked={!unticked.has(address)}
                    onChange={(e) => {
                      const next = new Set(unticked);
                      if (e.target.checked) {
                        next.delete(address);
                      } else {
                        next.add(address);
                      }
                      setUnticked(next);
                    }}
                  />{" "}
                  {address}
                </label>
              ))}
            </fieldset>
          ) : (
            <p className="muted">El cliente no tiene correos guardados: escríbalos abajo (puede guardarlos en la ficha del cliente).</p>
          )}
          <Field label="Otros correos" wide hint="Separados por coma. Hasta 10 destinatarios en total." error={invalid}>
            <input aria-label="Otros correos" inputMode="email" value={typed} onChange={(e) => setTyped(e.target.value)} />
          </Field>
          <Field label="Mensaje (opcional)" wide hint="Se agrega al texto del correo, que ya nombra el documento adjunto.">
            <textarea aria-label="Mensaje del correo" rows={3} maxLength={2000} value={message} onChange={(e) => setMessage(e.target.value)} />
          </Field>
          <div className="actions form-actions">
            <button type="button" onClick={() => setOpen(false)}>
              Cancelar
            </button>
            <button type="submit" className="primary" disabled={busy}>
              Enviar correo
            </button>
          </div>
          <ErrorBox error={error} />
        </form>
      ) : null}
      {items.length > 0 ? (
        <div className="table-wrap">
          <table data-testid="mail-history">
            <thead>
              <tr>
                <th>Enviado el</th>
                <th>Destinatarios</th>
                <th>Estado</th>
                <th>Por</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {items.map((m) => (
                <tr key={m.mailId}>
                  <td>{formatDateTime(m.requestedAt)}</td>
                  <td className="wrap">{m.recipients.join(", ")}</td>
                  <td className="wrap">
                    <StatusBadge status={mailStatusTone(m.status)} label={mailStatusLabel(m.status)} testId="mail-status" />
                    <div className="muted">{deliveryText(m)}</div>
                  </td>
                  <td className="wrap">{m.requestedBy}</td>
                  <td>
                    <div className="actions row-buttons">
                      {m.hasPdf ? <DownloadPdf mailId={m.mailId} /> : null}
                      {m.status === "FAILED" && can("mail:retry") ? <Retry mailId={m.mailId} onDone={reload} /> : null}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : canSend ? (
        <p className="muted">Este documento todavía no se ha enviado por correo.</p>
      ) : null}
    </section>
  );
}

export function QuoteMail({ quoteId, quoteNo, blocked }: { quoteId: string; quoteNo: string; blocked: string | null }) {
  const { can } = useSession();
  const send = useCommand(`mail-quote:${quoteId}`, "/api/v1/companies/{companyId}/sales/send-quote-by-email", `Cotización ${quoteNo} puesta en cola para envío por correo.`);
  return (
    <MailPanel documentType="QUOTE" documentId={quoteId} what={`la cotización ${quoteNo}`} canSend={can("quote:email")} blocked={blocked} busy={send.busy} error={send.error}
      send={async (recipients, message) => (await send.run({ quoteId, recipients, message })) !== undefined} />
  );
}

/** VS4-05 (E-VS4-05-1): an invoice whose e-CF the DGII accepted — the PDF with the QR and the signed XML. */
export function InvoiceMail({ invoiceId, invoiceNo, blocked }: { invoiceId: string; invoiceNo: string; blocked: string | null }) {
  const { can } = useSession();
  const send = useCommand(`mail-invoice:${invoiceId}`, "/api/v1/companies/{companyId}/sales/send-invoice-by-email", `Factura ${invoiceNo} puesta en cola para envío por correo.`);
  return (
    <MailPanel documentType="INVOICE" documentId={invoiceId} what={`la factura ${invoiceNo} (PDF con el QR y el XML firmado)`} canSend={can("invoice:email")} blocked={blocked} busy={send.busy} error={send.error}
      send={async (recipients, message) => (await send.run({ invoiceId, recipients, message })) !== undefined} />
  );
}

export function ProformaMail({ proformaId, proformaNo, blocked }: { proformaId: string; proformaNo: string; blocked: string | null }) {
  const { can } = useSession();
  const send = useCommand(`mail-proforma:${proformaId}`, "/api/v1/companies/{companyId}/sales/send-proforma-by-email", `Proforma ${proformaNo} puesta en cola para envío por correo.`);
  return (
    <MailPanel documentType="PROFORMA" documentId={proformaId} what={`la proforma ${proformaNo}`} canSend={can("proforma:email")} blocked={blocked} busy={send.busy} error={send.error}
      send={async (recipients, message) => (await send.run({ proformaId, recipients, message })) !== undefined} />
  );
}

export function DeliveryMail({ deliveryId, deliveryNo, blocked }: { deliveryId: string; deliveryNo: string; blocked: string | null }) {
  const { can } = useSession();
  const send = useCommand(`mail-delivery:${deliveryId}`, "/api/v1/companies/{companyId}/sales/send-delivery-by-email", `Conduce ${deliveryNo} puesto en cola para envío por correo.`);
  return (
    <MailPanel documentType="DELIVERY" documentId={deliveryId} what={`el conduce ${deliveryNo}`} canSend={can("delivery:email")} blocked={blocked} busy={send.busy} error={send.error}
      send={async (recipients, message) => (await send.run({ deliveryId, recipients, message })) !== undefined} />
  );
}

export function StatementMail({ partyId, from, to }: { partyId: string; from: string; to: string }) {
  const { can } = useSession();
  const send = useCommand(`mail-statement:${partyId}`, "/api/v1/companies/{companyId}/sales/send-statement-by-email", "Estado de cuenta puesto en cola para envío por correo.");
  return (
    <MailPanel documentType="STATEMENT" documentId={partyId} what="el estado de cuenta de este período" title="Correo — estado de cuenta" button="Enviar estado de cuenta por correo" canSend={can("statement:email")} blocked={null} busy={send.busy} error={send.error}
      send={async (recipients, message) => (await send.run({ partyId, from, to, recipients, message })) !== undefined} />
  );
}

export function AgingMail({ partyId }: { partyId: string }) {
  const { can } = useSession();
  const send = useCommand(`mail-aging:${partyId}`, "/api/v1/companies/{companyId}/sales/send-ar-aging-by-email", "Facturas pendientes puestas en cola para envío por correo.");
  return (
    <MailPanel documentType="AR_AGING" documentId={partyId} what="las facturas pendientes de hoy" title="Correo — facturas pendientes" button="Enviar facturas pendientes por correo" canSend={can("statement:email")} blocked={null} busy={send.busy} error={send.error}
      send={async (recipients, message) => (await send.run({ partyId, recipients, message })) !== undefined} />
  );
}
