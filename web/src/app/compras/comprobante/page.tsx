"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { History } from "@/components/History";
import { ConfirmAction, ErrorBox, Loading, Money, NoPermission, ReasonAction } from "@/components/ui";
import { downloadBase64 } from "@/lib/ecf";
import { formatDecimal, formatQuantity } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import {
  aiFieldLabel,
  checkLabel,
  documentStatusLabel,
  documentTypeLabel,
  invoiceFormHref,
  responseLabel,
  type SupplierDocument,
} from "@/lib/supplierDocuments";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// OCR1-03 (E-OCR-3/6, E-OCR1-01-3/4, E-OCR1-03-2…5/9): one captured supplier document — its header and lines as read, what is flagged in
// red, the answer before the DGII, «Pasar a factura», «Descartar», its files and history.

function Actions({ doc, onDone }: { doc: SupplierDocument; onDone: () => void }) {
  const { can } = useSession();
  const id = doc.supplierDocumentId;
  const target = { supplierDocumentId: id, expectedVersion: doc.version };
  const respond = useCommand(`respond-doc:${id}`, "/api/v1/companies/{companyId}/procurement/respond-to-supplier-document");
  const discard = useCommand(`discard-doc:${id}`, "/api/v1/companies/{companyId}/procurement/discard-supplier-document", `Comprobante ${doc.fiscalNumber} descartado.`);
  const busy = respond.busy || discard.busy;
  const answerable = doc.providerId != null && doc.receivedStatus === "RECEIVED" && doc.commercialResponse === "NOT_DECLARED" && can("supplier_document:respond");
  const after = (response: unknown) => {
    if (response) {
      onDone();
    }
  };

  return (
    <>
      <div className="actions">
        {doc.status === "CAPTURED" && doc.registrable && can("supplier_invoice:register") ? (
          doc.supplierId ? (
            <>
              <Link className="button primary" href={invoiceFormHref("expense", id)}>
                Pasar a factura de gastos
              </Link>
              <Link className="button" href={invoiceFormHref("inventory", id)}>
                Pasar a factura de inventario
              </Link>
            </>
          ) : can("supplier:create") ? (
            <Link className="button primary" href={`/maestros/proveedores/?nuevo=1&rnc=${doc.issuerRnc}`}>
              Crear proveedor
            </Link>
          ) : null
        ) : null}
        {answerable ? (
          <ConfirmAction
            label="Aceptar ante la DGII"
            consequence="Se informa a la DGII que la empresa acepta este e-CF. No se puede cambiar después."
            stepUp
            busy={busy}
            onConfirm={async () => after(await respond.run({ ...target, accept: true }, undefined, `e-CF ${doc.fiscalNumber} aceptado ante la DGII.`))}
          />
        ) : null}
        {answerable && doc.supplierInvoiceStatus !== "MATCHED" ? (
          <ReasonAction
            label="Rechazar ante la DGII"
            consequence="Se informa a la DGII que la empresa no acepta este e-CF. No se puede cambiar después."
            minLength={3}
            stepUp
            busy={busy}
            onConfirm={async (reason) => after(await respond.run({ ...target, accept: false, reason }, undefined, `e-CF ${doc.fiscalNumber} rechazado ante la DGII.`))}
          />
        ) : null}
        {doc.status === "CAPTURED" && can("supplier_document:capture") ? (
          <ReasonAction
            label="Descartar"
            consequence={
              answerable
                ? "El comprobante sale de los pendientes. Si el e-CF no es de la empresa, rechácelo también ante la DGII."
                : "El comprobante sale de los pendientes."
            }
            minLength={3}
            busy={busy}
            onConfirm={async (reason) => after(await discard.run({ ...target, reason }))}
          />
        ) : null}
      </div>
      <ErrorBox error={respond.error ?? discard.error} />
    </>
  );
}

function DocumentDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data: doc, error, reload } = useLoad(
    can("supplier_invoice:read") && id
      ? () => query("/api/v1/companies/{companyId}/procurement/supplier-documents/{supplierDocumentId}", { path: { companyId, supplierDocumentId: id } })
      : null,
    [companyId, id],
  );

  if (!can("supplier_invoice:read")) {
    return <NoPermission />;
  }
  if (doc === null) {
    return <Loading error={error} />;
  }
  const ai = new Set(doc.aiFields);
  const read = (field: string) => (ai.has(field) ? <span className="badge tone-attention"> leído por IA</span> : null);
  const xml = doc.files.find((f) => f.kind === "XML");
  const download = async () => {
    if (!xml) {
      return;
    }
    const file = await query("/api/v1/companies/{companyId}/procurement/supplier-documents/{supplierDocumentId}/files/{fileId}", {
      path: { companyId, supplierDocumentId: doc.supplierDocumentId, fileId: xml.fileId },
    });
    downloadBase64(file.fileName, file.contentBase64, file.contentType);
  };

  return (
    <>
      <p>
        <Link href="/compras/comprobantes/">← Comprobantes recibidos</Link>
      </p>
      <h1>
        {documentTypeLabel(doc.ecfType)} {doc.fiscalNumber}
      </h1>
      {doc.checks.length > 0 ? (
        <ul className="error" role="alert" data-testid="document-checks">
          {doc.checks.map((c) => (
            <li key={c} data-testid={`check-${c}`}>
              {checkLabel(c)}
            </li>
          ))}
        </ul>
      ) : null}
      <dl className="facts">
        <dt>Estado</dt>
        <dd data-testid="document-status">{documentStatusLabel(doc.status)}</dd>
        {doc.discardReason ? (
          <>
            <dt>Motivo del descarte</dt>
            <dd>{doc.discardReason}</dd>
          </>
        ) : null}
        <dt>Emisor{read("issuer_rnc")}</dt>
        <dd>
          {doc.supplierName ?? doc.issuerName ?? doc.registryName ?? "—"} · <span className="mono">{doc.issuerRnc}</span>
          {doc.supplierId ? null : <span className="muted"> (no es proveedor en el sistema)</span>}
        </dd>
        <dt>Fecha{read("doc_date")}</dt>
        <dd>{doc.docDate ? formatDate(doc.docDate) : "—"}</dd>
        <dt>Total{read("total_amount")}</dt>
        <dd>{doc.totalAmount != null ? <Money value={doc.totalAmount} currency testId="document-total" /> : "—"}</dd>
        <dt>ITBIS{read("itbis_amount")}</dt>
        <dd>{doc.itbisAmount != null ? <Money value={doc.itbisAmount} currency /> : "—"}</dd>
        {doc.qrTotalAmount != null ? (
          <>
            <dt>Total según el QR</dt>
            <dd>
              <Money value={doc.qrTotalAmount} currency />
            </dd>
          </>
        ) : null}
        {doc.securityCode ? (
          <>
            <dt>Código de seguridad</dt>
            <dd className="mono">{doc.securityCode}</dd>
          </>
        ) : null}
        <dt>Respuesta ante la DGII</dt>
        <dd data-testid="document-response">
          {responseLabel(doc.commercialResponse, doc.responseSentAt != null, doc.providerId)}
          {doc.respondedBy ? ` · ${doc.respondedBy}, ${formatDateTime(doc.respondedAt)}` : ""}
          {doc.responseReason ? ` · ${doc.responseReason}` : ""}
        </dd>
        {doc.supplierInvoiceId ? (
          <>
            <dt>Factura</dt>
            <dd>
              <Link href={`/cxp/factura/?id=${doc.supplierInvoiceId}`} data-testid="document-invoice">
                Ver la factura registrada
              </Link>
            </dd>
          </>
        ) : null}
      </dl>
      <Actions doc={doc} onDone={reload} />
      <div className="actions">
        {doc.qrUrl ? (
          <a className="button" href={doc.qrUrl} target="_blank" rel="noopener noreferrer">
            Verificar en la DGII
          </a>
        ) : null}
        {xml ? (
          <button type="button" onClick={() => void download()}>
            Descargar XML
          </button>
        ) : null}
      </div>
      <h2>Líneas{read("lines")}</h2>
      {doc.lines.length === 0 ? (
        <p className="muted">Sin líneas leídas.</p>
      ) : (
        <div className="table-wrap">
          <table data-testid="document-lines">
            <thead>
              <tr>
                <th>#</th>
                <th>Descripción</th>
                <th className="num">Cantidad</th>
                <th className="num">Precio (RD$)</th>
                <th className="num">Monto (RD$)</th>
                <th>ITBIS</th>
              </tr>
            </thead>
            <tbody>
              {doc.lines.map((l) => (
                <tr key={`${l.source}-${l.lineNo}`}>
                  <td>{l.lineNo}</td>
                  <td className="wrap">
                    {l.description}
                    {l.itemCode ? <span className="muted"> · {l.itemCode}</span> : null}
                  </td>
                  <td className="num">{formatQuantity(l.quantity)}</td>
                  <td className="num">{formatDecimal(l.unitPrice)}</td>
                  <td className="num">{formatDecimal(l.amount)}</td>
                  <td>{l.billingIndicator === 4 ? "Exento" : l.billingIndicator === 1 || l.billingIndicator === 2 || l.billingIndicator === 3 ? "Gravado" : "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {doc.aiFields.length > 0 ? <p className="muted">Leído por IA: {doc.aiFields.map(aiFieldLabel).join(", ")}. Revíselo contra la factura.</p> : null}
      <History history={doc.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <DocumentDetail />
    </Suspense>
  );
}
