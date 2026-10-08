"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { NoPermission, StatusBadge } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { DELIVERY_TERMS, formatDate, formatDateTime, todayInDominicanRepublic } from "@/lib/labels";
import { BOARD_COLUMNS } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { isZeroDecimal } from "@/lib/ux4bSales";

// VS3-10a (E-VS3-10-5): the day's dispatch work — orders waiting for a delivery, then deliveries by status, each opening its page
// with the step it needs.
// UX4-03 (V-26): each order shows the date the customer asked for and, per product, what was ordered and what was delivered (the
// server's figures, never subtracted); the delivery statuses without deliveries are named once, not one "Ninguno." each.

export default function Page() {
  const { companyId, can, plantName } = useSession();
  const { data, error } = useLoad(
    can("sales:read")
      ? async () => {
          // CF1-05 (E-CF1-05-14): a cash sale paid with money that counts is planned without waiting for «Verificar pago».
          const [driverToday, paidCash, confirmed, partial, ...columns] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/deliveries", { path: { companyId }, query: { driverConfirmedOn: todayInDominicanRepublic(), limit: 200 } }),
            query("/api/v1/companies/{companyId}/sales/orders", { path: { companyId }, query: { status: "PENDING_PAYMENT", cashSale: "true", limit: 200 } }),
            query("/api/v1/companies/{companyId}/sales/orders", { path: { companyId }, query: { status: "CONFIRMED", limit: 200 } }),
            query("/api/v1/companies/{companyId}/sales/orders", { path: { companyId }, query: { status: "PARTIALLY_DELIVERED", limit: 200 } }),
            ...BOARD_COLUMNS.map((c) => query("/api/v1/companies/{companyId}/sales/deliveries", { path: { companyId }, query: { status: c.status, limit: 200 } })),
          ]);
          const orders = [...confirmed.items, ...partial.items, ...paidCash.items];
          const details = await Promise.all(orders.map((o) => query("/api/v1/companies/{companyId}/sales/orders/{salesOrderId}", { path: { companyId, salesOrderId: o.salesOrderId } })));
          return {
            orders: orders.map((o, i) => ({ ...o, detail: details[i] })).filter((o) => o.status !== "PENDING_PAYMENT" || o.detail?.cashSale?.covered === true),
            columns: columns.map((c) => c.items),
            driverToday: driverToday.items,
          };
        }
      : null,
    [companyId],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const empty = BOARD_COLUMNS.filter((_, index) => (data.columns[index] ?? []).length === 0).map((c) => c.title);
  return (
    <>
      <h1>Tablero de despacho</h1>
      <h2>Pedidos por despachar ({data.orders.length})</h2>
      {data.orders.length === 0 ? (
        <EmptyState title="No hay pedidos confirmados pendientes de despacho.">
          <p>Un pedido aparece aquí cuando Crédito lo confirma.</p>
        </EmptyState>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Pedido</th>
                <th>Fecha del pedido</th>
                <th>Solicitado para</th>
                <th>Cliente</th>
                <th>Entrega</th>
                <th>Pedido / entregado</th>
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
                  <td data-testid={`requested:${o.orderNo}`}>{o.detail?.requestedDate ? formatDate(o.detail.requestedDate) : <span className="muted">Sin fecha</span>}</td>
                  <td className="wrap">{o.customerName}</td>
                  <td>{DELIVERY_TERMS[o.deliveryTermCode] ?? o.deliveryTermCode}</td>
                  <td className="wrap">
                    {(o.detail?.lines ?? []).map((l) => (
                      <div key={l.salesOrderLineId}>
                        {l.itemCode}: {formatQuantity(l.qtyOrdered)} {l.uom}
                        {isZeroDecimal(l.qtyDelivered) ? " · nada entregado" : ` · ${formatQuantity(l.qtyDelivered)} entregado`}
                      </div>
                    ))}
                  </td>
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
          </table>
        </div>
      )}
      {BOARD_COLUMNS.map((column, index) => {
        const items = data.columns[index] ?? [];
        return items.length === 0 ? null : (
          <section key={column.status}>
            <h2>
              {column.title} ({items.length})
            </h2>
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Conduce</th>
                    <th>Pedido</th>
                    <th>Cliente</th>
                    <th>Planta</th>
                    <th>Entrega</th>
                    <th>Ficha</th>
                    <th>Chofer</th>
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
                      <td className="mono">{d.fleetCode ?? "—"}</td>
                      <td className="wrap">{d.driverName ?? "—"}</td>
                      <td>{d.gateOutAt ? formatDateTime(d.gateOutAt) : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        );
      })}
      {data.driverToday.length > 0 ? (
        <section data-testid="board-driver-today">
          <h2>Confirmadas por el chofer hoy ({data.driverToday.length})</h2>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Conduce</th>
                  <th>Cliente</th>
                  <th>Chofer</th>
                  <th>Recibió</th>
                  <th>Hora</th>
                  <th>Resultado</th>
                </tr>
              </thead>
              <tbody>
                {data.driverToday.map((d) => (
                  <tr key={d.deliveryId}>
                    <td className="mono">
                      <Link href={`/despacho/conduce/?id=${d.deliveryId}`}>{d.deliveryNo}</Link>
                    </td>
                    <td className="wrap">{d.customerName}</td>
                    <td className="wrap">{d.driverName ?? "—"}</td>
                    <td className="wrap">{d.driverReceiver ?? "—"}</td>
                    <td>{d.driverConfirmedAt ? formatDateTime(d.driverConfirmedAt) : "—"}</td>
                    <td>{d.driverOutcome === "DIFFERENCES" ? (d.status === "IN_TRANSIT" ? "Con diferencias: falta completar" : "Con diferencias") : "Completa"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : null}
      {empty.length > 0 ? (
        <p className="muted" data-testid="board-empty-columns">
          Sin conduces {empty.length === BOARD_COLUMNS.length ? "en curso" : `en: ${empty.join(", ").toLowerCase()}`}.
        </p>
      ) : null}
    </>
  );
}
