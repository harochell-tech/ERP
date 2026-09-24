"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, NoPermission } from "@/components/ui";
import { formatDate, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

const STATUSES = ["DRAFT", "PENDING_APPROVAL", "APPROVED", "PARTIALLY_RECEIVED", "RECEIVED", "CANCELLED"] as const;

function Orders() {
  const { companyId, can, plantFor } = useSession();
  const router = useRouter();
  const status = useSearchParams().get("estado") ?? "";
  const plantId = plantFor("purchase_order:read");
  const { data, error } = useLoad(
    can("purchase_order:read")
      ? () => query("/api/v1/companies/{companyId}/procurement/purchase-orders", { path: { companyId }, query: { status, plantId, limit: 200 } })
      : null,
    [companyId, status, plantId],
  );

  if (!can("purchase_order:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Órdenes de compra</h1>
      <div className="actions">
        <label>
          Estado:{" "}
          <select aria-label="Estado" value={status} onChange={(e) => router.push(e.target.value ? `/compras/ordenes/?estado=${e.target.value}` : "/compras/ordenes/")}>
            <option value="">Todos</option>
            {STATUSES.map((s) => (
              <option key={s} value={s}>
                {statusLabel(s)}
              </option>
            ))}
          </select>
        </label>
        {can("purchase_order:create") ? (
          <Link className="button" href="/compras/ordenes/nueva/">
            Nueva orden
          </Link>
        ) : null}
      </div>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay órdenes.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Número</th>
              <th>Proveedor</th>
              <th>Planta</th>
              <th>Fecha</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((po) => (
              <tr key={po.purchaseOrderId}>
                <td>
                  <Link href={`/compras/orden/?id=${po.purchaseOrderId}`}>{po.poNo}</Link>
                </td>
                <td>{po.supplierName}</td>
                <td>{po.plantCode}</td>
                <td>{formatDate(po.orderDate)}</td>
                <td>{statusLabel(po.status)}</td>
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
