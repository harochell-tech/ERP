"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { AllocateToProformas, ReceiptAllocations, ReceiptRefunds } from "@/components/ReceiptProformas";
import { MoneyText, SalesHistory } from "@/components/SalesUx4";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, FieldMessage, fieldAria, LineTable, Money, NoPermission, ReasonAction, StatusBadge, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { METHODS, applicationGroups } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { bankAccountLabel, suggestionAmounts } from "@/lib/ux4bSales";

type Receipt = Schemas["ReceiptDetail"];

// VS3-10b (E-VS3-10-7): a receipt — apply it to open invoices of its customer (one amount per invoice; the server checks and
// returns the totals), unapply a whole application with a reason, and the Controller's reversal (step-up).

function Apply({ receipt, onDone }: { receipt: Receipt; onDone: () => void }) {
  const { companyId } = useSession();
  const h = receipt.header;
  const apply = useCommand<"/api/v1/companies/{companyId}/sales/apply-receipt", Record<string, string>>(`apply-receipt:${h.receiptId}`, "/api/v1/companies/{companyId}/sales/apply-receipt", `Cobro ${h.receiptNo} aplicado.`);
  const [typed, setAmounts] = useState<Record<string, string> | null>(() => apply.restored ?? null);
  const [invalid, setInvalid] = useState<string | null>(null);
  const fe = useFieldErrors();
  const { data, error } = useLoad(
    async () => {
      const [invoices, suggestion] = await Promise.all([
        query("/api/v1/companies/{companyId}/sales/invoices", { path: { companyId }, query: { partyId: h.partyId, openOnly: "true", limit: 200 } }),
        query("/api/v1/companies/{companyId}/sales/customers/{partyId}/receipt-application-suggestion", { path: { companyId, partyId: h.partyId }, query: { amount: h.available } }),
      ]);
      return { items: invoices.items, suggested: suggestionAmounts(suggestion.invoices) };
    },
    [companyId, h.partyId, h.available],
  );
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  if (data.items.length === 0) {
    return <p className="muted">El cliente no tiene facturas pendientes: el cobro queda sin aplicar, como saldo a su favor.</p>;
  }
  // UX4-03 (V-34, E-UX4-10): the amounts start at the server's suggestion for what is still unapplied (oldest invoices first).
  const amounts = typed ?? data.suggested;
  return (
    <>
      <h2>Aplicar a facturas</h2>
      <p className="muted">Montos sugeridos por el sistema (las facturas más antiguas primero); puede cambiarlos.</p>
      <LineTable>
        <thead>
          <tr>
            <th>Factura</th>
            <th>e-NCF</th>
            <th>Vence</th>
            <th className="num">Abierto (RD$)</th>
            <th className="num">Aplicar (RD$)</th>
          </tr>
        </thead>
        <tbody>
          {data.items.map((i) => {
            const lineError = fe.errors[i.invoiceId];
            return (
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
                  <input
                    aria-label={`Aplicar a ${i.invoiceNo}`}
                    inputMode="decimal"
                    value={amounts[i.invoiceId] ?? ""}
                    onChange={(e) => setAmounts({ ...amounts, [i.invoiceId]: e.target.value })}
                    {...fieldAria(lineError, `apply-${i.invoiceId}`)}
                  />
                  <FieldMessage id={`apply-${i.invoiceId}`} error={lineError} />
                </td>
              </tr>
            );
          })}
        </tbody>
      </LineTable>
      <div className="actions form-actions">
        <button
          type="button"
          className="primary"
          disabled={apply.busy}
          onClick={async () => {
            const applications = Object.entries(amounts)
              .map(([invoiceId, a]) => ({ invoiceId, amount: normalizeInput(a) }))
              .filter((a) => a.amount !== "");
            if (!fe.check(Object.fromEntries(applications.filter((a) => !isPositiveDecimal(a.amount, 2)).map((a) => [a.invoiceId, "Monto mayor que cero, hasta 2 decimales."])))) {
              setInvalid(null);
              return;
            }
            if (applications.length === 0) {
              setInvalid("Indique al menos un monto a aplicar.");
              return;
            }
            setInvalid(null);
            const invoices = data.items.filter((i) => applications.some((a) => a.invoiceId === i.invoiceId)).map((i) => i.invoiceNo);
            if (await apply.run({ receiptId: h.receiptId, expectedVersion: h.version, applications }, amounts, `Cobro ${h.receiptNo} aplicado a ${invoices.join(", ")}.`)) {
              setAmounts(null);
              onDone();
            }
          }}
        >
          Aplicar cobro
        </button>
        {invalid ? (
          <span className="error" role="alert">
            {invalid}
          </span>
        ) : null}
      </div>
      <ErrorBox error={apply.error} />
    </>
  );
}

