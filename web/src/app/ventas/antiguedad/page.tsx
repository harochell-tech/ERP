"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { Field, Money, NoPermission } from "@/components/ui";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { csvUrl } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { filterAging } from "@/lib/ux4bSales";

// VS3-10b (E-VS3-09-1/2/4): AR aging by the CREDIT policy buckets; unapplied receipts shown apart, in the customer's favour.
// UX4-03 (V-18, E-UX4-2): the totals per bucket are the server's (bucketTotals); each invoice links to its page; the customers
// can be filtered by name and to those with something past due (a filter of the rows shown: the totals stay the company's).

export default function Page() {
  const { companyId, can } = useSession();
  const [asOf, setAsOf] = useState(todayInDominicanRepublic());
  const [text, setText] = useState("");
  const [overdueOnly, setOverdueOnly] = useState(false);
  const { data, error } = useLoad(
    can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/ar-aging", { path: { companyId }, query: { asOf } }) : null,
    [companyId, asOf],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  const b = data?.buckets;
  const customers = data ? filterAging(data.customers, text, overdueOnly) : [];
  const filtered = text.trim() !== "" || overdueOnly;
  return (
    <>
      <h1>Cuentas por cobrar por antigüedad</h1>
      <p className="muted">Lo que cada cliente debe, según cuántos días lleva vencida cada factura a la fecha elegida.</p>
      <div className="inline-form">
        <Field label="Al">
          <input type="date" value={asOf} onChange={(e) => setAsOf(e.target.value)} />
        </Field>
        <Field label="Buscar cliente">
          <input aria-label="Buscar cliente" value={text} onChange={(e) => setText(e.target.value)} />
        </Field>
        <label className="field">
          <span>Solo con saldo vencido</span>
          <input type="checkbox" aria-label="Solo con saldo vencido" checked={overdueOnly} onChange={(e) => setOverdueOnly(e.target.checked)} />
        </label>
        <a className="button" href={csvUrl("/api/v1/companies/{companyId}/sales/ar-aging", { path: { companyId }, query: { asOf } })}>
          Descargar CSV
        </a>
      </div>
      {data === null || !b ? (
        <LoadingIndicator error={error} />
      ) : data.customers.length === 0 ? (
        <EmptyState title="Ningún cliente tiene saldo pendiente a esa fecha." />
      ) : (
        <>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Cliente</th>
                  <th className="num">Al día (RD$)</th>
                  <th className="num">1–{b.bucket1Days} días</th>
                  <th className="num">
                    {b.bucket1Days + 1}–{b.bucket2Days} días
                  </th>
                  <th className="num">
                    {b.bucket2Days + 1}–{b.bucket3Days} días
                  </th>
                  <th className="num">Más de {b.bucket3Days} días</th>
                  <th className="num">Total (RD$)</th>
                  <th className="num">A favor (RD$)</th>
                  <th className="num">Neto (RD$)</th>
                  {/* FIS1b-07 (E-FIS1b-9): delivered under proforma and not yet invoiced — receivable, but outside the fiscal AR. */}
                  <th className="num">En proforma (RD$)</th>
                  <th className="num">Depósito ITBIS (RD$)</th>
                </tr>
              </thead>
              <tbody>
                {customers.map((c) => (
                  <tr key={c.customerId}>
                    <td className="wrap">
                      <Link href={`/ventas/estado-de-cuenta/?cliente=${c.customerId}`}>{c.customerName}</Link>
                      <div className="muted">
                        {c.documents.map((d, i) => (
                          <span key={d.arDocId}>
                            {i > 0 ? " · " : ""}
                            <Link href={`/facturacion/factura/?id=${d.invoiceId}`}>{d.invoiceNo}</Link> vence {formatDate(d.dueDate)}
                            {d.daysOverdue > 0 ? ` (${d.daysOverdue} días vencida)` : ""}
                          </span>
                        ))}
                        {c.proformaDocuments.map((f, i) => (
                          <span key={f.proformaId}>
                            {i > 0 || c.documents.length > 0 ? " · " : ""}
                            <Link href={`/facturacion/proforma/?id=${f.proformaId}`}>{f.proformaNo}</Link> vence {formatDate(f.dueDate)}
                            {f.daysOverdue > 0 ? ` (${f.daysOverdue} días vencida)` : ""}
                          </span>
                        ))}
                      </div>
                    </td>
                    <td className="num">
                      <Money value={c.current} />
                    </td>
                    <td className="num">
                      <Money value={c.bucket1} />
                    </td>
                    <td className="num">
                      <Money value={c.bucket2} />
                    </td>
                    <td className="num">
                      <Money value={c.bucket3} />
                    </td>
                    <td className="num">
                      <Money value={c.over} />
                    </td>
                    <td className="num">
                      <Money value={c.total} />
                    </td>
                    <td className="num">
                      <Money value={c.unapplied} />
                    </td>
                    <td className="num">
                      <Money value={c.net} />
                    </td>
                    <td className="num">
                      <Money value={c.proformas} />
                    </td>
                    <td className="num">
                      <Money value={c.deposits} />
                    </td>
                  </tr>
                ))}
                {customers.length === 0 ? (
                  <tr>
                    <td colSpan={11} className="muted">
                      Ningún cliente coincide con el filtro.
                    </td>
                  </tr>
                ) : null}
              </tbody>
              <tfoot>
                <tr>
                  <th>Totales de la empresa{filtered ? " (sin filtro)" : ""}</th>
                  <td className="num">
                    <Money value={data.bucketTotals.current} testId="aging-total-current" />
                  </td>
                  <td className="num">
                    <Money value={data.bucketTotals.bucket1} testId="aging-total-bucket1" />
                  </td>
                  <td className="num">
                    <Money value={data.bucketTotals.bucket2} testId="aging-total-bucket2" />
                  </td>
                  <td className="num">
                    <Money value={data.bucketTotals.bucket3} testId="aging-total-bucket3" />
                  </td>
                  <td className="num">
                    <Money value={data.bucketTotals.over} testId="aging-total-over" />
                  </td>
                  <td className="num">
                    <Money value={data.bucketTotals.total} testId="aging-total" />
                  </td>
                  <td className="num">
                    <Money value={data.unapplied} />
                  </td>
                  <td className="num">
                    <Money value={data.net} testId="aging-net" />
                  </td>
                  <td className="num">
                    <Money value={data.proformas} testId="aging-proformas" />
                  </td>
                  <td className="num">
                    <Money value={data.deposits} testId="aging-deposits" />
                  </td>
                </tr>
              </tfoot>
            </table>
          </div>
        </>
      )}
    </>
  );
}
