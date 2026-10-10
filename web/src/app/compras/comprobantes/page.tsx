"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { MissingKeys, prepareInvoiceFile, useOcrEnabled } from "@/components/InvoicePhoto";
import { QrScan } from "@/components/QrScan";
import { ErrorBox, Loading, NoPermission } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { DOCUMENT_TABS, documentStatusLabel, documentTypeLabel, responseLabel, sourcesLabel } from "@/lib/supplierDocuments";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// OCR1-03 (E-OCR-2/4, E-OCR1-03-1/6/8): Compras › Comprobantes recibidos — the e-CF suppliers sent through Alanube, the ones captured
// from a QR, and (OCR1-04) photos; tabs by status, search, and «Escanear QR».

function Inbox() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const params = useSearchParams();
  const tab = params.get("estado") ?? "CAPTURED";
  const status = tab === "TODOS" ? null : tab;
  const unsent = params.get("sinEnviar") === "1";
  const [search, setSearch] = useState("");
  const [applied, setApplied] = useState("");
  const [scanning, setScanning] = useState(false);
  const capture = useCommand("capture-qr", "/api/v1/companies/{companyId}/procurement/capture-supplier-document-from-qr");
  // OCR1-04 (E-OCR1-04-1…4): a photo, scan or PDF read by AI.
  const ocr = useOcrEnabled();
  const photo = useCommand("capture-photo", "/api/v1/companies/{companyId}/procurement/capture-supplier-document-from-image");
  const [pending, setPending] = useState<{ content: string; needs: string[] } | null>(null);
  const [photoProblem, setPhotoProblem] = useState<string | null>(null);
  const { data, error } = useLoad(
    can("supplier_invoice:read")
      ? () =>
          query("/api/v1/companies/{companyId}/procurement/supplier-documents", {
            path: { companyId },
            query: { status: unsent ? undefined : (status ?? undefined), search: applied || undefined, unsentOver24Hours: unsent ? "true" : undefined, limit: 200 },
          })
      : null,
    [companyId, status, applied, unsent],
  );

  if (!can("supplier_invoice:read")) {
    return <NoPermission />;
  }

  const fromQr = async (link: string) => {
    const response = await capture.run({ qrUrl: link }, undefined, (r) =>
      (r.result as { created?: boolean } | null)?.created ? "Comprobante capturado desde el QR." : "El QR se agregó al comprobante que ya estaba.",
    );
    const id = (response?.result as { supplierDocumentId?: string } | null)?.supplierDocumentId;
    if (id) {
      router.push(`/compras/comprobante/?id=${id}`);
    }
  };

  const sendPhoto = async (content: string, rnc: string | null, ncf: string | null) => {
    const response = await photo.run({ contentBase64: content, issuerRnc: rnc, fiscalNumber: ncf }, undefined, (r) =>
      (r.result as { supplierDocumentId?: string | null } | null)?.supplierDocumentId ? "Factura leída: revise lo que leyó la IA." : "Falta un dato de la factura.",
    );
    const result = response?.result as { supplierDocumentId?: string | null; needsInput?: string[] } | null;
    if (result?.supplierDocumentId) {
      setPending(null);
      router.push(`/compras/comprobante/?id=${result.supplierDocumentId}`);
    } else if (result?.needsInput && result.needsInput.length > 0) {
      setPending({ content, needs: result.needsInput });
    }
  };

  const fromFile = async (file: File | undefined) => {
    setPhotoProblem(null);
    if (!file) {
      return;
    }
    const prepared = await prepareInvoiceFile(file);
    if ("problem" in prepared) {
      setPhotoProblem(prepared.problem);
      return;
    }
    await sendPhoto(prepared.content, null, null);
  };

  return (
    <>
      <h1>Comprobantes recibidos</h1>
      <p className="muted">
        Las facturas que los proveedores envían como e-CF llegan solas desde Alanube cada hora. Las impresas se capturan con su QR. Nada se registra
        solo: cada comprobante se pasa a factura y se revisa.
      </p>
      {can("supplier_document:capture") ? (
        <div className="actions">
          <button type="button" className="primary" onClick={() => setScanning((s) => !s)}>
            {scanning ? "Cerrar" : "Escanear QR"}
          </button>
          {ocr ? (
            <label className="button">
              {photo.busy ? "Leyendo la factura…" : "Subir foto o PDF"}
              <input
                type="file"
                accept="image/*,application/pdf"
                hidden
                disabled={photo.busy}
                aria-label="Foto o PDF de la factura"
                onChange={(e) => {
                  void fromFile(e.target.files?.[0]);
                  e.target.value = "";
                }}
              />
            </label>
          ) : null}
        </div>
      ) : null}
      {pending ? <MissingKeys needs={pending.needs} busy={photo.busy} onSend={(rnc, ncf) => void sendPhoto(pending.content, rnc, ncf)} /> : null}
      {photoProblem ? (
        <p className="error" role="alert">
          {photoProblem}
        </p>
      ) : null}
      <ErrorBox error={photo.error} />
      {scanning ? <QrScan busy={capture.busy} onLink={(link) => void fromQr(link)} /> : null}
      <ErrorBox error={capture.error} />
      <nav className="tabs" aria-label="Estado">
        {DOCUMENT_TABS.map((t) => {
          const value = t.status ?? "TODOS";
          return (
            <Link key={value} href={`/compras/comprobantes/?estado=${value}`} aria-current={!unsent && value === tab ? "page" : undefined}>
              {t.label}
            </Link>
          );
        })}
        {unsent ? <span aria-current="page">Respuestas sin enviar</span> : null}
      </nav>
      <form
        className="actions"
        role="search"
        onSubmit={(e) => {
          e.preventDefault();
          setApplied(search.trim());
        }}
      >
        <input type="search" aria-label="Buscar comprobante" placeholder="RNC, nombre o número" value={search} onChange={(e) => setSearch(e.target.value)} />
        <button type="submit">Buscar</button>
      </form>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted" data-testid="documents-empty">
          No hay comprobantes {status === "CAPTURED" && !unsent ? "pendientes" : "aquí"}.
        </p>
      ) : (
        <div className="table-wrap">
          <table data-testid="supplier-documents">
            <thead>
              <tr>
                <th>Fecha</th>
                <th>Proveedor</th>
                <th>Comprobante</th>
                <th className="num">Total (RD$)</th>
                <th>Llegó por</th>
                <th>Respuesta DGII</th>
                <th>Estado</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((d) => (
                <tr key={d.supplierDocumentId} data-testid={`document-row-${d.fiscalNumber}`}>
                  <td>{d.docDate ? formatDate(d.docDate) : "—"}</td>
                  <td className="wrap">
                    {d.issuerName ?? "—"}
                    <br />
                    <span className="muted mono">{d.issuerRnc}</span>
                  </td>
                  <td>
                    <Link href={`/compras/comprobante/?id=${d.supplierDocumentId}`}>{d.fiscalNumber}</Link>
                    <br />
                    <span className="muted">{documentTypeLabel(d.ecfType)}</span>
                  </td>
                  <td className="num">{d.totalAmount != null ? formatDecimal(d.totalAmount) : "—"}</td>
                  <td>
                    {sourcesLabel(d)}
                    {d.aiRead ? <span className="badge tone-attention"> leído por IA</span> : null}
                  </td>
                  <td>{d.hasXml || d.receivedStatus ? responseLabel(d.commercialResponse, d.responseSent) : "—"}</td>
                  <td>{documentStatusLabel(d.status)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Inbox />
    </Suspense>
  );
}
