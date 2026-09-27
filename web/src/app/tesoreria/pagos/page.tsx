"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS2-08: payments of the company, newest first (payment:read; E-VS2-07-1). Numbers of accounts arrive masked (E-VS2-07-3).
const FILTERS: readonly { value: string; label: string }[] = [
  { value: "", label: "Todos" },
  { value: "PREPARED", label: "Preparados (por liberar)" },
  { value: "RELEASED", label: "Liberados (sin compensar)" },
  { value: "CLEARED", label: "Compensados" },
  { value: "REVERSED", label: "Revertidos" },
  { value: "VOIDED", label: "Anulados" },
];

function Payments() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const status = useSearchParams().get("estado") ?? "";
  const { data, error } = useLoad(
    can("payment:read") ? () => query("/api/v1/companies/{companyId}/treasury/payments", { path: { companyId }, query: { status, limit: 200 } }) : null,
    [companyId, status],
  );
  if (!can("payment:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <div className="actions" style={{ justifyContent: "space-between" }}>
        <h1>Pagos</h1>
        {can("payment:prepare") ? (
          <Link className="button primary" href="/tesoreria/propuesta/">
            Preparar un pago
          </Link>
        ) : null}
      </div>
      <label className="field">
        <span>Estado</span>
        <select value={status} onChange={(e) => router.push(e.target.value ? `/tesoreria/pagos/?estado=${e.target.value}` : "/tesoreria/pagos/")}>
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
        <p className="muted">No hay pagos con ese estado.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Número</th>
              <th>Proveedor</th>
              <th>Cuenta de la empresa</th>
              <th>Fecha valor</th>
              <th className="num">Monto</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((p) => (
              <tr key={p.paymentId}>
                <td className="mono">
                  <Link href={`/tesoreria/pago/?id=${p.paymentId}`}>{p.paymentNo}</Link>
                </td>
                <td>{p.supplierName}</td>
                <td className="mono">
                  {p.bankCode} {p.accountNumber}
                </td>
                <td>{formatDate(p.valueDate)}</td>
                <td className="num">
                  <Money value={p.amount} />
                </td>
                <td>
                  <StatusBadge status={p.status} />
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
      <Payments />
    </Suspense>
  );
}
