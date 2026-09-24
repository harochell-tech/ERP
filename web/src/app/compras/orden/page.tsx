"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { ErrorBox, Loading, NoPermission, ReasonAction } from "@/components/ui";
import { formatDecimal, formatQuantity } from "@/lib/decimal";
import { formatDate, formatDateTime, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Order = Schemas["PurchaseOrderDetail"];

function Actions({ order, onDone }: { order: Order; onDone: () => void }) {
  const { can } = useSession();
  const id = order.purchaseOrderId;
  const target = { plantId: order.plantId, purchaseOrderId: id, expectedVersion: order.version };
  const submit = useCommand(`submit-po:${id}`, "/api/v1/companies/{companyId}/procurement/submit-purchase-order");
  const approve = useCommand(`approve-po:${id}`, "/api/v1/companies/{companyId}/procurement/approve-purchase-order");
  const reject = useCommand(`reject-po:${id}`, "/api/v1/companies/{companyId}/procurement/reject-purchase-order");
  const cancel = useCommand(`cancel-po:${id}`, "/api/v1/companies/{companyId}/procurement/cancel-purchase-order");
  const busy = submit.busy || approve.busy || reject.busy || cancel.busy;
  const after = (response: unknown) => {
    if (response) {
      onDone();
    }
  };
  const receivable = order.status === "APPROVED" || order.status === "PARTIALLY_RECEIVED";

  return (
    <>
      <div className="actions">
        {order.status === "DRAFT" && can("purchase_order:submit") ? (
          <button type="button" disabled={busy} onClick={async () => after(await submit.run(target))}>
            Enviar a aprobación
          </button>
        ) : null}
        {order.status === "PENDING_APPROVAL" && can("purchase_order:approve") ? (
          <>
            <button type="button" disabled={busy} onClick={async () => after(await approve.run(target))}>
              Aprobar
            </button>
            <ReasonAction label="Rechazar" busy={busy} onConfirm={async (reason) => after(await reject.run({ ...target, reason }))} />
          </>
        ) : null}
        {(order.status === "DRAFT" || order.status === "PENDING_APPROVAL" || order.status === "APPROVED") && can("purchase_order:cancel") ? (
          <ReasonAction label="Cancelar orden" busy={busy} onConfirm={async (reason) => after(await cancel.run({ ...target, reason }))} />
        ) : null}
        {receivable && can("goods_receipt:post") ? (
          <Link className="button" href={`/almacen/recibir/?oc=${id}`}>
            Recibir material
          </Link>
        ) : null}
      </div>
      <ErrorBox error={submit.error ?? approve.error ?? reject.error ?? cancel.error} />
    </>
  );
}

function OrderDetail() {
  const { companyId, can, plantFor } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const plantId = plantFor("purchase_order:read");
  const { data: order, error, reload } = useLoad(
    can("purchase_order:read") && id
      ? () => query("/api/v1/companies/{companyId}/procurement/purchase-orders/{purchaseOrderId}", { path: { companyId, purchaseOrderId: id }, query: { plantId } })
      : null,
    [companyId, id, plantId],
  );

  if (!can("purchase_order:read")) {
    return <NoPermission />;
  }
  if (order === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <h1>Orden de compra {order.poNo}</h1>
      <dl className="facts">
        <dt>Estado</dt>
        <dd data-testid="po-status">{statusLabel(order.status)}</dd>
        <dt>Proveedor</dt>
        <dd>{order.supplierName}</dd>
        <dt>Planta</dt>
        <dd>{order.plantCode}</dd>
        <dt>Fecha</dt>
        <dd>{formatDate(order.orderDate)}</dd>
        <dt>Creada por</dt>
        <dd>{order.createdBy ?? "—"}</dd>
        <dt>Aprobada por</dt>
        <dd>{order.approvedBy ? `${order.approvedBy} (${formatDateTime(order.approvedAt)})` : "—"}</dd>
      </dl>
      <Actions order={order} onDone={reload} />
      <h2>Líneas</h2>
      <table>
        <thead>
          <tr>
            <th>#</th>
            <th>Artículo</th>
            <th>Unidad</th>
            <th className="num">Pedido</th>
            <th className="num">Precio</th>
            <th className="num">Recibido</th>
            <th className="num">Facturado</th>
          </tr>
        </thead>
        <tbody>
          {order.lines.map((l) => (
            <tr key={l.poLineId}>
              <td>{l.lineNo}</td>
              <td>
                {l.itemCode} — {l.itemDescription}
              </td>
              <td>{l.uom}</td>
              <td className="num">{formatQuantity(l.qtyOrdered)}</td>
              <td className="num">{formatDecimal(l.unitPrice)}</td>
              <td className="num" data-testid="qty-received">
                {formatQuantity(l.qtyReceived)}
              </td>
              <td className="num">{formatQuantity(l.qtyInvoiced)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <h2>Recepciones</h2>
      {order.goodsReceipts.length === 0 ? (
        <p className="muted">Sin recepciones.</p>
      ) : (
        <ul>
          {order.goodsReceipts.map((gr) => (
            <li key={gr.goodsReceiptId}>
              {can("goods_receipt:read") ? <Link href={`/almacen/recepcion/?id=${gr.goodsReceiptId}`}>{gr.grNo}</Link> : gr.grNo} — {formatDateTime(gr.occurredAt)} —{" "}
              {statusLabel(gr.documentStatus)}
            </li>
          ))}
        </ul>
      )}
      <History history={order.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <OrderDetail />
    </Suspense>
  );
}
