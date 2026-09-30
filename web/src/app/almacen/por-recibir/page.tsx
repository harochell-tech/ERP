"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { canReceiveMore } from "@/lib/receiving";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

/**
 * UX3-02 (E-UX3-5): Almacén › Por recibir — the approved and partially received orders of the plant, oldest first, with what is
 * pending and the most each line may still take (the server's PostGoodsReceipt rule).
 */
export default function ToReceive() {
  const { companyId, can, plantFor, plantName } = useSession();
  const allowed = can("purchase_order:read");
  const plantId = plantFor("purchase_order:read");
  const { data, error } = useLoad(
    allowed ? () => query("/api/v1/companies/{companyId}/procurement/purchase-orders/to-receive", { path: { companyId }, query: { plantId, limit: 200 } }) : null,
    [companyId, plantId, allowed],
  );

  if (!allowed) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Por recibir</h1>
      <p className="muted">Órdenes aprobadas o recibidas en parte, de la más antigua a la más reciente.</p>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay órdenes por recibir.</p>
      ) : (
        data.items.map((order) => (
          <section key={order.purchaseOrderId} className="card" data-testid={`to-receive-${order.poNo}`} aria-label={`Orden ${order.poNo}`}>
            <h2 style={{ marginTop: 0 }}>
              <Link href={`/compras/orden/?id=${order.purchaseOrderId}`}>{order.poNo}</Link> · {order.supplierName} <StatusBadge status={order.status} />
            </h2>
            <p className="muted">
              Planta {plantName(order.plantId, order.plantCode)} · pedida el {formatDate(order.orderDate)} · aprobada {formatDateTime(order.approvedAt)}
            </p>
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Artículo</th>
                    <th>Unidad</th>
                    <th className="num">Pedido</th>
                    <th className="num">Recibido</th>
                    <th className="num">Pendiente</th>
                    <th className="num">Máximo permitido</th>
                  </tr>
                </thead>
                <tbody>
                  {order.lines.map((l) => (
                    <tr key={l.poLineId}>
                      <td className="wrap">
                        {l.itemCode} <span className="muted">{l.itemDescription}</span>
                      </td>
                      <td>{l.uom}</td>
                      <td className="num">{formatQuantity(l.qtyOrdered)}</td>
                      <td className="num">{formatQuantity(l.qtyReceived)}</td>
                      <td className="num">{formatQuantity(l.openQuantity)}</td>
                      <td className="num">{formatQuantity(l.maxReceivable)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {can("goods_receipt:post") && canReceiveMore(order.lines) ? (
              <Link className="button primary" href={`/almacen/recibir/?oc=${order.purchaseOrderId}`}>
                Recibir
              </Link>
            ) : null}
          </section>
        ))
      )}
    </>
  );
}
