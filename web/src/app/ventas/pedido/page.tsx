"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { ErrorBox, Loading, Money, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { DELIVERY_TERMS, formatDate, formatDateTime, statusLabel } from "@/lib/labels";
import { orderCancellable, orderDispatchable } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Order = Schemas["SalesOrderDetail"];

// VS3-10a (E-VS3-10-4): an order — lines priced by the server, the credit checks with the exposure they saw, and the actions of
// each role: the Vendedor edits, submits and cancels; Crédito approves (step-up) or rejects, and closes short a partly delivered
// order; Despacho plans its deliveries.

function Actions({ order, onDone }: { order: Order; onDone: () => void }) {
  const { can } = useSession();
  const h = order.header;
  const target = { salesOrderId: h.salesOrderId, expectedVersion: h.version };
  const submit = useCommand(`submit-order:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/submit-for-credit");
  const approve = useCommand(`approve-credit:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/approve-credit");
  const reject = useCommand(`reject-credit:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/reject-credit");
  const cancel = useCommand(`cancel-order:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/cancel-sales-order");
  const close = useCommand(`close-order:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/close-short-sales-order");
  const busy = submit.busy || approve.busy || reject.busy || cancel.busy || close.busy;
  const after = (response: unknown) => response && onDone();
  return (
    <>
      <div className="actions">
        {h.status === "DRAFT" && can("sales_order:create") ? (
          <>
            <Link className="button" href={`/ventas/pedidos/nuevo/?id=${h.salesOrderId}`}>
              Editar
            </Link>
            <button type="button" className="primary" disabled={busy} onClick={async () => after(await submit.run(target))}>
              Enviar a crédito
            </button>
          </>
        ) : null}
        {h.status === "PENDING_CREDIT" && can("credit:approve") ? (
          <>
            <button type="button" className="primary" disabled={busy} onClick={async () => after(await approve.run(target))}>
              Aprobar crédito
            </button>
            <ReasonAction label="Rechazar crédito" busy={busy} onConfirm={async (reason) => after(await reject.run({ ...target, reason }))} />
          </>
        ) : null}
        {/* FIS1-05 (E-FIS1-05-7): the proforma the customer takes to the DGII for a CONFOTUR exemption. */}
        <Link className="button" href={`/ventas/proforma/?id=${h.salesOrderId}`}>
          Proforma
        </Link>
        {orderDispatchable(h.status) && can("delivery:manage") ? (
          <Link className="button" href={`/despacho/planificar/?pedido=${h.salesOrderId}`}>
            Planificar conduce
          </Link>
        ) : null}
        {h.status === "PARTIALLY_DELIVERED" && can("sales_order:close") ? (
          <ReasonAction label="Cerrar con faltante" busy={busy} onConfirm={async (reason) => after(await close.run({ ...target, reason }))} />
        ) : null}
        {orderCancellable(h.status) && can("sales_order:cancel") ? (
          <ReasonAction label="Cancelar pedido" busy={busy} onConfirm={async (reason) => after(await cancel.run({ ...target, reason }))} />
        ) : null}
      </div>
      <ErrorBox error={submit.error ?? approve.error ?? reject.error ?? cancel.error ?? close.error} />
    </>
  );
}

function OrderDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("sales:read") && id
      ? async () => {
          const [order, deliveries] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/orders/{salesOrderId}", { path: { companyId, salesOrderId: id } }),
            query("/api/v1/companies/{companyId}/sales/deliveries", { path: { companyId }, query: { salesOrderId: id, limit: 200 } }),
          ]);
          const exposure = await query("/api/v1/companies/{companyId}/sales/customers/{partyId}/exposure", { path: { companyId, partyId: order.header.partyId } });
          return { order, deliveries: deliveries.items, exposure };
        }
      : null,
    [companyId, id],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const { order, deliveries, exposure } = data;
  const h = order.header;
  return (
    <>
      <p>
        <Link href="/ventas/pedidos/">← Pedidos</Link>
      </p>
      <h1>
        Pedido {h.orderNo} <StatusBadge status={h.status} testId="order-status" />
      </h1>
      <p>
        {h.customerName} · {formatDate(h.orderDate)} · planta {h.plantCode} · {DELIVERY_TERMS[h.deliveryTermCode] ?? h.deliveryTermCode}
        {order.siteAddress ? ` · obra: ${order.siteAddress}` : ""}
        {order.requestedDate ? ` · solicitado para ${formatDate(order.requestedDate)}` : ""}
        {order.customerPoRef ? ` · OC del cliente ${order.customerPoRef}` : ""}
      </p>
      {order.cancelReason ? <p className="muted">Motivo: {order.cancelReason}</p> : null}
      <Actions order={order} onDone={reload} />
      <table>
        <thead>
          <tr>
            <th className="num">#</th>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Pedido</th>
            <th className="num">Precio</th>
            <th className="num">Neto</th>
            <th className="num">Entregado</th>
            <th className="num">Facturado</th>
          </tr>
        </thead>
        <tbody>
          {order.lines.map((l) => (
            <tr key={l.salesOrderLineId}>
              <td className="num">{l.lineNo}</td>
              <td>
                {l.itemCode} — {l.itemDescription}
              </td>
              <td>{l.uom}</td>
              <td className="num">{formatQuantity(l.qtyOrdered)}</td>
              <td className="num">
                <Money value={l.unitPrice} />
              </td>
              <td className="num">
                <Money value={l.netAmount} />
              </td>
              <td className="num">{formatQuantity(l.qtyDelivered)}</td>
              <td className="num">{formatQuantity(l.qtyInvoiced)}</td>
            </tr>
          ))}
          <tr>
            <th colSpan={5}>Total neto (sin ITBIS)</th>
            <td className="num">
              <Money value={h.totalNet} testId="order-total" />
            </td>
            <td colSpan={2} />
          </tr>
        </tbody>
      </table>

      <h2>Crédito</h2>
      <p>
        Exposición actual <Money value={exposure.exposure} /> de un límite <Money value={exposure.creditLimit} /> · disponible <Money value={exposure.available} />
        {exposure.overdueDays > 0 ? ` · ${exposure.overdueDays} días de atraso` : ""}
      </p>
      {order.creditChecks.length === 0 ? (
        <p className="muted">Todavía no se ha evaluado el crédito.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Fecha</th>
              <th className="num">Pedido</th>
              <th className="num">CxC</th>
              <th className="num">Pedidos</th>
              <th className="num">Sin facturar</th>
              <th className="num">Límite</th>
              <th>Decisión</th>
              <th>Resultado</th>
              <th>Por</th>
            </tr>
          </thead>
          <tbody>
            {order.creditChecks.map((c) => (
              <tr key={c.creditCheckId}>
                <td>{formatDateTime(c.checkedAt)}</td>
                <td className="num">
                  <Money value={c.orderAmount} />
                </td>
                <td className="num">
                  <Money value={c.exposureAr} />
                </td>
                <td className="num">
                  <Money value={c.exposureOrders} />
                </td>
                <td className="num">
                  <Money value={c.exposureUninvoiced} />
                </td>
                <td className="num">
                  <Money value={c.creditLimit} />
                </td>
                <td>
                  <StatusBadge status={c.decision} />
                </td>
                <td>{c.outcome ? statusLabel(c.outcome) : "—"}</td>
                <td>{c.decidedBy ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <h2>Conduces</h2>
      {deliveries.length === 0 ? (
        <p className="muted">Sin conduces.</p>
      ) : (
        <ul>
          {deliveries.map((d) => (
            <li key={d.deliveryId}>
              <Link href={`/despacho/conduce/?id=${d.deliveryId}`}>{d.deliveryNo}</Link> — <StatusBadge status={d.status} />
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
