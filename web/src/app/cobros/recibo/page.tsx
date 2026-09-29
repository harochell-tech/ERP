"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { ErrorBox, Loading, Money, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { METHODS, applicationGroups } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Receipt = Schemas["ReceiptDetail"];

// VS3-10b (E-VS3-10-7): a receipt — apply it to open invoices of its customer (one amount per invoice; the server checks and
// returns the totals), unapply a whole application with a reason, and the Controller's reversal (step-up).

function Apply({ receipt, onDone }: { receipt: Receipt; onDone: () => void }) {
  const { companyId } = useSession();
  const h = receipt.header;
  const apply = useCommand<"/api/v1/companies/{companyId}/sales/apply-receipt", Record<string, string>>(`apply-receipt:${h.receiptId}`, "/api/v1/companies/{companyId}/sales/apply-receipt");
  const [amounts, setAmounts] = useState<Record<string, string>>(() => apply.restored ?? {});
  const [invalid, setInvalid] = useState<string | null>(null);
  const { data, error } = useLoad(
    () => query("/api/v1/companies/{companyId}/sales/invoices", { path: { companyId }, query: { partyId: h.partyId, openOnly: "true", limit: 200 } }),
    [companyId, h.partyId],
  );
  if (data === null) {
    return <Loading error={error} />;
  }
  if (data.items.length === 0) {
    return <p className="muted">El cliente no tiene facturas abiertas: el cobro queda como anticipo sin aplicar.</p>;
  }
  return (
    <>
      <h2>Aplicar a facturas</h2>
      <table>
        <thead>
          <tr>
            <th>Factura</th>
            <th>e-NCF</th>
            <th>Vence</th>
            <th className="num">Abierto</th>
            <th className="num">Aplicar</th>
          </tr>
        </thead>
        <tbody>
          {data.items.map((i) => (
            <tr key={i.invoiceId}>
              <td className="mono">
                <Link href={`/facturacion/factura/?id=${i.invoiceId}`}>{i.invoiceNo}</Link>
              </td>
              <td className="mono">{i.encf ?? "—"}</td>
              <td>{formatDate(i.dueDate)}</td>
              <td className="num">
                <Money value={i.openAmount} />
              </td>
              <td className="num">
                <input aria-label={`Aplicar a ${i.invoiceNo}`} inputMode="decimal" value={amounts[i.invoiceId] ?? ""} onChange={(e) => setAmounts({ ...amounts, [i.invoiceId]: e.target.value })} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <button
        type="button"
        className="primary"
        disabled={apply.busy}
        onClick={async () => {
          const applications = Object.entries(amounts)
            .map(([invoiceId, a]) => ({ invoiceId, amount: normalizeInput(a) }))
            .filter((a) => a.amount !== "");
          if (applications.length === 0 || applications.some((a) => !isPositiveDecimal(a.amount, 2))) {
            setInvalid("Indique al menos un monto a aplicar (hasta 2 decimales).");
            return;
          }
          setInvalid(null);
          if (await apply.run({ receiptId: h.receiptId, expectedVersion: h.version, applications }, amounts)) {
            setAmounts({});
            onDone();
          }
        }}
      >
        Aplicar cobro
      </button>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={apply.error} />
    </>
  );
}

function Unapply({ receiptId, eventId, onDone }: { receiptId: string; eventId: string; onDone: () => void }) {
  const unapply = useCommand(`unapply:${eventId}`, "/api/v1/companies/{companyId}/sales/unapply-receipt");
  return (
    <>
      <ReasonAction label="Desaplicar" busy={unapply.busy} onConfirm={async (reason) => (await unapply.run({ receiptId, applicationEventId: eventId, reason })) && onDone()} />
      <ErrorBox error={unapply.error} />
    </>
  );
}

function ReceiptDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/receipts/{receiptId}", { path: { companyId, receiptId: id } }) : null,
    [companyId, id],
  );
  const reverse = useCommand(`reverse-receipt:${id}`, "/api/v1/companies/{companyId}/sales/reverse-receipt");
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const h = data.header;
  const groups = applicationGroups(data.applications);
  const reversible = h.status === "RECORDED" && h.applicationStatus === "UNAPPLIED" && (h.method === "TRANSFER" ? h.bankStatus === "DEPOSITED" : h.bankStatus === "IN_TRANSIT");
  return (
    <>
      <p>
        <Link href="/cobros/recibos/">← Recibos</Link>
      </p>
      <h1>
        Recibo {h.receiptNo} <StatusBadge status={h.status} testId="receipt-status" /> <StatusBadge status={h.applicationStatus} testId="receipt-application" />{" "}
        <StatusBadge status={h.bankStatus} />
      </h1>
      <p>
        {h.customerName} · {METHODS[h.method] ?? h.method} · <Money value={h.amount} /> · fecha {formatDate(h.receiptDate)}
        {h.method === "TRANSFER" ? ` · fecha valor ${formatDate(h.valueDate)}` : ""}
        {h.chequeNo ? ` · cheque ${h.chequeNo} del ${h.chequeBank} (${formatDate(h.chequeDate)})` : ""}
        {h.reference ? ` · ref. ${h.reference}` : ""}
        {h.depositNo ? " · depósito " : ""}
        {h.depositNo && h.depositId ? <Link href={`/cobros/deposito/?id=${h.depositId}`}>{h.depositNo}</Link> : null}
      </p>
      <p>
        Sin aplicar: <Money value={h.unapplied} testId="receipt-unapplied" /> · registró {data.recordedBy ?? "—"}
      </p>
      {data.closingReason ? <p className="muted">Motivo: {data.closingReason}</p> : null}
      {reversible && can("receipt:reverse") ? (
        <div className="actions">
          <ReasonAction label="Anular recibo" busy={reverse.busy} onConfirm={async (reason) => (await reverse.run({ receiptId: h.receiptId, expectedVersion: h.version, reason })) && reload()} />
          <ErrorBox error={reverse.error} />
        </div>
      ) : null}
      {h.status === "RECORDED" && h.applicationStatus !== "APPLIED" && can("receipt:apply") ? <Apply receipt={data} onDone={reload} /> : null}

      <h2>Aplicaciones</h2>
      {data.applications.length === 0 ? (
        <p className="muted">Sin aplicaciones.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Fecha</th>
              <th>Factura</th>
              <th className="num">Monto</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {data.applications.map((a) => (
              <tr key={a.applicationId}>
                <td>{formatDateTime(a.at)}</td>
                <td className="mono">
                  <Link href={`/facturacion/factura/?id=${a.invoiceId}`}>{a.invoiceNo}</Link>
                </td>
                <td className="num">
                  <Money value={a.reversesApplicationId ? `-${a.amount}` : a.amount} />
                </td>
                <td>{a.reversesApplicationId ? "Desaplicación" : a.live ? "Vigente" : "Desaplicada"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {h.status === "RECORDED" && can("receipt:apply")
        ? groups.map((g) => (
            <div key={g.eventId} className="inline-form">
              <span>Aplicación a {g.items.map((i) => i.invoiceNo).join(", ")}</span>
              <Unapply receiptId={h.receiptId} eventId={g.eventId} onDone={reload} />
            </div>
          ))
        : null}
      {data.matchedLines.length > 0 ? (
        <>
          <h2>Líneas del extracto</h2>
          <ul>
            {data.matchedLines.map((l) => (
              <li key={l.lineId}>
                {formatDate(l.valueDate)} · {l.direction === "CREDIT" ? "Crédito" : "Débito"} <Money value={l.amount} /> · {l.description}
              </li>
            ))}
          </ul>
        </>
      ) : null}
      <History history={data.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <ReceiptDetail />
    </Suspense>
  );
}
