"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { MoneyText } from "@/components/SalesUx4";
import { StatementTable } from "@/components/SalesStatement";
import { LoadingIndicator } from "@/components/StateNotices";
import { NoPermission } from "@/components/ui";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// UX4-03 (V-19): the statement of account the customer receives — issuer, customer, period, the ledger's lines and the closing
// balance (the server's). The browser prints it (the menu and the buttons are hidden, the proforma's print CSS); no PDF is made.

function StatementPrint() {
  const { companyId, company, can } = useSession();
  const params = useSearchParams();
  const customer = params.get("cliente") ?? "";
  const from = params.get("desde") ?? "";
  const to = params.get("hasta") ?? "";
  const { data, error } = useLoad(
    can("sales:read") && customer && from && to
      ? () => query("/api/v1/companies/{companyId}/sales/customers/{partyId}/statement", { path: { companyId, partyId: customer }, query: { from, to } })
      : null,
    [companyId, customer, from, to],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  return (
    <div className="proforma statement-print">
      <div className="actions no-print">
        <Link href={`/ventas/estado-de-cuenta/?cliente=${customer}&desde=${from}&hasta=${to}`}>← Estado de cuenta</Link>
        <button type="button" className="primary" onClick={() => window.print()}>
          Imprimir
        </button>
      </div>
      <div className="print-head">
        <div>
          <h1>Estado de cuenta</h1>
          <p>
            Del {formatDate(data.from)} al {formatDate(data.to)} · emitido el {formatDate(todayInDominicanRepublic())}
          </p>
        </div>
      </div>
      <dl className="facts">
        <dt>Emisor</dt>
        <dd>{company?.legalName ?? ""}</dd>
        <dt>Cliente</dt>
        <dd data-testid="print-statement-customer">
          {data.customerName}
          {data.rnc ? (
            <>
              {" "}
              · RNC <span className="mono">{data.rnc}</span>
            </>
          ) : null}
        </dd>
        <dt>Saldo al {formatDate(data.to)}</dt>
        <dd>
          <strong>
            <MoneyText value={data.closing} testId="print-statement-closing" />
          </strong>
        </dd>
      </dl>
      <StatementTable statement={data} />
      <p className="muted">Si encuentra alguna diferencia, comuníquese con nuestro departamento de cobros. Documento no fiscal.</p>
    </div>
  );
}

export default function Page() {
  return (
    <Suspense>
      <StatementPrint />
    </Suspense>
  );
}
