"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { DELIVERY_TERMS, formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS3-10a: sales orders, newest first (sales:read); the Vendedor creates them (sales_order:create).
const FILTERS: readonly { value: string; label: string }[] = [
  { value: "", label: "Todos" },
  { value: "DRAFT", label: "Borradores" },
  { value: "PENDING_CREDIT", label: "Pendientes de crédito" },
  { value: "CONFIRMED", label: "Confirmados" },
  { value: "PARTIALLY_DELIVERED", label: "Entregados parcialmente" },
  { value: "DELIVERED", label: "Entregados" },
  { value: "CLOSED", label: "Cerrados" },
  { value: "CANCELLED", label: "Cancelados" },
];

function Orders() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const params = useSearchParams();
  const status = params.get("estado") ?? "";
  const { data, error } = useLoad(
    can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/orders", { path: { companyId }, query: { status, limit: 200 } }) : null,
    [companyId, status],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <div className="actions" style={{ justifyContent: "space-between" }}>
        <h1>Pedidos de venta</h1>
        {can("sales_order:create") ? (
          <Link className="button primary" href="/ventas/pedidos/nuevo/">
            Nuevo pedido
          </Link>
        ) : null}
      </div>
      <label className="field">
        <span>Estado</span>
        <select value={status} onChange={(e) => router.push(e.target.value ? `/ventas/pedidos/?estado=${e.target.value}` : "/ventas/pedidos/")}>
          {FILTERS.map((f) => (
            <option key={f.value} value={f.value}>
              {f.label}
            </option>
          ))}
        </select>
      </label>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay pedidos con ese estado.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Número</th>
              <th>Fecha</th>
              <th>Cliente</th>
              <th>Planta</th>
              <th>Entrega</th>
              <th className="num">Total neto</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((o) => (
              <tr key={o.salesOrderId}>
                <td className="mono">
                  <Link href={`/ventas/pedido/?id=${o.salesOrderId}`}>{o.orderNo}</Link>
                </td>
                <td>{formatDate(o.orderDate)}</td>
                <td>{o.customerName}</td>
                <td>{o.plantCode}</td>
                <td>{DELIVERY_TERMS[o.deliveryTermCode] ?? o.deliveryTermCode}</td>
                <td className="num">
                  <Money value={o.totalNet} />
                </td>
                <td>
                  <StatusBadge status={o.status} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Orders />
    </Suspense>
  );
}
