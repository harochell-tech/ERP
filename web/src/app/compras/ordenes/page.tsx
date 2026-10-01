"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { pendingMyApproval } from "@/lib/ux4a-compras";
import { allSuppliers } from "@/lib/paging";

const STATUSES = ["DRAFT", "PENDING_APPROVAL", "APPROVED", "PARTIALLY_RECEIVED", "RECEIVED", "CANCELLED"] as const;

function listHref(status: string, supplierId: string): string {
  const params = new URLSearchParams();
  if (status) {
    params.set("estado", status);
  }
  if (supplierId) {
    params.set("proveedor", supplierId);
  }
  const text = params.toString();
  return text ? `/compras/ordenes/?${text}` : "/compras/ordenes/";
}

function Orders() {
  const { companyId, can, plantFor, plantName, isMine } = useSession();
  const router = useRouter();
  const params = useSearchParams();
  const status = params.get("estado") ?? "";
  const supplierId = params.get("proveedor") ?? "";
  const plantId = plantFor("purchase_order:read");
  const approver = can("purchase_order:approve");
  const { data, error } = useLoad(
    can("purchase_order:read")
      ? () =>
          query("/api/v1/companies/{companyId}/procurement/purchase-orders", {
            path: { companyId },
            query: { status, supplierId: supplierId || undefined, plantId, limit: 200 },
          })
      : null,
    [companyId, status, supplierId, plantId],
  );
  // C-10: the supplier filter lists the active suppliers (master data) when the reader may see them.
  const suppliers = useLoad(
    can("master_data:read") ? () => allSuppliers(companyId) : null,
    [companyId],
  );
  // C-10: "Pendientes de mi aprobación (n)" for an approver.
  const pending = useLoad(
    approver
      ? () => query("/api/v1/companies/{companyId}/procurement/purchase-orders", { path: { companyId }, query: { status: "PENDING_APPROVAL", plantId: plantFor("purchase_order:approve"), limit: 200 } })
      : null,
    [companyId, approver],
  );

  if (!can("purchase_order:read")) {
    return <NoPermission />;
  }
  const supplierOptions = suppliers.data
    ? suppliers.data.items.map((s) => ({ id: s.supplierId, name: s.legalName }))
    : [...new Map((data?.items ?? []).map((po) => [po.supplierId, po.supplierName])).entries()].map(([id, name]) => ({ id, name }));
  const pendingCount = pending.data ? pendingMyApproval(pending.data.items, isMine).length : null;

  return (
    <>
      <h1>Órdenes de compra</h1>
      <div className="actions">
        <label>
          Estado:{" "}
          <select aria-label="Estado" value={status} onChange={(e) => router.push(listHref(e.target.value, supplierId))}>
            <option value="">Todos</option>
            {STATUSES.map((s) => (
              <option key={s} value={s}>
                {statusLabel(s)}
              </option>
            ))}
          </select>
        </label>
        <label>
          Proveedor:{" "}
          <select aria-label="Filtrar por proveedor" value={supplierId} onChange={(e) => router.push(listHref(status, e.target.value))}>
            <option value="">Todos</option>
            {supplierOptions.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </select>
        </label>
        {pendingCount !== null ? (
          <Link className="button" href={listHref("PENDING_APPROVAL", "")} data-testid="po-pending-mine">
            Pendientes de mi aprobación ({pendingCount})
          </Link>
        ) : null}
        {can("purchase_order:create") ? (
          <Link className="button primary" href="/compras/ordenes/nueva/">
            Nueva orden
          </Link>
        ) : null}
      </div>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">{status || supplierId ? "No hay órdenes con este filtro." : "No hay órdenes. Cree la primera con «Nueva orden»."}</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Número</th>
                <th>Proveedor</th>
                <th>Planta</th>
                <th>Fecha</th>
                <th className="num">Total (RD$)</th>
                <th>Estado</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((po) => (
                <tr key={po.purchaseOrderId} data-testid={`po-row-${po.poNo}`}>
                  <td>
                    <Link href={`/compras/orden/?id=${po.purchaseOrderId}`}>{po.poNo}</Link>
                  </td>
                  <td className="wrap">{po.supplierName}</td>
                  <td>{plantName(po.plantId, po.plantCode)}</td>
                  <td>{formatDate(po.orderDate)}</td>
                  <td className="num" data-testid="po-total">
                    <Money value={po.total} />
                  </td>
                  <td>
                    <StatusBadge status={po.status} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="muted">El total es la suma de los netos de las líneas, sin ITBIS.</p>
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
