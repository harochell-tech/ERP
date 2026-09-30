"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { METHODS } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { bankAccountLabel } from "@/lib/ux4bSales";

// VS3-10b (E-VS3-10-7, E-VS3-07-3): deposit slips DEP-…; Cobros picks the cheques and cash in transit and the bank account
// (masked list, E-VS3-10-14). The slip's total is the server's.

function NewDeposit({ onDone }: { onDone: (depositId: string) => void }) {
  const { companyId } = useSession();
  const deposit = useCommand("deposit-receipts", "/api/v1/companies/{companyId}/sales/deposit-receipts", (_r, doc) => (doc ? `Depósito ${doc} registrado.` : "Depósito registrado."));
  const [picked, setPicked] = useState<Record<string, boolean>>({});
  const [bankAccountId, setBankAccountId] = useState("");
  const { data, error } = useLoad(
    async () => {
      const [receipts, banks] = await Promise.all([
        query("/api/v1/companies/{companyId}/sales/receipts", { path: { companyId }, query: { status: "RECORDED", bankStatus: "IN_TRANSIT", limit: 200 } }),
        query("/api/v1/companies/{companyId}/sales/bank-accounts", { path: { companyId } }),
      ]);
      return { receipts: receipts.items, banks: banks.items };
    },
    [companyId],
  );
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  if (data.receipts.length === 0) {
    return <p className="muted">No hay cheques ni efectivo por depositar: todo lo cobrado por esos medios ya está depositado.</p>;
  }
  const bank = bankAccountId || data.banks[0]?.bankAccountId || "";
  const chosen = data.receipts.filter((r) => picked[r.receiptId]).map((r) => r.receiptId);
  return (
    <>
      <h2>Nuevo depósito</h2>
      <p className="muted">Marque los cheques y el efectivo que lleva al banco en un mismo volante de depósito y elija la cuenta donde los deposita.</p>
      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th />
            <th>Recibo</th>
            <th>Cliente</th>
            <th>Medio</th>
            <th className="num">Monto (RD$)</th>
          </tr>
        </thead>
        <tbody>
          {data.receipts.map((r) => (
            <tr key={r.receiptId}>
              <td>
                <input type="checkbox" aria-label={`Depositar ${r.receiptNo}`} checked={picked[r.receiptId] ?? false} onChange={(e) => setPicked({ ...picked, [r.receiptId]: e.target.checked })} />
              </td>
              <td className="mono">{r.receiptNo}</td>
              <td className="wrap">{r.customerName}</td>
              <td>
                {METHODS[r.method] ?? r.method}
                {r.chequeNo ? ` ${r.chequeNo}` : ""}
              </td>
              <td className="num">
                <Money value={r.amount} />
              </td>
            </tr>
          ))}
        </tbody>
      </table></div>
      <div className="actions form-actions">
        <Field label="Cuenta">
          <select aria-label="Cuenta del depósito" value={bank} onChange={(e) => setBankAccountId(e.target.value)}>
            {data.banks.map((b) => (
              <option key={b.bankAccountId} value={b.bankAccountId}>
                {bankAccountLabel(b)}
              </option>
            ))}
          </select>
        </Field>
        <button
          type="button"
          className="primary"
          disabled={deposit.busy || chosen.length === 0 || !bank}
          onClick={async () => {
            const response = await deposit.run({ bankAccountId: bank, receiptIds: chosen });
            if (response) {
              onDone(response.resultRef);
            }
          }}
        >
          Depositar {chosen.length} recibo(s)
        </button>
      </div>
      <ErrorBox error={deposit.error} />
    </>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const { data, error } = useLoad(can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/deposits", { path: { companyId }, query: { limit: 200 } }) : null, [companyId]);
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Depósitos</h1>
      {can("receipt:deposit") ? <NewDeposit onDone={(id) => router.push(`/cobros/deposito/?id=${id}`)} /> : null}
      <h2>Depósitos registrados</h2>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : data.items.length === 0 ? (
        <EmptyState title="Todavía no hay depósitos."><p>Un depósito agrupa los cheques y el efectivo cobrados que se llevan juntos al banco.</p></EmptyState>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Número</th>
              <th>Fecha</th>
              <th>Cuenta</th>
              <th className="num">Recibos</th>
              <th className="num">Total (RD$)</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((d) => (
              <tr key={d.depositId}>
                <td className="mono">
                  <Link href={`/cobros/deposito/?id=${d.depositId}`}>{d.depositNo}</Link>
                </td>
                <td>{formatDate(d.depositDate)}</td>
                <td className="wrap">
                  {bankAccountLabel({ alias: d.bankAccountAlias, bankCode: d.bankCode, accountNumber: d.accountNumber })}
                </td>
                <td className="num">{d.receipts}</td>
                <td className="num">
                  <Money value={d.total} />
                </td>
                <td>
                  <StatusBadge status={d.status} />
                </td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
