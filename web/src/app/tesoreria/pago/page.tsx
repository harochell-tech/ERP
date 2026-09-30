"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { ConfirmAction, ErrorBox, Loading, Money, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Payment = Schemas["PaymentDetail"];

/** E-VS2-04-5: a reversal names what happened, at least 10 characters. */
const REVERSAL_REASON_MIN = 10;

// VS2-08: a payment and what can be done with it now. The preparer never sees "Liberar" (PAY-04, four eyes); the total is the
// server's (E-UI-3).
function Actions({ payment, onDone }: { payment: Payment; onDone: () => void }) {
  const { can, isMine } = useSession();
  const id = payment.paymentId;
  const target = { paymentId: id, expectedVersion: payment.version };
  const release = useCommand(`release-payment:${id}`, "/api/v1/companies/{companyId}/treasury/release-supplier-payment", `Pago ${payment.paymentNo} liberado.`);
  const voidPayment = useCommand(`void-payment:${id}`, "/api/v1/companies/{companyId}/treasury/void-payment", `Pago ${payment.paymentNo} anulado.`);
  const reverse = useCommand(`reverse-payment:${id}`, "/api/v1/companies/{companyId}/treasury/reverse-payment", `Pago ${payment.paymentNo} revertido.`);
  const busy = release.busy || voidPayment.busy || reverse.busy;
  // UX1-01b (E-UX1-01-3): preparedBy is the preparer's display name (the e-mail until the first sign-in brings one).
  const preparedByMe = isMine(payment.preparedBy);
  const after = (response: unknown) => {
    if (response) {
      onDone();
    }
  };

  return (
    <>
      <div className="actions">
        {payment.status === "PREPARED" && can("payment:release") && !preparedByMe ? (
          <ConfirmAction
            label="Liberar pago"
            title={`¿Liberar el pago ${payment.paymentNo}?`}
            consequence={`Se transfieren RD$ ${formatDecimal(payment.amount)} a ${payment.supplierName} y se contabiliza el pago. Después solo se deshace con una reversa.`}
            stepUp
            className="primary"
            busy={busy}
            onConfirm={async () => after(await release.run(target))}
          />
        ) : null}
        {payment.status === "PREPARED" && can("payment:release") && preparedByMe ? (
          <span className="muted">Lo libera alguien distinto de quien lo preparó.</span>
        ) : null}
        {payment.status === "PREPARED" && can("payment:void") ? (
          <ReasonAction
            label="Anular pago"
            consequence="El pago preparado queda anulado y las facturas vuelven a estar pendientes de pago."
            busy={busy}
            onConfirm={async (reason) => after(await voidPayment.run({ ...target, reason }))}
          />
        ) : null}
        {(payment.status === "RELEASED" || payment.status === "CLEARED") && can("payment:reverse") ? (
          <ReasonAction
            label="Revertir pago"
            consequence="Se contabiliza la reversa del pago y las facturas vuelven a quedar abiertas. No se puede deshacer."
            stepUp
            busy={busy}
            minLength={REVERSAL_REASON_MIN}
            onConfirm={async (reason) => after(await reverse.run({ ...target, reason }))}
          />
        ) : null}
        {payment.postingEventId && can("audit:read") ? (
          <Link className="button" href={`/auditoria/asientos/?evento=${payment.postingEventId}`}>
            Ver asientos
          </Link>
        ) : null}
      </div>
      <ErrorBox error={release.error ?? voidPayment.error ?? reverse.error} />
    </>
  );
}

function PaymentDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data: payment, error, reload } = useLoad(
    can("payment:read") && id ? () => query("/api/v1/companies/{companyId}/treasury/payments/{paymentId}", { path: { companyId, paymentId: id } }) : null,
    [companyId, id],
  );
  if (!can("payment:read")) {
    return <NoPermission />;
  }
  if (payment === null) {
    return <Loading error={error} />;
  }
  const rows = payment.status === "PREPARED" ? payment.plan : payment.applications;
  return (
    <>
      <div className="actions">
        <h1 className="mono" style={{ margin: 0 }}>
          {payment.paymentNo}
        </h1>
        <StatusBadge status={payment.status} testId="payment-status" />
      </div>
      <p className="muted">Transferencia a {payment.supplierName}</p>
      <dl className="summary">
        <div>
          <dt>Monto</dt>
          <dd>
            <Money value={payment.amount} testId="payment-amount" currency />
          </dd>
        </div>
        <div>
          <dt>Fecha valor</dt>
          <dd>{formatDate(payment.valueDate)}</dd>
        </div>
        <div>
          <dt>Cuenta de la empresa</dt>
          <dd className="mono">
            {payment.bankCode} {payment.accountNumber}
          </dd>
        </div>
        <div>
          <dt>Cuenta del proveedor</dt>
          <dd className="mono">
            {payment.partyBankCode} {payment.partyAccountNumber} <StatusBadge status={payment.partyAccountStatus} />
          </dd>
        </div>
        <div>
          <dt>Preparado por</dt>
          <dd>{payment.preparedBy ?? "—"}</dd>
        </div>
        <div>
          <dt>Liberado por</dt>
          <dd>{payment.releasedBy ?? "—"}</dd>
        </div>
        <div>
          <dt>Referencia bancaria</dt>
          <dd>{payment.bankReference ?? "—"}</dd>
        </div>
      </dl>
      <Actions payment={payment} onDone={reload} />
      <h2>{payment.status === "PREPARED" ? "Facturas que pagará" : "Facturas pagadas"}</h2>
      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th>NCF</th>
            <th>Vence</th>
            <th className="num">Monto (RD$)</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {rows.map((a, i) => (
            <tr key={`${a.apDocId}:${a.reversal}:${i}`}>
              <td className="mono">
                {can("supplier_invoice:read") ? <Link href={`/cxp/factura/?id=${a.supplierInvoiceId}`}>{a.supplierFiscalNumber}</Link> : a.supplierFiscalNumber}
              </td>
              <td>{formatDate(a.dueDate)}</td>
              <td className="num">
                <Money value={a.amount} />
              </td>
              <td>{a.reversal ? <StatusBadge status="REVERSED" label="Reversa" /> : null}</td>
            </tr>
          ))}
        </tbody>
      </table></div>
      <h2>Conciliación bancaria</h2>
      {payment.statementLines.length === 0 ? (
        payment.status === "RELEASED" ? (
          <p className="notice">Aún no hay una línea del extracto con este pago. Es una partida en tránsito hasta que Tesorería la concilie.</p>
        ) : (
          <p className="muted">Sin líneas del extracto.</p>
        )
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Fecha</th>
              <th>Tipo</th>
              <th>Descripción</th>
              <th className="num">Monto (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {payment.statementLines.map((l) => (
              <tr key={l.lineId}>
                <td>{formatDate(l.valueDate)}</td>
                <td>{l.direction === "DEBIT" ? "Débito (transferencia)" : "Crédito (devolución)"}</td>
                <td className="wrap">
                  {l.description} {l.bankReference ? <span className="muted">· {l.bankReference}</span> : null}
                </td>
                <td className="num">
                  <Money value={l.amount} />
                </td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
      <History history={payment.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <PaymentDetail />
    </Suspense>
  );
}
