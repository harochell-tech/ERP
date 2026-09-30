"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { METHODS } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS3-10b (E-VS3-10-7): customer receipts REC-… with their three statuses (E-VS3-07-4), newest first (sales:read).
const FILTERS: readonly { value: string; label: string; query: Record<string, string> }[] = [
  { value: "", label: "Todos", query: {} },
  { value: "sin-aplicar", label: "Con saldo sin aplicar", query: { status: "RECORDED", applicationStatus: "UNAPPLIED" } },
  { value: "parcial", label: "Aplicados parcialmente", query: { status: "RECORDED", applicationStatus: "PARTIALLY_APPLIED" } },
  { value: "en-transito", label: "Cheques y efectivo sin depositar", query: { status: "RECORDED", bankStatus: "IN_TRANSIT" } },
  { value: "devueltos", label: "Cheques devueltos", query: { status: "BOUNCED" } },
  { value: "anulados", label: "Anulados", query: { status: "REVERSED" } },
];

function Receipts() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const chosen = useSearchParams().get("filtro") ?? "";
  const filter = FILTERS.find((f) => f.value === chosen) ?? FILTERS[0]!;
  const { data, error } = useLoad(
    can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/receipts", { path: { companyId }, query: { ...filter.query, limit: 200 } }) : null,
    [companyId, filter.value],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <div className="actions" style={{ justifyContent: "space-between" }}>
        <h1>Recibos de cobro</h1>
        {can("receipt:record") ? (
          <Link className="button primary" href="/cobros/recibos/nuevo/">
            Registrar cobro
          </Link>
        ) : null}
      </div>
      <label className="field">
        <span>Mostrar</span>
        <select value={filter.value} onChange={(e) => router.push(e.target.value ? `/cobros/recibos/?filtro=${e.target.value}` : "/cobros/recibos/")}>
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
        <p className="muted">No hay recibos.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Número</th>
              <th>Fecha</th>
              <th>Cliente</th>
              <th>Medio</th>
              <th className="num">Monto (RD$)</th>
              <th className="num">Sin aplicar (RD$)</th>
              <th>Estado</th>
              <th>Aplicación</th>
              <th>Banco</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((r) => (
              <tr key={r.receiptId}>
                <td className="mono">
                  <Link href={`/cobros/recibo/?id=${r.receiptId}`}>{r.receiptNo}</Link>
                </td>
                <td>{formatDate(r.receiptDate)}</td>
                <td className="wrap">{r.customerName}</td>
                <td>{METHODS[r.method] ?? r.method}</td>
                <td className="num">
                  <Money value={r.amount} />
                </td>
                <td className="num">
                  <Money value={r.unapplied} />
                </td>
                <td>
                  <StatusBadge status={r.status} />
                </td>
                <td>
                  <StatusBadge status={r.applicationStatus} />
                </td>
                <td>
                  <StatusBadge status={r.bankStatus} />
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
      <Receipts />
    </Suspense>
  );
}
