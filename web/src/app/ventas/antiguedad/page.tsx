"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { Field, Loading, Money, NoPermission } from "@/components/ui";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { csvUrl } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS3-10b (E-VS3-09-1/2/4): AR aging by the CREDIT policy buckets; unapplied receipts shown apart, in the customer's favour.

export default function Page() {
  const { companyId, can } = useSession();
  const [asOf, setAsOf] = useState(todayInDominicanRepublic());
  const { data, error } = useLoad(
    can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/ar-aging", { path: { companyId }, query: { asOf } }) : null,
    [companyId, asOf],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  const b = data?.buckets;
  return (
    <>
      <h1>Antigüedad de cuentas por cobrar</h1>
      <div className="inline-form">
        <Field label="Al">
          <input type="date" value={asOf} onChange={(e) => setAsOf(e.target.value)} />
        </Field>
        <a className="button" href={csvUrl("/api/v1/companies/{companyId}/sales/ar-aging", { path: { companyId }, query: { asOf } })}>
          Descargar CSV
        </a>
      </div>
      {data === null || !b ? (
        <Loading error={error} />
      ) : data.customers.length === 0 ? (
        <p className="muted">No hay saldos abiertos.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Cliente</th>
              <th className="num">Al día</th>
              <th className="num">1–{b.bucket1Days}</th>
              <th className="num">
                {b.bucket1Days + 1}–{b.bucket2Days}
              </th>
              <th className="num">
                {b.bucket2Days + 1}–{b.bucket3Days}
              </th>
              <th className="num">Más de {b.bucket3Days}</th>
              <th className="num">Total</th>
              <th className="num">A favor</th>
              <th className="num">Neto</th>
            </tr>
          </thead>
          <tbody>
            {data.customers.map((c) => (
              <tr key={c.customerId}>
                <td>
                  {c.customerName}
                  <div className="muted">{c.documents.map((d) => `${d.invoiceNo} vence ${formatDate(d.dueDate)}`).join(" · ")}</div>
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
              </tr>
            ))}
            <tr>
              <th colSpan={6}>Totales</th>
              <td className="num">
                <Money value={data.total} />
              </td>
              <td className="num">
                <Money value={data.unapplied} />
              </td>
              <td className="num">
                <Money value={data.net} testId="aging-net" />
              </td>
            </tr>
          </tbody>
        </table>
      )}
    </>
  );
}
