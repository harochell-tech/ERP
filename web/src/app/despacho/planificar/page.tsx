"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, FieldMessage, fieldAria, LineTable, NoPermission, useFieldErrors } from "@/components/ui";
import { formatQuantity, isDecimal, normalizeInput } from "@/lib/decimal";
import { DELIVERY_TERMS, formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { isOpenDelivery, pendingWithoutArithmetic } from "@/lib/ux4bSales";

// VS3-10a (E-VS3-04-13): plan a delivery CD-… of a confirmed order, in the order's units; the server refuses more than is still
// open (the screen shows ordered and delivered as they come, without subtracting).
// UX4-03 (V-25): a "Pendiente" column and "A despachar" prefilled with it when the server's figures give it without arithmetic.

function Plan() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const orderId = useSearchParams().get("pedido") ?? "";
  const plan = useCommand<"/api/v1/companies/{companyId}/sales/plan-delivery", Record<string, string>>(`plan-delivery:${orderId}`, "/api/v1/companies/{companyId}/sales/plan-delivery", (_r, doc) => (doc ? `Conduce ${doc} planificado.` : "Conduce planificado."));
  const [typed, setQuantities] = useState<Record<string, string> | null>(() => plan.restored ?? null);
  const [invalid, setInvalid] = useState<string | null>(null);
  const fe = useFieldErrors();
  const { data: loaded, error } = useLoad(
    can("delivery:manage") && orderId
      ? async () => {
          const [order, deliveries] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/orders/{salesOrderId}", { path: { companyId, salesOrderId: orderId } }),
            query("/api/v1/companies/{companyId}/sales/deliveries", { path: { companyId }, query: { salesOrderId: orderId, limit: 200 } }),
          ]);
          const openDeliveries = deliveries.items.filter((d) => isOpenDelivery(d.status));
          const pending = Object.fromEntries(order.lines.map((l) => [l.salesOrderLineId, pendingWithoutArithmetic(l, openDeliveries.length > 0)]));
          return { order, openDeliveries, pending };
        }
      : null,
    [companyId, orderId],
  );
  if (!can("delivery:manage")) {
    return <NoPermission />;
  }
  if (loaded === null) {
    return <LoadingIndicator error={error} />;
  }
  const data = loaded.order;
  // V-25: "A despachar" starts at the pending quantity when the server's figures give it (nothing delivered or planned yet).
  const known = Object.fromEntries(Object.entries(loaded.pending).flatMap(([lineId, q]) => (q === null ? [] : [[lineId, formatQuantity(q)]])));
  const quantities = typed ?? known;
  const allKnown = data.lines.every((l) => loaded.pending[l.salesOrderLineId] !== null);
  return (
    <>
      <p>
        <Link href="/despacho/tablero/">← Tablero</Link>
      </p>
      <h1>Planificar conduce del pedido {data.header.orderNo}</h1>
      <p>
        {data.header.customerName} · {DELIVERY_TERMS[data.header.deliveryTermCode] ?? data.header.deliveryTermCode}
        {data.siteAddress ? ` · obra: ${data.siteAddress}` : ""}
        {data.requestedDate ? ` · solicitado para ${formatDate(data.requestedDate)}` : ""}
      </p>
      {loaded.openDeliveries.length > 0 ? (
        <p className="notice" data-testid="open-deliveries">
          El pedido ya tiene conduces en curso ({loaded.openDeliveries.map((d) => d.deliveryNo).join(", ")}): indique lo que falta despachar; el sistema no
          acepta más de lo pendiente.
        </p>
      ) : null}
      <LineTable>
        <thead>
          <tr>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Pedido</th>
            <th className="num">Entregado</th>
            <th className="num">Pendiente</th>
            <th className="num">A despachar</th>
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => (
            <tr key={l.salesOrderLineId}>
              <td>
                {l.itemCode} — {l.itemDescription}
              </td>
              <td>{l.uom}</td>
              <td className="num">{formatQuantity(l.qtyOrdered)}</td>
              <td className="num">{formatQuantity(l.qtyDelivered)}</td>
              <td className="num" data-testid={`pending:${l.itemCode}`}>
                {loaded.pending[l.salesOrderLineId] !== null ? formatQuantity(loaded.pending[l.salesOrderLineId]) : <span className="muted">Lo verifica el sistema</span>}
              </td>
              <td className="num">
                <input
                  aria-label={`A despachar ${l.itemCode}`}
                  inputMode="decimal"
                  value={quantities[l.salesOrderLineId] ?? ""}
                  onChange={(e) => setQuantities({ ...quantities, [l.salesOrderLineId]: e.target.value })}
                  {...fieldAria(fe.errors[l.salesOrderLineId], `qty-${l.salesOrderLineId}`)}
                />
                <FieldMessage id={`qty-${l.salesOrderLineId}`} error={fe.errors[l.salesOrderLineId]} />
              </td>
            </tr>
          ))}
        </tbody>
      </LineTable>
      <div className="actions form-actions">
        {allKnown ? (
          <button type="button" onClick={() => setQuantities(known)}>
            Despachar todo
          </button>
        ) : null}
        <button
          type="button"
          className="primary"
          disabled={plan.busy}
          onClick={async () => {
            const entered = Object.entries(quantities).map(([salesOrderLineId, q]) => ({ salesOrderLineId, quantity: normalizeInput(q) }));
            const badLines = Object.fromEntries(
              entered
                .filter((l) => l.quantity !== "" && (!isDecimal(l.quantity, 6) || l.quantity.startsWith("-")))
                .map((l) => [l.salesOrderLineId, "Cantidad no válida (cero o más, hasta 6 decimales)."]),
            );
            if (!fe.check(badLines)) {
              setInvalid(null);
              return;
            }
            const lines = entered.filter((l) => l.quantity !== "" && /[1-9]/.test(l.quantity));
            if (lines.length === 0) {
              setInvalid("Indique al menos una cantidad mayor que cero.");
              return;
            }
            setInvalid(null);
            const response = await plan.run({ salesOrderId: orderId, lines }, quantities);
            if (response) {
              router.push(`/despacho/conduce/?id=${response.resultRef}`);
            }
          }}
        >
          Planificar conduce
        </button>
      </div>
      {invalid ? <div className="error" role="alert">{invalid}</div> : null}
      <ErrorBox error={plan.error} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Plan />
    </Suspense>
  );
}
