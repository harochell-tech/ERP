"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { METHODS } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS3-10b: a deposit slip DEP-… — its receipts and the statement line it was matched to (E-VS3-07-3, E-VS3-07-10).

function DepositDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/deposits/{depositId}", { path: { companyId, depositId: id } }) : null,
    [companyId, id],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const h = data.header;
  return (
    <>
      <p>
        <Link href="/cobros/depositos/">← Depósitos</Link>
      </p>
      <h1>
        Depósito {h.depositNo} <StatusBadge status={h.status} />
      </h1>
      <p>
        {h.bankCode} {h.accountNumber} · {formatDate(h.depositDate)} · total <Money value={h.total} currency />
      </p>
      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th>Recibo</th>
            <th>Cliente</th>
            <th>Medio</th>
            <th className="num">Monto (RD$)</th>
            <th>Estado</th>
          </tr>
        </thead>
        <tbody>
          {data.receipts.map((r) => (
            <tr key={r.receiptId}>
              <td className="mono">
                <Link href={`/cobros/recibo/?id=${r.receiptId}`}>{r.receiptNo}</Link>
              </td>
              <td className="wrap">{r.customerName}</td>
              <td>{METHODS[r.method] ?? r.method}</td>
              <td className="num">
                <Money value={r.amount} />
              </td>
              <td>
                <StatusBadge status={r.status} />
              </td>
            </tr>
          ))}
        </tbody>
      </table></div>
      {data.matchedLines.map((l) => (
        <p key={l.lineId} className="muted">
          Conciliado con la línea del {formatDate(l.valueDate)} · {l.description}
        </p>
      ))}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <DepositDetail />
    </Suspense>
  );
}
