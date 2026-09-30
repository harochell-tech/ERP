"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { Money, NoPermission, StatusBadge } from "@/components/ui";
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
  const { companyId, can, plantName } = useSession();
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
        <LoadingIndicator error={error} />
      ) : data.items.length === 0 ? (
        <EmptyState title={status ? "No hay pedidos con ese estado." : "Todavía no hay pedidos."} steps={[status && { href: "/ventas/pedidos/", label: "Ver todos los pedidos" }, can("sales_order:create") && { href: "/ventas/pedidos/nuevo/", label: "Crear un pedido" }]} />
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Número</th>
              <th>Fecha</th>
              <th>Cliente</th>
              <th>Planta</th>
              <th>Entrega</th>
              <th className="num">Total neto (RD$)</th>
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
                <td className="wrap">{o.customerName}</td>
                <td>{plantName(o.plantCode)}</td>
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
        </table></div>
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
