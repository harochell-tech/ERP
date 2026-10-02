"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { Money, NoPermission, StatusBadge } from "@/components/ui";
import { DELIVERY_TERMS, formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// CF1-05 (E-CF1-05-1): the cash sales to the final consumer, newest first, with who bought and what must be paid.

export default function Page() {
  const { companyId, can, plantName } = useSession();
  const { data, error } = useLoad(
    can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/orders", { path: { companyId }, query: { cashSale: "true", limit: 200 } }) : null,
    [companyId],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  return (
    <>
      <h1>Ventas de contado</h1>
      <p className="muted">Ventas a consumidor final: se cobran completas antes de despachar y cada entrega se factura con un e-CF de consumo (E32).</p>
      {can("cash_sale:create") ? (
        <p className="actions">
          <Link className="button primary" href="/ventas/contado/nueva/">
            Nueva venta de contado
          </Link>
        </p>
      ) : null}
      {data.items.length === 0 ? (
        <EmptyState title="Todavía no hay ventas de contado." steps={[can("cash_sale:create") && { href: "/ventas/contado/nueva/", label: "Hacer la primera venta de contado" }]} />
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Número</th>
              <th>Fecha</th>
              <th>Comprador</th>
              <th>Planta</th>
              <th>Entrega</th>
              <th className="num">Total a pagar (RD$)</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((o) => (
              <tr key={o.salesOrderId}>
                <td className="mono">
                  <Link href={`/ventas/venta-contado/?id=${o.salesOrderId}`}>{o.orderNo}</Link>
                </td>
                <td>{formatDate(o.orderDate)}</td>
                <td className="wrap">{o.buyerName ?? "—"}</td>
                <td>{plantName(o.plantCode)}</td>
                <td>{DELIVERY_TERMS[o.deliveryTermCode] ?? o.deliveryTermCode}</td>
                <td className="num">{o.paymentTotal ? <Money value={o.paymentTotal} /> : <span className="muted">al enviar a pago</span>}</td>
                <td>
                  <StatusBadge status={o.status} />
                </td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
