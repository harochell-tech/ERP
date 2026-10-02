"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { CopyField, RecordEcfForm } from "@/components/Ecf";
import { SalesHistory } from "@/components/SalesUx4";
import { LoadingIndicator } from "@/components/StateNotices";
import { AccountingStatus, ConfirmAction, ErrorBox, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDecimal, formatPercent } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { creditNoteReasonLabel } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { canSeeAccounting, creditNoteStatusLabel } from "@/lib/ux4bSales";

// VS3-10b (E-VS3-06-1…10): a credit note — issued (P-22, step-up) by someone other than who issued the invoice, then its e-CF 34
// with the modified e-NCF from the package.

function NotePackage({ creditNoteId }: { creditNoteId: string }) {
  const { companyId } = useSession();
  const { data, error } = useLoad(
    () => query("/api/v1/companies/{companyId}/sales/credit-notes/{creditNoteId}/fiscal-package", { path: { companyId, creditNoteId } }),
    [companyId, creditNoteId],
  );
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  return (
    <>
      <h2>Paquete fiscal para el portal</h2>
      <div className="table-wrap"><table>
        <tbody>
          <CopyField label="Tipo de e-CF" value={data.ecfType} />
          <CopyField label="e-NCF modificado" value={data.modifiedEncf} />
          <CopyField label="RNC del emisor" value={data.issuerRnc} />
          <CopyField label="RNC del receptor" value={data.receiverRnc} />
          {data.receiverPassport ? <CopyField label="Pasaporte del receptor" value={data.receiverPassport} /> : null}
          <CopyField label="Receptor" value={data.receiverName} />
          <CopyField label="Fecha" value={data.creditDate} />
          <CopyField label="Motivo" value={`${creditNoteReasonLabel(data.reasonCategory)}: ${data.reason}`} />
          <CopyField label="Neto" value={data.netTotal} />
          <CopyField label="ITBIS" value={data.taxTotal} />
          <CopyField label="Total" value={data.total} />
        </tbody>
      </table></div>
    </>
  );
}

function NoteDetail() {
  const { companyId, can, isMyUserId } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/credit-notes/{creditNoteId}", { path: { companyId, creditNoteId: id } }) : null,
    [companyId, id],
  );
  const issue = useCommand(`issue-credit-note:${id}`, "/api/v1/companies/{companyId}/sales/issue-credit-note", () => `Nota de crédito ${data?.header.creditNoteNo ?? ""} emitida y contabilizada.`);
  const record = useCommand(`record-credit-note-ecf:${id}`, "/api/v1/companies/{companyId}/sales/record-external-credit-note-document", () => `e-CF 34 registrado en la nota de crédito ${data?.header.creditNoteNo ?? ""}.`);
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const h = data.header;
  return (
    <>
      <p>
        <Link href="/facturacion/notas/">← Notas de crédito</Link>
      </p>
      <h1>
        Nota de crédito {h.creditNoteNo} <StatusBadge status={h.commercialStatus} label={creditNoteStatusLabel(h.commercialStatus)} testId="credit-note-status" />{" "}
        <StatusBadge status={h.fiscalStatus} />
      </h1>
      <p>
        Factura <Link href={`/facturacion/factura/?id=${h.invoiceId}`}>{h.invoiceNo}</Link> (e-NCF {h.invoiceEncf ?? "—"}) · {h.customerName} · fecha {formatDate(h.creditDate)}
        {canSeeAccounting(can) ? (
          <>
            {" "}
            · contabilidad <AccountingStatus status={h.accountingStatus} eventId={data.postingEventId} />
          </>
        ) : null}
      </p>
      <p className="muted">
        Motivo: {creditNoteReasonLabel(h.reasonCategory)} — {h.reason} · creó {data.createdBy ?? "—"} · emitió {data.issuedBy ?? "—"}
      </p>
      {/* UX3-02 (E-UX3-9): whoever issued the invoice may not issue its credit note (the server refuses it). */}
      {h.commercialStatus === "DRAFT" && can("credit_note:issue") && isMyUserId(data.invoiceIssuedById) ? (
        <p className="notice" data-testid="credit-note-own-invoice">
          Usted emitió la factura {h.invoiceNo}: otra persona con permiso de emitir notas de crédito debe emitir esta nota.
        </p>
      ) : null}
      {h.commercialStatus === "DRAFT" && can("credit_note:issue") && !isMyUserId(data.invoiceIssuedById) ? (
        <div className="actions">
          <ConfirmAction
            label="Emitir nota de crédito"
            className="primary"
            stepUp
            busy={issue.busy}
            consequence={`La nota ${h.creditNoteNo} se confirma y se contabiliza: reduce el saldo de la factura ${h.invoiceNo} por RD$ ${formatDecimal(h.total ?? h.netTotal)}. Ya no se puede editar; después se registra su e-CF 34.`}
            onConfirm={async () => (await issue.run({ creditNoteId: h.creditNoteId, expectedVersion: h.version })) && reload()}
          />
          <ErrorBox error={issue.error} />
        </div>
      ) : null}
      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th className="num">Línea de factura</th>
            <th>Producto</th>
            <th className="num">Neto (RD$)</th>
            <th className="num">Tasa</th>
            <th className="num">ITBIS (RD$)</th>
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => (
            <tr key={l.lineNo}>
              <td className="num">{l.invoiceLineNo}</td>
              <td>
                {l.itemCode} — {l.itemDescription}
              </td>
              <td className="num">
                <Money value={l.netAmount} />
              </td>
              <td className="num">{formatPercent(l.rate)}</td>
              <td className="num">
                <Money value={l.itbis} />
              </td>
            </tr>
          ))}
          <tr>
            <th colSpan={2}>Totales</th>
            <td className="num">
              <Money value={h.netTotal} />
            </td>
            <td />
            <td className="num">
              <Money value={h.taxTotal} />
            </td>
          </tr>
          <tr>
            <th colSpan={4}>Total</th>
            <td className="num">
              <Money value={h.total} />
            </td>
          </tr>
        </tbody>
      </table></div>
      {h.fiscalStatus === "PENDING_EXTERNAL" ? <NotePackage creditNoteId={h.creditNoteId} /> : null}
      {h.fiscalStatus === "PENDING_EXTERNAL" && can("fiscal_document:record") ? (
        <>
          <h2>Registrar el e-CF 34 emitido en el portal</h2>
          <RecordEcfForm consumer={h.invoiceEncf?.startsWith("E32") ?? false} prefix="E34" busy={record.busy} error={record.error} onSubmit={async (v) => (await record.run({ creditNoteId: h.creditNoteId, expectedVersion: h.version, ...v })) && reload()} />
        </>
      ) : null}
      {data.fiscalRecord ? (
        <p>
          e-CF {data.fiscalRecord.encf} emitido {formatDateTime(data.fiscalRecord.issuedAt)} · código {data.fiscalRecord.securityCode} · {data.fiscalRecord.evidenceRef}
        </p>
      ) : null}
      <SalesHistory history={data.history} label={creditNoteStatusLabel} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <NoteDetail />
    </Suspense>
  );
}