function Unapply({ receiptId, eventId, invoices, onDone }: { receiptId: string; eventId: string; invoices?: string; onDone: () => void }) {
  const unapply = useCommand(`unapply:${eventId}`, "/api/v1/companies/{companyId}/sales/unapply-receipt", `Aplicación deshecha${invoices ? ` (${invoices})` : ""}: el monto vuelve a quedar sin aplicar.`);
  return (
    <>
      <ReasonAction label="Desaplicar" consequence="La aplicación se deshace con un asiento contrario: las facturas vuelven a quedar abiertas por ese monto y el cobro queda sin aplicar." busy={unapply.busy} onConfirm={async (reason) => (await unapply.run({ receiptId, applicationEventId: eventId, reason })) && onDone()} />
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
  const reverse = useCommand(`reverse-receipt:${id}`, "/api/v1/companies/{companyId}/sales/reverse-receipt", (_r, doc) => `Recibo ${doc ?? data?.header.receiptNo ?? ""} anulado.`);
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
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
        {h.customerName} · {METHODS[h.method] ?? h.method} · <MoneyText value={h.amount} /> · fecha {formatDate(h.receiptDate)}
        {h.method === "TRANSFER" ? ` · el banco lo acreditó el ${formatDate(h.valueDate)} (fecha valor)` : ""}
        {h.bankCode || h.bankAccountAlias ? ` · cuenta ${bankAccountLabel({ alias: h.bankAccountAlias, bankCode: h.bankCode, accountNumber: h.bankAccountNumber })}` : ""}
        {h.chequeNo ? ` · cheque ${h.chequeNo} del ${h.chequeBank} (${formatDate(h.chequeDate)})` : ""}
        {h.reference ? ` · ref. ${h.reference}` : ""}
        {h.depositNo ? " · depósito " : ""}
        {h.depositNo && h.depositId ? <Link href={`/cobros/deposito/?id=${h.depositId}`}>{h.depositNo}</Link> : null}
      </p>
      <p>
        Sin aplicar: <MoneyText value={h.unapplied} testId="receipt-unapplied" />
        {Number(h.allocated) > 0 ? (
          <>
            {" "}
            · asignado a proformas: <MoneyText value={h.allocated} testId="receipt-allocated" /> · disponible: <MoneyText value={h.available} testId="receipt-available" />
          </>
        ) : null}{" "}
        · registró {data.recordedBy ?? "—"}
      </p>
      {data.closingReason ? <p className="muted">Motivo: {data.closingReason}</p> : null}
      {reversible && can("receipt:reverse") ? (
        <div className="actions">
          <ReasonAction label="Anular recibo" stepUp consequence="El recibo se reversa con un asiento contrario y deja de contar como cobrado. No se puede deshacer." busy={reverse.busy} onConfirm={async (reason) => (await reverse.run({ receiptId: h.receiptId, expectedVersion: h.version, reason })) && reload()} />
          <ErrorBox error={reverse.error} />
        </div>
      ) : null}
      {h.status === "RECORDED" && Number(h.available) > 0 && can("receipt:apply") ? <Apply receipt={data} onDone={reload} /> : null}
      {h.status === "RECORDED" && Number(h.available) > 0 && can("receipt:apply") ? <AllocateToProformas receipt={data} onDone={reload} /> : null}
      <ReceiptAllocations receipt={data} onDone={reload} />
      <ReceiptRefunds receipt={data} onDone={reload} />

      <h2>Aplicaciones</h2>
      {data.applications.length === 0 ? (
        <p className="muted">Sin aplicaciones.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Fecha</th>
              <th>Factura</th>
              <th className="num">Monto (RD$)</th>
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
        </table></div>
      )}
      {h.status === "RECORDED" && can("receipt:apply")
        ? groups.map((g) => (
            <div key={g.eventId} className="inline-form">
              <span>Aplicación a {g.items.map((i) => i.invoiceNo).join(", ")}</span>
              <Unapply receiptId={h.receiptId} eventId={g.eventId} invoices={g.items.map((i) => i.invoiceNo).join(", ")} onDone={reload} />
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
      <SalesHistory history={data.history} />
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
