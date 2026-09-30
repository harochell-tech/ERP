"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { bankAccountLabel } from "@/lib/ux4a";
import { paymentsCountText } from "@/lib/ux4a-tesoreria";

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
        <>
        {/* C-31 (E-UX4-2): count and total of every payment the filter selects, the server's (all pages). */}
        <p data-testid="payments-summary">
          <strong data-testid="payments-count">{paymentsCountText(data.count)}</strong> por un total de <Money value={data.total} testId="payments-total" currency />
          {data.count > data.items.length ? <span className="muted"> · se muestran los {data.items.length} más recientes</span> : null}
        </p>
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Número</th>
              <th>Proveedor</th>
              <th>Cuenta de la empresa</th>
              <th>Fecha valor</th>
              <th className="num">Monto (RD$)</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((p) => (
              <tr key={p.paymentId}>
                <td className="mono">
                  <Link href={`/tesoreria/pago/?id=${p.paymentId}`}>{p.paymentNo}</Link>
                </td>
                <td className="wrap">{p.supplierName}</td>
                <td className="mono">{bankAccountLabel({ alias: p.bankAccountAlias, bankCode: p.bankCode, accountNumber: p.accountNumber })}</td>
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
        </table></div>
        </>
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
