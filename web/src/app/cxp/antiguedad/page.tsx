"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { Field, Loading, Money, NoPermission } from "@/components/ui";
import { todayInDominicanRepublic } from "@/lib/labels";
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
        <p className="muted">No hay facturas con saldo abierto.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Proveedor</th>
              <th className="num">Por vencer</th>
              <th className="num">1–{b.bucket1Days} días</th>
              <th className="num">
                {b.bucket1Days + 1}–{b.bucket2Days} días
              </th>
              <th className="num">
                {b.bucket2Days + 1}–{b.bucket3Days} días
              </th>
              <th className="num">Más de {b.bucket3Days}</th>
              <th className="num">Total</th>
            </tr>
          </thead>
          <tbody>
            {data.suppliers.map((s) => (
              <tr key={s.supplierId}>
                <td>{s.supplierName}</td>
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
            <tr>
              <td>
                <strong>Total</strong>
              </td>
              <td colSpan={5}></td>
              <td className="num">
                <strong>
                  <Money value={data.total} />
                </strong>
              </td>
            </tr>
          </tbody>
        </table>
      )}
    </>
  );
}
