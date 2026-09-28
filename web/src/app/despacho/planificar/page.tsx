"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Loading, NoPermission } from "@/components/ui";
import { formatQuantity, isDecimal, normalizeInput } from "@/lib/decimal";
import { DELIVERY_TERMS } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-04-13): plan a delivery CD-… of a confirmed order, in the order's units; the server refuses more than is still
// open (the screen shows ordered and delivered as they come, without subtracting).

function Plan() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const orderId = useSearchParams().get("pedido") ?? "";
  const plan = useCommand<"/api/v1/companies/{companyId}/sales/plan-delivery", Record<string, string>>(`plan-delivery:${orderId}`, "/api/v1/companies/{companyId}/sales/plan-delivery");
  const [quantities, setQuantities] = useState<Record<string, string>>(() => plan.restored ?? {});
  const [invalid, setInvalid] = useState<string | null>(null);
  const { data, error } = useLoad(
    can("delivery:manage") && orderId ? () => query("/api/v1/companies/{companyId}/sales/orders/{salesOrderId}", { path: { companyId, salesOrderId: orderId } }) : null,
    [companyId, orderId],
  );
  if (!can("delivery:manage")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <p>
        <Link href="/despacho/tablero/">← Tablero</Link>
      </p>
      <h1>Planificar conduce del pedido {data.header.orderNo}</h1>
      <p>
        {data.header.customerName} · {DELIVERY_TERMS[data.header.deliveryTermCode] ?? data.header.deliveryTermCode}
        {data.siteAddress ? ` · obra: ${data.siteAddress}` : ""}
      </p>
      <table>
        <thead>
          <tr>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Pedido</th>
            <th className="num">Entregado</th>
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
              <td className="num">
                <input
                  aria-label={`A despachar ${l.itemCode}`}
                  inputMode="decimal"
                  value={quantities[l.salesOrderLineId] ?? ""}
                  onChange={(e) => setQuantities({ ...quantities, [l.salesOrderLineId]: e.target.value })}
                />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="actions">
        <button
          type="button"
          className="primary"
          disabled={plan.busy}
          onClick={async () => {
            const lines = Object.entries(quantities)
              .map(([salesOrderLineId, q]) => ({ salesOrderLineId, quantity: normalizeInput(q) }))
              .filter((l) => l.quantity !== "" && /[1-9]/.test(l.quantity));
            if (lines.length === 0 || lines.some((l) => !isDecimal(l.quantity, 6) || l.quantity.startsWith("-"))) {
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
      {invalid ? <div className="error">{invalid}</div> : null}
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
