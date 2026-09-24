"use client";

import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { AccountingStatus, ErrorBox, Loading, NoPermission, ReasonAction } from "@/components/ui";
import { formatDecimal, formatQuantity } from "@/lib/decimal";
import { formatDate, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Invoice = Schemas["SupplierInvoiceDetail"];

/** T-07…T-10: match, approve a price exception, post, reverse. The accounting status only changes through these commands (E-11). */
function Actions({ invoice, onDone }: { invoice: Invoice; onDone: () => void }) {
  const { can } = useSession();
  const id = invoice.supplierInvoiceId;
  const target = { supplierInvoiceId: id, expectedVersion: invoice.version };
  const match = useCommand(`match-si:${id}`, "/api/v1/companies/{companyId}/procurement/match-supplier-invoice");
  const exception = useCommand(`exception-si:${id}`, "/api/v1/companies/{companyId}/procurement/approve-match-exception");
  const post = useCommand(`post-si:${id}`, "/api/v1/companies/{companyId}/procurement/post-supplier-invoice");
  const reverse = useCommand(`reverse-si:${id}`, "/api/v1/companies/{companyId}/procurement/reverse-supplier-invoice");
  const busy = match.busy || exception.busy || post.busy || reverse.busy;
  const after = (response: unknown) => {
    if (response) {
      onDone();
    }
  };
  const document = invoice.documentStatus;
  const accounting = invoice.accountingStatus;

  return (
    <>
      <div className="actions">
        {(document === "DRAFT" || document === "MATCH_EXCEPTION") && can("supplier_invoice:match") ? (
          <button type="button" disabled={busy} onClick={async () => after(await match.run(target))}>
            {document === "DRAFT" ? "Conciliar con la orden y la recepción" : "Volver a conciliar"}
          </button>
        ) : null}
        {document === "MATCH_EXCEPTION" && can("match_exception:approve") ? (
          <ReasonAction label="Aprobar excepción" busy={busy} onConfirm={async (reason) => after(await exception.run({ ...target, reason }))} />
        ) : null}
        {document === "MATCHED" && (accounting === "NOT_POSTED" || accounting === "POSTING_BLOCKED") && can("supplier_invoice:post") ? (
          <button type="button" disabled={busy} onClick={async () => after(await post.run(target))}>
            Contabilizar
          </button>
        ) : null}
        {accounting === "POSTED" && can("supplier_invoice:reverse") ? (
          <ReasonAction label="Reversar factura" busy={busy} onConfirm={async (reason) => after(await reverse.run({ ...target, reason }))} />
        ) : null}
      </div>
      <ErrorBox error={match.error ?? exception.error ?? post.error ?? reverse.error} />
    </>
  );
}

function InvoiceDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data: invoice, error, reload } = useLoad(
    can("supplier_invoice:read") && id
      ? () => query("/api/v1/companies/{companyId}/procurement/supplier-invoices/{supplierInvoiceId}", { path: { companyId, supplierInvoiceId: id } })
      : null,
    [companyId, id],
  );

  if (!can("supplier_invoice:read")) {
    return <NoPermission />;
  }
  if (invoice === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <h1>Factura {invoice.supplierFiscalNumber}</h1>
      <dl className="facts">
        <dt>Proveedor</dt>
        <dd>{invoice.supplierName}</dd>
        <dt>Fecha / vencimiento</dt>
        <dd>
          {formatDate(invoice.docDate)} / {formatDate(invoice.dueDate)}
        </dd>
        <dt>Total</dt>
        <dd>{formatDecimal(invoice.totalAmount)}</dd>
        <dt>Estado</dt>
        <dd data-testid="si-status">{statusLabel(invoice.documentStatus)}</dd>
        <dt>Contabilidad</dt>
        <dd>
          <AccountingStatus status={invoice.accountingStatus} eventId={invoice.postingEventId} />
        </dd>
        <dt>Registrada por</dt>
        <dd>{invoice.createdBy ?? "—"}</dd>
        <dt>Excepción aprobada por</dt>
        <dd>{invoice.exceptionApprovedBy ?? "—"}</dd>
      </dl>
      <Actions invoice={invoice} onDone={reload} />
      <h2>Líneas y conciliación</h2>
      <table>
        <thead>
          <tr>
            <th>#</th>
            <th>Orden</th>
            <th>Artículo</th>
            <th className="num">Cantidad</th>
            <th className="num">Precio</th>
            <th className="num">Neto</th>
            <th className="num">Disponible</th>
            <th className="num">Dif. precio</th>
            <th>Resultado</th>
          </tr>
        </thead>
        <tbody>
          {invoice.lines.map((l) => (
            <tr key={l.siLineId}>
              <td>{l.lineNo}</td>
              <td>{l.poNo}</td>
              <td>{l.itemCode}</td>
              <td className="num">{formatQuantity(l.qty)}</td>
              <td className="num">{formatDecimal(l.unitPrice)}</td>
              <td className="num">{formatDecimal(l.netAmount)}</td>
              <td className="num">{l.match ? formatQuantity(l.match.qtyAvailableToInvoice) : "—"}</td>
              <td className="num">{l.match ? formatDecimal(l.match.priceDiff) : "—"}</td>
              <td>{l.match ? (l.match.qtyExceeds ? "Excede la cantidad" : l.match.withinTolerance ? "Dentro de tolerancia" : "Fuera de tolerancia") : "Sin conciliar"}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <h2>Impuestos determinados</h2>
      {invoice.taxes.length === 0 ? (
        <p className="muted">Se determinan al contabilizar.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Impuesto</th>
              <th className="num">Base</th>
              <th className="num">Tasa</th>
              <th className="num">Monto</th>
              <th>Efecto</th>
            </tr>
          </thead>
          <tbody>
            {invoice.taxes.map((t, i) => (
              <tr key={i}>
                <td>{t.taxCode}</td>
                <td className="num">{formatDecimal(t.base)}</td>
                <td className="num">{formatDecimal(t.rate, 0)}</td>
                <td className="num" data-testid={`tax-${t.taxCode}`}>
                  {formatDecimal(t.amount)}
                </td>
                <td>{t.effect}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {invoice.apDocument ? (
        <p>
          Cuenta por pagar: original {formatDecimal(invoice.apDocument.originalAmount)}, pendiente {formatDecimal(invoice.apDocument.openAmount)}, vence{" "}
          {formatDate(invoice.apDocument.dueDate)}.
        </p>
      ) : null}
      <History history={invoice.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <InvoiceDetail />
    </Suspense>
  );
}
