"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, Money, NoPermission } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// FIS1-05 (E-FIS1-05-7): the order's proforma the customer takes to the DGII to request the CONFOTUR exemption — issuer, customer,
// lines with the ITBIS at the rules in force today (the server's, never stored) and a blank area for the supplier's signature and
// stamp. The browser prints it (the menu and the buttons are hidden when printing); no PDF is generated.

function Proforma() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/tax/proformas/{salesOrderId}", { path: { companyId, salesOrderId: id } }) : null,
    [companyId, id],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  return (
    <div className="proforma">
      <div className="actions no-print">
        <Link href={`/ventas/pedido/?id=${id}`}>← Pedido {data.orderNo}</Link>
        <button type="button" className="primary" onClick={() => window.print()}>
          Imprimir
        </button>
      </div>
      <h1>Proforma</h1>
      <p>
        Pedido <span className="mono">{data.orderNo}</span> del {formatDate(data.orderDate)} · proforma emitida el {formatDate(data.proformaDate)}
      </p>
      <dl className="facts">
        <dt>Suplidor</dt>
        <dd>
          {data.issuerName} · RNC <span className="mono">{data.issuerRnc}</span>
        </dd>
        <dt>Cliente</dt>
        <dd data-testid="proforma-customer">
          {data.customerName} · RNC <span className="mono">{data.customerRnc}</span>
        </dd>
        {data.siteAddress ? (
          <>
            <dt>Obra</dt>
            <dd>{data.siteAddress}</dd>
          </>
        ) : null}
      </dl>
      <table>
        <thead>
          <tr>
            <th className="num">#</th>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Cantidad</th>
            <th className="num">Precio</th>
            <th className="num">Neto</th>
            <th className="num">ITBIS</th>
            <th className="num">Total</th>
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
            <th colSpan={5}>Totales</th>
            <td className="num">
              <Money value={data.netTotal} testId="proforma-net" />
            </td>
            <td className="num">
              <Money value={data.itbisTotal} testId="proforma-itbis" />
            </td>
            <td className="num">
              <Money value={data.total} testId="proforma-total" />
            </td>
          </tr>
        </tbody>
      </table>
      <p className="muted">Documento no fiscal. El ITBIS se calcula con las reglas vigentes a la fecha de la proforma.</p>
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
