"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { ProformaMail } from "@/components/DocumentMail";
import { SalesHistory } from "@/components/SalesUx4";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Money, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { Watermark } from "@/components/Watermark";
import { formatQuantity } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { certificationLabel, certificationStatus, collectsLabel, dueText, proformaStatusLabel } from "@/lib/proformas";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// FIS1b-07 (E-FIS1b-1, E-FIS1b-9, E-FIS1b-01-11, 13): a proforma — the delivery's lines with their ITBIS, what it collects, what
// receipts were assigned to it and the state of its certification. It prints with room for the supplier's signature and stamp (the
// document the customer takes to the DGII). Facturación voids an open proforma without collections (proforma:void).

function Proforma() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/proformas/{proformaId}", { path: { companyId, proformaId: id } }) : null,
    [companyId, id],
  );
  const voidProforma = useCommand(`void-proforma:${id}`, "/api/v1/companies/{companyId}/sales/void-proforma", "Proforma anulada: su conduce vuelve a «Por facturar».");
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const f = data.header;
  return (
    <div className="proforma">
      <Watermark text={f.status === "VOIDED" ? "ANULADA" : null} />
      <div className="actions no-print">
        <Link href="/facturacion/proformas/">← Proformas</Link>
        {/* PRT-01: the printed proforma is the server's, with the company's format. */}
        <Link className="button primary" href={`/facturacion/proforma/imprimir/?id=${id}`}>
          Imprimir
        </Link>
        {f.status === "OPEN" && can("proforma:void") ? (
          <ReasonAction
            label="Anular proforma"
            consequence={`La proforma ${f.proformaNo} quedará anulada y el conduce ${f.deliveryNo} volverá a «Por facturar». Solo se anula si no tiene cobros asignados ni la cita una autorización fiscal.`}
            busy={voidProforma.busy}
            onConfirm={async (reason) => {
              if (await voidProforma.run({ proformaId: id, expectedVersion: f.version, reason })) {
                reload();
              }
            }}
          />
        ) : null}
      </div>
      <ErrorBox error={voidProforma.error} />
      <h1>
        Proforma <span className="mono" data-testid="proforma-no">{f.proformaNo}</span>
      </h1>
      <p className="no-print">
        <StatusBadge status={f.status === "OPEN" ? "DRAFT" : f.status === "INVOICED" ? "ACTIVE" : "REJECTED"} label={proformaStatusLabel(f.status)} testId="proforma-status" />{" "}
        <StatusBadge status={certificationStatus(f.certification)} label={certificationLabel(f.certification)} />{" "}
        {f.invoiceId ? <Link href={`/facturacion/factura/?id=${f.invoiceId}`}>Factura {f.invoiceNo}</Link> : null}
        {data.voidReason ? <span className="muted"> · {data.voidReason}</span> : null}
      </p>
      <p>
        Conduce <span className="mono">{f.deliveryNo}</span> · pedido <span className="mono">{f.orderNo}</span> · entregado el {formatDate(f.proformaDate)} · vence el{" "}
        {formatDate(f.dueDate)}
      </p>
      <dl className="facts">
        <dt>Suplidor</dt>
        <dd>
          {data.issuerName} · RNC <span className="mono">{data.issuerRnc}</span>
        </dd>
        <dt>Cliente</dt>
        <dd>
          {f.customerName} · RNC <span className="mono">{f.customerRnc ?? "—"}</span>
        </dd>
        {data.siteAddress ? (
          <>
            <dt>Obra</dt>
            <dd>{data.siteAddress}</dd>
          </>
        ) : null}
      </dl>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th className="num">#</th>
              <th>Producto</th>
              <th>Unidad</th>
              <th className="num">Cantidad</th>
              <th className="num">Precio (RD$)</th>
              <th className="num">Neto (RD$)</th>
              <th className="num">ITBIS (RD$)</th>
              <th className="num">Total (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {data.lines.map((l) => (
              <tr key={l.lineNo}>
                <td className="num">{l.lineNo}</td>
                <td>
                  {l.itemCode} — {l.itemName}
                </td>
                <td>{l.uom}</td>
                <td className="num">{formatQuantity(l.quantity)}</td>
                <td className="num">
                  <Money value={l.unitPrice} />
                </td>
                <td className="num">
                  <Money value={l.net} />
                </td>
                <td className="num">
                  <Money value={l.itbis} />
                </td>
                <td className="num">
                  <Money value={l.total} />
                </td>
              </tr>
            ))}
            <tr>
              <th colSpan={5}>Totales (RD$)</th>
              <td className="num">
                <Money value={f.net} testId="proforma-net" />
              </td>
              <td className="num">
                <Money value={f.itbis} testId="proforma-itbis" />
              </td>
              <td className="num">
                <Money value={f.total} testId="proforma-total" />
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <p className="muted">Documento no fiscal. El ITBIS se calculó con la regla vigente el día de la entrega; el comprobante fiscal se emite cuando la DGII resuelva la exención.</p>
      <div className="no-print">
        <h2>Cobro</h2>
        <dl className="summary">
          <div>
            <dt>{collectsLabel(f.collectsItbis)}</dt>
            <dd>{dueText(f, formatDate(f.dueDate))}</dd>
          </div>
          <div>
            <dt>Cobrado</dt>
            <dd>
              <Money value={f.allocated} currency testId="proforma-allocated" />
            </dd>
          </div>
          <div>
            <dt>Depósito del cliente (ITBIS adelantado)</dt>
            <dd>
              <Money value={f.deposit} currency testId="proforma-deposit" />
            </dd>
          </div>
          <div>
            <dt>Saldo por cobrar</dt>
            <dd>
              <Money value={f.balance} currency testId="proforma-balance" />
            </dd>
          </div>
        </dl>
        {data.collections.length > 0 ? (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Recibo</th>
                  <th>Forma</th>
                  <th>Asignado el</th>
                  <th className="num">Monto (RD$)</th>
                </tr>
              </thead>
              <tbody>
                {data.collections.map((c) => (
                  <tr key={c.allocationId}>
                    <td className="mono">
                      <Link href={`/cobros/recibo/?id=${c.receiptId}`}>{c.receiptNo}</Link>
                    </td>
                    <td>{c.method}</td>
                    <td>{formatDateTime(c.at)}</td>
                    <td className="num">
                      <Money value={c.amount} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <p className="muted">Aún no se le ha asignado ningún cobro. Se asigna desde el recibo del cliente, en Cobros.</p>
        )}
        <ProformaMail proformaId={id} proformaNo={f.proformaNo} blocked={f.status === "VOIDED" ? "Una proforma anulada no se envía." : null} />
        <SalesHistory history={data.history} />
      </div>
      <div className="signature">
        <div>
          <div className="signature-line" />
          Firma del suplidor
        </div>
        <div>
          <div className="signature-box" />
          Sello del suplidor
        </div>
      </div>
    </div>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Proforma />
    </Suspense>
  );
}
