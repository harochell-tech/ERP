"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { Loading, NoPermission, StatusBadge } from "@/components/ui";
import { DELIVERY_TERMS, formatDate, formatDateTime } from "@/lib/labels";
import { BOARD_COLUMNS } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-10-5): the day's dispatch work — orders waiting for a delivery, then deliveries by status, each opening its page
// with the step it needs.

export default function Page() {
  const { companyId, can, plantName } = useSession();
  const { data, error } = useLoad(
    can("sales:read")
      ? async () => {
          const [confirmed, partial, ...columns] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/orders", { path: { companyId }, query: { status: "CONFIRMED", limit: 200 } }),
            query("/api/v1/companies/{companyId}/sales/orders", { path: { companyId }, query: { status: "PARTIALLY_DELIVERED", limit: 200 } }),
            ...BOARD_COLUMNS.map((c) => query("/api/v1/companies/{companyId}/sales/deliveries", { path: { companyId }, query: { status: c.status, limit: 200 } })),
          ]);
          return { orders: [...confirmed.items, ...partial.items], columns: columns.map((c) => c.items) };
        }
      : null,
    [companyId],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <h1>Tablero de despacho</h1>
      <h2>Pedidos por despachar</h2>
      {data.orders.length === 0 ? (
        <p className="muted">No hay pedidos confirmados pendientes.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Pedido</th>
              <th>Fecha</th>
              <th>Cliente</th>
              <th>Entrega</th>
              <th>Estado</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.orders.map((o) => (
              <tr key={o.salesOrderId}>
                <td className="mono">
                  <Link href={`/ventas/pedido/?id=${o.salesOrderId}`}>{o.orderNo}</Link>
                </td>
                <td>{formatDate(o.orderDate)}</td>
                <td className="wrap">{o.customerName}</td>
                <td>{DELIVERY_TERMS[o.deliveryTermCode] ?? o.deliveryTermCode}</td>
                <td>
                  <StatusBadge status={o.status} />
                </td>
                <td className="actions">
                  {can("delivery:manage") ? (
                    <Link className="button" href={`/despacho/planificar/?pedido=${o.salesOrderId}`}>
                      Planificar conduce
                    </Link>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
      {BOARD_COLUMNS.map((column, index) => {
        const items = data.columns[index] ?? [];
        return (
          <section key={column.status}>
            <h2>
              {column.title} ({items.length})
            </h2>
            {items.length === 0 ? (
              <p className="muted">Ninguno.</p>
            ) : (
              <div className="table-wrap"><table>
                <thead>
                  <tr>
                    <th>Conduce</th>
                    <th>Pedido</th>
                    <th>Cliente</th>
                    <th>Planta</th>
                    <th>Entrega</th>
                    <th>Salida</th>
                  </tr>
                </thead>
                <tbody>
                  {items.map((d) => (
                    <tr key={d.deliveryId}>
                      <td className="mono">
                        <Link href={`/despacho/conduce/?id=${d.deliveryId}`}>{d.deliveryNo}</Link>
                      </td>
                      <td className="mono">{d.orderNo}</td>
                      <td className="wrap">{d.customerName}</td>
                      <td>{plantName(d.plantCode)}</td>
                      <td>{DELIVERY_TERMS[d.deliveryTermCode] ?? d.deliveryTermCode}</td>
                      <td>{d.gateOutAt ? formatDateTime(d.gateOutAt) : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table></div>
            )}
          </section>
        );
      })}
    </>
  );
}
