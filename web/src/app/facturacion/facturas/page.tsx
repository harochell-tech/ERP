"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS3-10b: sales invoices with their three statuses (commercial, accounting, fiscal — E-1), newest first (sales:read).
const FILTERS: readonly { value: string; label: string; commercial?: string; fiscal?: string }[] = [
  { value: "", label: "Todas" },
  { value: "draft", label: "Borradores", commercial: "DRAFT" },
  { value: "ecf", label: "e-CF pendiente", fiscal: "PENDING_EXTERNAL" },
  { value: "open", label: "Por cobrar", commercial: "CONFIRMED" },
  { value: "partial", label: "Cobradas parcialmente", commercial: "PARTIALLY_PAID" },
  { value: "paid", label: "Cobradas", commercial: "PAID" },
  { value: "credited", label: "Acreditadas", commercial: "CREDITED" },
  { value: "voided", label: "Anuladas", commercial: "VOIDED" },
];

function Invoices() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const chosen = useSearchParams().get("filtro") ?? "";
  const filter = FILTERS.find((f) => f.value === chosen) ?? FILTERS[0]!;
  const { data, error } = useLoad(
    can("sales:read")
      ? () => query("/api/v1/companies/{companyId}/sales/invoices", { path: { companyId }, query: { commercialStatus: filter.commercial, fiscalStatus: filter.fiscal, limit: 200 } })
      : null,
    [companyId, filter.value],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Facturas de venta</h1>
      <label className="field">
        <span>Mostrar</span>
        <select value={filter.value} onChange={(e) => router.push(e.target.value ? `/facturacion/facturas/?filtro=${e.target.value}` : "/facturacion/facturas/")}>
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
        <p className="muted">No hay facturas.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Número</th>
              <th>e-NCF</th>
              <th>Fecha</th>
              <th>Vence</th>
              <th>Cliente</th>
              <th className="num">Total</th>
              <th className="num">Abierto</th>
              <th>Estado</th>
              <th>Fiscal</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((i) => (
              <tr key={i.invoiceId}>
                <td className="mono">
                  <Link href={`/facturacion/factura/?id=${i.invoiceId}`}>{i.invoiceNo}</Link>
                </td>
                <td className="mono">{i.encf ?? "—"}</td>
                <td>{formatDate(i.invoiceDate)}</td>
                <td>{formatDate(i.dueDate)}</td>
                <td>{i.customerName}</td>
                <td className="num">
                  <Money value={i.total ?? i.netTotal} />
                </td>
                <td className="num">
                  <Money value={i.openAmount} />
                </td>
                <td>
                  <StatusBadge status={i.commercialStatus} />
                </td>
                <td>
                  <StatusBadge status={i.fiscalStatus} />
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
      <Invoices />
    </Suspense>
  );
}
