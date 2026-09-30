"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { Field, Loading, Money, NoPermission } from "@/components/ui";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS2-08 / E-VS2-07-2: open AP of posted invoices per supplier in the TREASURY policy's buckets (the Controller approves them).
export default function Page() {
  const { companyId, can } = useSession();
  const [asOf, setAsOf] = useState(todayInDominicanRepublic());
  const { data, error } = useLoad(
    can("payment:read") ? () => query("/api/v1/companies/{companyId}/treasury/ap-aging", { path: { companyId }, query: { asOf } }) : null,
    [companyId, asOf],
  );
  if (!can("payment:read")) {
    return <NoPermission />;
  }
  const b = data?.buckets;
  return (
    <>
      <h1>Antigüedad de cuentas por pagar</h1>
      <div className="card">
        <Field label="Al">
          <input type="date" value={asOf} onChange={(e) => setAsOf(e.target.value)} />
        </Field>
      </div>
      {data === null || !b ? (
        <Loading error={error} />
      ) : data.suppliers.length === 0 ? (
        <p className="muted" data-testid="ap-aging-empty">
          No hay facturas contabilizadas con saldo pendiente al {formatDate(asOf)}: no se le debe nada a ningún proveedor. Las facturas aparecen
          aquí cuando se contabilizan en <Link href="/cxp/facturas/">Facturas de proveedor</Link>
          {can("payment:prepare") ? (
            <>
              ; para pagar lo que vence, use la <Link href="/tesoreria/propuesta/">Propuesta de pagos</Link>
            </>
          ) : null}
          .
        </p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Proveedor</th>
              <th className="num">Por vencer (RD$)</th>
              <th className="num">1–{b.bucket1Days} días (RD$)</th>
              <th className="num">
                {b.bucket1Days + 1}–{b.bucket2Days} días (RD$)
              </th>
              <th className="num">
                {b.bucket2Days + 1}–{b.bucket3Days} días (RD$)
              </th>
              <th className="num">Más de {b.bucket3Days} (RD$)</th>
              <th className="num">Total (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {data.suppliers.map((s) => (
              <tr key={s.supplierId}>
                <td className="wrap">{s.supplierName}</td>
                <td className="num">
                  <Money value={s.current} />
                </td>
                <td className="num">
                  <Money value={s.bucket1} />
                </td>
                <td className="num">
                  <Money value={s.bucket2} />
                </td>
                <td className="num">
                  <Money value={s.bucket3} />
                </td>
                <td className="num">
                  <Money value={s.over} />
                </td>
                <td className="num">
                  <strong>
                    <Money value={s.total} />
                  </strong>
                </td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            {/* C-24 (E-UX4-2): each bucket's total is the server's (bucketTotals). */}
            <tr data-testid="ap-aging-totals">
              <td>
                <strong>Total</strong>
              </td>
              {(["current", "bucket1", "bucket2", "bucket3", "over"] as const).map((k) => (
                <td key={k} className="num">
                  <strong>
                    <Money value={data.bucketTotals[k]} />
                  </strong>
                </td>
              ))}
              <td className="num">
                <strong>
                  <Money value={data.bucketTotals.total} testId="ap-aging-total" />
                </strong>
              </td>
            </tr>
          </tfoot>
        </table></div>
      )}
    </>
  );
}
