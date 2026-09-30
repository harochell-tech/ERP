"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, Money, NoPermission } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { DELIVERY_TERMS, formatDate } from "@/lib/labels";
import { quoteStatusLabel } from "@/lib/quotes";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// QUO1-04 (E-QUO1-04-5): the quote the customer receives — issuer, customer, lines, the informative ITBIS of the SALES_ITBIS rule in
// force at the quote date (the server's, never stored), totals, validity, notes and the general conditions (provisional until X-Q1).
// The browser prints it (the menu and the buttons are hidden when printing); no PDF is generated.

function QuotePrint() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/quotes/{quoteId}/print", { path: { companyId, quoteId: id } }) : null,
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
        <Link href={`/ventas/cotizacion/?id=${id}`}>← Cotización {data.quoteNo}</Link>
        <button type="button" className="primary" onClick={() => window.print()}>
          Imprimir
        </button>
      </div>
      <h1>Cotización {data.quoteNo}</h1>
      <p>
        Emitida el {formatDate(data.quoteDate)} · <strong data-testid="print-valid-until">válida hasta el {formatDate(data.validUntil)}</strong>
        {data.status !== "SENT" || data.expired ? <span className="no-print muted"> · {quoteStatusLabel(data.status, data.expired)}</span> : null}
      </p>
      <dl className="facts">
        <dt>Suplidor</dt>
        <dd>
          {data.issuerName} · RNC <span className="mono">{data.issuerRnc}</span>
        </dd>
        <dt>Cliente</dt>
        <dd data-testid="print-customer">
          {data.customerName}
          {data.customerRnc ? (
            <>
              {" "}
              · RNC <span className="mono">{data.customerRnc}</span>
            </>
          ) : null}
        </dd>
        {data.customerRef ? (
          <>
            <dt>Su referencia</dt>
            <dd>{data.customerRef}</dd>
          </>
        ) : null}
        <dt>Entrega</dt>
        <dd>
          {DELIVERY_TERMS[data.deliveryTermCode] ?? data.deliveryTermCode}
          {data.siteAddress ? ` · obra: ${data.siteAddress}` : ""}
        </dd>
      </dl>
      <div className="table-wrap"><table>
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
              <Money value={data.netTotal} testId="print-net" />
            </td>
            <td className="num">
              <Money value={data.itbisTotal} testId="print-itbis" />
            </td>
            <td className="num">
              <Money value={data.total} testId="print-total" />
            </td>
          </tr>
        </tbody>
      </table></div>
      {data.notes ? (
        <p>
          <strong>Notas:</strong> {data.notes}
        </p>
      ) : null}
      <h2>Condiciones generales</h2>
      <p className="muted" data-testid="print-conditions">
        Precios en pesos dominicanos, sin ITBIS salvo indicación; el ITBIS mostrado es informativo, calculado con las reglas vigentes a la fecha de la
        cotización. Sujeto a disponibilidad. Condiciones definitivas pendientes (X-Q1). Documento no fiscal.
      </p>
      <div className="signature">
        <div>
          <div className="signature-line" />
          Por el suplidor
        </div>
        <div>
          <div className="signature-line" />
          Aceptación del cliente
        </div>
      </div>
    </div>
  );
}

export default function Page() {
  return (
    <Suspense>
      <QuotePrint />
    </Suspense>
  );
}
