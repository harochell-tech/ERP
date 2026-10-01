"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { StatementProformas, StatementTable } from "@/components/SalesStatement";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { Field, NoPermission } from "@/components/ui";
import { addDays, todayInDominicanRepublic } from "@/lib/labels";
import { csvUrl, monthStart } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { allCustomers } from "@/lib/paging";

// VS3-10b (E-VS3-09-3/4): a customer's statement of account from the ledger (it always agrees with AR-GL), with a CSV to send.
// UX4-03 (V-19): and a print view for the customer (letter, without the menu), like the quote's.

function Statement() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const params = useSearchParams();
  const customer = params.get("cliente") ?? "";
  const today = todayInDominicanRepublic();
  const [from, setFrom] = useState(params.get("desde") ?? monthStart(addDays(today, -60)));
  const [to, setTo] = useState(params.get("hasta") ?? today);
  const customers = useLoad(can("sales:read") ? () => allCustomers(companyId) : null, [companyId]);
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
          <select aria-label="Cliente" value={customer} onChange={(e) => router.push(`/ventas/estado-de-cuenta/?cliente=${e.target.value}`)}>
            <option value="">Seleccione…</option>
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
          <>
            <Link className="button" href={`/ventas/estado-de-cuenta/imprimir/?cliente=${customer}&desde=${from}&hasta=${to}`}>
              Vista para imprimir
            </Link>
            <a className="button" href={csvUrl("/api/v1/companies/{companyId}/sales/customers/{partyId}/statement", { path: { companyId, partyId: customer }, query: { from, to } })}>
              Descargar CSV
            </a>
          </>
        ) : null}
      </div>
      {!customer ? (
        <EmptyState title="Elija un cliente para ver su estado de cuenta.">
          <p>Muestra facturas, notas de crédito, cobros y retenciones del período con el saldo después de cada movimiento; se puede imprimir o descargar.</p>
        </EmptyState>
      ) : s === null ? (
        <LoadingIndicator error={statement.error} />
      ) : (
        <>
          <p>
            {s.customerName} · RNC {s.rnc ?? "—"}
          </p>
          <StatementTable statement={s} />
          <StatementProformas statement={s} />
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
