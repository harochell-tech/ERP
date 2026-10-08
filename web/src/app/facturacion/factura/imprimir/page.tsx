"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { EcfQr } from "@/components/EcfGateway";
import { LoadingIndicator } from "@/components/StateNotices";
import { Money, NoPermission } from "@/components/ui";
import { Watermark } from "@/components/Watermark";
import { formatQuantity } from "@/lib/decimal";
import { ECF_TYPES } from "@/lib/ecf";
import { formatDate, formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS4-04 (E-VS4-04-4): the printable invoice, letter size — issuer, customer, lines, totals and, once the DGII accepted its e-CF, the
// e-NCF, the security code, the signature date and the QR of the DGII stamp. Until then it carries «SIN VALIDEZ FISCAL». The browser
// prints it; no PDF is generated here (the signed PDF is Alanube's, in Fiscal › e-CF).

function InvoicePrint() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const allowed = can("sales:read") && id !== "";
  const detail = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/sales/invoices/{invoiceId}", { path: { companyId, invoiceId: id } }) : null, [companyId, id]);
  const pkg = useLoad(
    allowed ? () => query("/api/v1/companies/{companyId}/sales/invoices/{invoiceId}/fiscal-package", { path: { companyId, invoiceId: id } }) : null,
    [companyId, id],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (detail.data === null || pkg.data === null) {
    return <LoadingIndicator error={detail.error ?? pkg.error} />;
  }
  const h = detail.data.header;
  const ecf = detail.data.ecf;
  const issuer = detail.data.issuer;
  const accepted = ecf ? ecf.status === "ACCEPTED" || ecf.status === "ACCEPTED_CONDITIONAL" : h.fiscalStatus === "ACCEPTED_EXTERNAL";
  const encf = ecf && accepted ? ecf.encf : h.encf;
  return (
    <div className="proforma">
      <Watermark text={h.commercialStatus === "VOIDED" ? "ANULADA" : accepted ? null : "SIN VALIDEZ FISCAL"} />
      <div className="actions no-print">
        <Link href={`/facturacion/factura/?id=${id}`}>← Factura {h.invoiceNo}</Link>
        <button type="button" className="primary" onClick={() => window.print()}>
          Imprimir
        </button>
      </div>
      <header className="print-head">
        <div>
          <strong>{issuer?.tradeName ?? issuer?.legalName ?? pkg.data.issuerName}</strong>
          {issuer?.tradeName ? <div>{issuer.legalName}</div> : null}
          <div>
            RNC <span className="mono">{issuer?.rnc ?? pkg.data.issuerRnc}</span>
          </div>
          {issuer?.address ? <div>{issuer.address}</div> : null}
          {issuer?.phone || issuer?.email ? <div>{[issuer.phone, issuer.email].filter(Boolean).join(" · ")}</div> : null}
        </div>
        <div className="print-number">
          <h1>{ECF_TYPES[h.ecfType]?.replace(/^\d+ · /, "Factura de ") ?? "Factura"}</h1>
          <div>
            e-NCF <span className="mono" data-testid="print-encf">{encf ?? "—"}</span>
          </div>
          <div>
            Factura <span className="mono">{h.invoiceNo}</span> del {formatDate(h.invoiceDate)}
          </div>
          {h.dueDate && h.dueDate !== h.invoiceDate ? <div>Vence el {formatDate(h.dueDate)}</div> : null}
        </div>
      </header>
      <dl className="facts">
        <dt>Cliente</dt>
        <dd>
          {pkg.data.receiverName}
          {pkg.data.receiverRnc ? (
            <>
              {" "}
              · RNC / cédula <span className="mono">{pkg.data.receiverRnc}</span>
            </>
          ) : null}
          {pkg.data.receiverPassport ? ` · pasaporte ${pkg.data.receiverPassport}` : ""}
        </dd>
        {pkg.data.exemption ? (
          <>
            <dt>Exención</dt>
            <dd>
              {pkg.data.exemption.regime} certificación {pkg.data.exemption.certificateNo} · {pkg.data.exemption.projectName}
            </dd>
          </>
        ) : null}
      </dl>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th className="num">#</th>
              <th>Descripción</th>
              <th>Unidad</th>
              <th className="num">Cantidad</th>
              <th className="num">Precio (RD$)</th>
              <th className="num">ITBIS (RD$)</th>
              <th className="num">Importe (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {detail.data.lines.map((l) => (
              <tr key={l.lineNo}>
                <td className="num">{l.lineNo}</td>
                <td className="wrap">
                  {l.itemCode} — {l.itemDescription}
                  <span className="muted"> · conduce {l.deliveryNo}</span>
                </td>
                <td>{l.uom}</td>
                <td className="num">{formatQuantity(l.quantity)}</td>
                <td className="num">
                  <Money value={l.unitPrice} />
                </td>
                <td className="num">
                  <Money value={l.itbis} />
                </td>
                <td className="num">
                  <Money value={l.netAmount} />
                </td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr>
              <th colSpan={6} className="num">
                Subtotal
              </th>
              <td className="num">
                <Money value={h.netTotal} />
              </td>
            </tr>
            <tr>
              <th colSpan={6} className="num">
                ITBIS
              </th>
              <td className="num">
                <Money value={h.taxTotal} />
              </td>
            </tr>
            <tr>
              <th colSpan={6} className="num">
                Total
              </th>
              <td className="num">
                <strong>
                  <Money value={h.total} currency testId="print-total" />
                </strong>
              </td>
            </tr>
          </tfoot>
        </table>
      </div>
      {ecf && accepted ? (
        <div className="ecf-print" data-testid="print-ecf">
          <EcfQr url={ecf.stampUrl} size={112} />
          <div>
            <div>
              Código de seguridad: <span className="mono">{ecf.securityCode}</span>
            </div>
            <div>Fecha de firma digital: {formatDateTime(ecf.signatureDate)}</div>
          </div>
        </div>
      ) : null}
    </div>
  );
}

export default function Page() {
  return (
    <Suspense fallback={<LoadingIndicator />}>
      <InvoicePrint />
    </Suspense>
  );
}
