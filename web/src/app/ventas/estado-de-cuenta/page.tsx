"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { Field, Loading, Money, NoPermission } from "@/components/ui";
import { addDays, formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { csvUrl, monthStart } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS3-10b (E-VS3-09-3/4): a customer's statement of account from the ledger (it always agrees with AR-GL), with a CSV to send.
const KINDS: Readonly<Record<string, string>> = {
  FACTURA: "Factura",
  FACTURA_ANULADA: "Factura anulada",
  NOTA_DE_CREDITO: "Nota de crédito",
  COBRO: "Cobro",
  COBRO_ANULADO: "Cobro anulado",
  CHEQUE_DEVUELTO: "Cheque devuelto",
  RETENCION: "Retención",
  RETENCION_REVERSADA: "Retención reversada",
  OTRO: "Otro",
};

function Statement() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const customer = useSearchParams().get("cliente") ?? "";
  const today = todayInDominicanRepublic();
  const [from, setFrom] = useState(monthStart(addDays(today, -60)));
  const [to, setTo] = useState(today);
  const customers = useLoad(can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/customers", { path: { companyId }, query: { limit: 200 } }) : null, [companyId]);
  const statement = useLoad(
    can("sales:read") && customer ? () => query("/api/v1/companies/{companyId}/sales/customers/{partyId}/statement", { path: { companyId, partyId: customer }, query: { from, to } }) : null,
    [companyId, customer, from, to],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  const s = statement.data;
  return (
    <>
      <h1>Estado de cuenta del cliente</h1>
      <div className="inline-form">
        <Field label="Cliente">
          <select value={customer} onChange={(e) => router.push(`/ventas/estado-de-cuenta/?cliente=${e.target.value}`)}>
            <option value="">—</option>
            {(customers.data?.items ?? []).map((c) => (
              <option key={c.partyId} value={c.partyId}>
                {c.legalName}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Desde">
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label="Hasta">
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </Field>
        {customer ? (
          <a className="button" href={csvUrl("/api/v1/companies/{companyId}/sales/customers/{partyId}/statement", { path: { companyId, partyId: customer }, query: { from, to } })}>
            Descargar CSV
          </a>
        ) : null}
      </div>
      {!customer ? (
        <p className="muted">Elija un cliente.</p>
      ) : s === null ? (
        <Loading error={statement.error} />
      ) : (
        <>
          <p>
            {s.customerName} · RNC {s.rnc ?? "—"}
          </p>
          <div className="table-wrap"><table>
            <thead>
              <tr>
                <th>Fecha</th>
                <th>Tipo</th>
                <th>Documento</th>
                <th className="num">Débito (RD$)</th>
                <th className="num">Crédito (RD$)</th>
                <th className="num">Saldo (RD$)</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>{formatDate(s.from)}</td>
                <td colSpan={4}>Saldo inicial</td>
                <td className="num">
                  <Money value={s.opening} />
                </td>
              </tr>
              {s.entries.map((e, i) => (
                <tr key={i}>
                  <td>{formatDate(e.postingDate)}</td>
                  <td>{KINDS[e.kind] ?? e.kind}</td>
                  <td className="mono">{e.documentNo ?? "—"}</td>
                  <td className="num">
                    <Money value={e.debit} />
                  </td>
                  <td className="num">
                    <Money value={e.credit} />
                  </td>
                  <td className="num">
                    <Money value={e.balance} />
                  </td>
                </tr>
              ))}
              <tr>
                <th colSpan={3}>Saldo final</th>
                <td className="num">
                  <Money value={s.totalDebit} />
                </td>
                <td className="num">
                  <Money value={s.totalCredit} />
                </td>
                <td className="num">
                  <Money value={s.closing} testId="statement-closing" />
                </td>
              </tr>
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
      <Statement />
    </Suspense>
  );
}
