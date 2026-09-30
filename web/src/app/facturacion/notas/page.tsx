"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS3-10b: commercial credit notes NC-… (sales:read); they are created from the invoice's page.

export default function Page() {
  const { companyId, can } = useSession();
  const { data, error } = useLoad(can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/credit-notes", { path: { companyId }, query: { limit: 200 } }) : null, [companyId]);
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Notas de crédito</h1>
      <p className="muted">Se crean desde el detalle de la factura.</p>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay notas de crédito.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Número</th>
              <th>e-NCF</th>
              <th>Factura</th>
              <th>Cliente</th>
              <th>Fecha</th>
              <th className="num">Total (RD$)</th>
              <th>Estado</th>
              <th>Fiscal</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((n) => (
              <tr key={n.creditNoteId}>
                <td className="mono">
                  <Link href={`/facturacion/nota/?id=${n.creditNoteId}`}>{n.creditNoteNo}</Link>
                </td>
                <td className="mono">{n.encf ?? "—"}</td>
                <td className="mono">
                  <Link href={`/facturacion/factura/?id=${n.invoiceId}`}>{n.invoiceNo}</Link>
                </td>
                <td className="wrap">{n.customerName}</td>
                <td>{formatDate(n.creditDate)}</td>
                <td className="num">
                  <Money value={n.total} />
                </td>
                <td>
                  <StatusBadge status={n.commercialStatus} />
                </td>
                <td>
                  <StatusBadge status={n.fiscalStatus} />
                </td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
