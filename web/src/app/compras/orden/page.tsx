"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { ErrorBox, Loading, Money, NoPermission, ReasonAction } from "@/components/ui";
import { formatDecimal, formatQuantity } from "@/lib/decimal";
import { formatDate, formatDateTime, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { itemLabel, uomLabel } from "@/lib/ux4a-compras";

type Order = Schemas["PurchaseOrderDetail"];

function Actions({ order, onDone }: { order: Order; onDone: () => void }) {
  const { can } = useSession();
  const id = order.purchaseOrderId;
  const target = { plantId: order.plantId, purchaseOrderId: id, expectedVersion: order.version };
  const submit = useCommand(`submit-po:${id}`, "/api/v1/companies/{companyId}/procurement/submit-purchase-order", `Orden de compra ${order.poNo} enviada a aprobación.`);
  const approve = useCommand(`approve-po:${id}`, "/api/v1/companies/{companyId}/procurement/approve-purchase-order", `Orden de compra ${order.poNo} aprobada.`);
  const reject = useCommand(`reject-po:${id}`, "/api/v1/companies/{companyId}/procurement/reject-purchase-order", `Orden de compra ${order.poNo} rechazada: vuelve a borrador.`);
  const cancel = useCommand(`cancel-po:${id}`, "/api/v1/companies/{companyId}/procurement/cancel-purchase-order", `Orden de compra ${order.poNo} cancelada.`);
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
            <ReasonAction label="Rechazar" consequence="La orden vuelve a borrador para que el comprador la corrija." busy={busy} onConfirm={async (reason) => after(await reject.run({ ...target, reason }))} />
          </>
        ) : null}
        {(order.status === "DRAFT" || order.status === "PENDING_APPROVAL" || order.status === "APPROVED") && can("purchase_order:cancel") ? (
          <ReasonAction label="Cancelar orden" consequence="La orden queda cancelada y ya no se podrá recibir ni facturar. No se puede deshacer." busy={busy} onConfirm={async (reason) => after(await cancel.run({ ...target, reason }))} />
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

/** C-13: the order's receipts as a table, one row per received line with its quantity (each receipt's detail, goods_receipt:read). */
function Receipts({ order }: { order: Order }) {
  const { companyId, can, plantFor } = useSession();
  const detailed = can("goods_receipt:read");
  const ids = order.goodsReceipts.map((gr) => gr.goodsReceiptId).join(",");
  const { data } = useLoad(
    detailed && order.goodsReceipts.length > 0
      ? () =>
          Promise.all(
            order.goodsReceipts.map((gr) =>
              query("/api/v1/companies/{companyId}/procurement/goods-receipts/{goodsReceiptId}", {
                path: { companyId, goodsReceiptId: gr.goodsReceiptId },
                query: { plantId: plantFor("goods_receipt:read") },
              }),
            ),
          )
      : null,
    [companyId, ids, detailed],
  );
  if (order.goodsReceipts.length === 0) {
    return <p className="muted">Sin recepciones todavía.</p>;
  }
  const byId = new Map((data ?? []).map((r) => [r.goodsReceiptId, r]));
  return (
    <div className="table-wrap">
      <table data-testid="po-receipts">
        <thead>
          <tr>
            <th>Recepción</th>
            <th>Fecha y hora</th>
            <th>Ubicación</th>
            <th>Artículo</th>
            <th className="num">Cantidad</th>
            <th>Estado</th>
          </tr>
        </thead>
        <tbody>
          {order.goodsReceipts.flatMap((gr) => {
            const detail = byId.get(gr.goodsReceiptId);
            const number = detailed ? <Link href={`/almacen/recepcion/?id=${gr.goodsReceiptId}`}>{gr.grNo}</Link> : gr.grNo;
            if (!detail || detail.lines.length === 0) {
              return [
                <tr key={gr.goodsReceiptId}>
                  <td>{number}</td>
                  <td>{formatDateTime(gr.occurredAt)}</td>
                  <td>—</td>
                  <td>—</td>
                  <td className="num">—</td>
                  <td>{statusLabel(gr.documentStatus)}</td>
                </tr>,
              ];
            }
            return detail.lines.map((l) => (
              <tr key={l.grLineId}>
                <td>{number}</td>
                <td>{formatDateTime(gr.occurredAt)}</td>
                <td>{detail.locationCode}</td>
                <td>{l.itemCode}</td>
                <td className="num">{formatQuantity(l.qty)}</td>
                <td>{statusLabel(gr.documentStatus)}</td>
              </tr>
            ));
          })}
        </tbody>
      </table>
    </div>
  );
}

function OrderDetail() {
  const { companyId, can, plantFor, plantName } = useSession();
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
        <dd>{plantName(order.plantId, order.plantCode)}</dd>
        <dt>Fecha</dt>
        <dd>{formatDate(order.orderDate)}</dd>
        <dt>Creada por</dt>
        <dd>{order.createdBy ?? "—"}</dd>
        <dt>Aprobada por</dt>
        <dd>{order.approvedBy ? `${order.approvedBy} (${formatDateTime(order.approvedAt)})` : order.status === "PENDING_APPROVAL" ? "Pendiente" : "—"}</dd>
        <dt>Total (sin ITBIS)</dt>
        <dd>
          <Money value={order.total} currency testId="po-detail-total" />
        </dd>
      </dl>
      {order.status === "DRAFT" ? (
        <p className="notice" data-testid="po-not-sent">
          Aún no enviada a aprobación: nadie la puede aprobar hasta que se envíe
          {can("purchase_order:submit") ? " con «Enviar a aprobación»." : "."}
        </p>
      ) : null}
      <Actions order={order} onDone={reload} />
      <h2>Líneas</h2>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>#</th>
              <th>Artículo</th>
              <th>Unidad</th>
              <th className="num">Pedido</th>
              <th className="num">Precio (RD$)</th>
              <th className="num">Neto (RD$)</th>
              <th className="num">Recibido</th>
              <th className="num">Pendiente</th>
              <th className="num">Facturado</th>
            </tr>
          </thead>
          <tbody>
            {order.lines.map((l) => (
              <tr key={l.poLineId}>
                <td>{l.lineNo}</td>
                {/* GAS1-05: an expense line has no item nor unit — it shows what is bought and its category. */}
                <td className="wrap">{l.itemCode ? itemLabel(l.itemCode, l.itemDescription) : `${l.description ?? "—"} (${l.expenseCategoryName ?? "—"} · ${l.taxTypeCode ?? "—"})`}</td>
                <td>{l.uom ? uomLabel(l.uom) : "—"}</td>
                <td className="num">{formatQuantity(l.qtyOrdered)}</td>
                <td className="num">{formatDecimal(l.unitPrice)}</td>
                <td className="num">{formatDecimal(l.netAmount)}</td>
                <td className="num" data-testid="qty-received">
                  {formatQuantity(l.qtyReceived)}
                </td>
                <td className="num" data-testid="qty-open">
                  {formatQuantity(l.openQuantity)}
                </td>
                <td className="num">{formatQuantity(l.qtyInvoiced)}</td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr>
              <td colSpan={5}>
                <strong>Total</strong>
              </td>
              <td className="num">
                <strong>{formatDecimal(order.total)}</strong>
              </td>
              <td colSpan={3} />
            </tr>
          </tfoot>
        </table>
      </div>
      <h2>Recepciones</h2>
      <Receipts order={order} />
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
