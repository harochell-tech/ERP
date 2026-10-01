"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmAction, ErrorBox, Field, Money, ReasonAction, StatusBadge, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { bankAccountLabel } from "@/lib/ux4bSales";

type Receipt = Schemas["ReceiptDetail"];

// FIS1b-07 (E-FIS1b-4, E-FIS1b-8, E-FIS1b-01-4, 9): on a receipt — assign it to open proformas of its customer (nothing posts: the
// money waits for the proforma's invoice), release an assignment with a reason, and refund the credit balance: Cobros prepares the
// refund, the Controller releases it (step-up), Treasury matches it with the statement.

const REFUND_STATUS: Record<string, string> = { PREPARED: "Preparada", RELEASED: "Liberada", CLEARED: "Conciliada", VOIDED: "Anulada" };

/** On the receipt form: the customer has open proformas, so the payment may be theirs and not an invoice's. */
export function OpenProformasNotice({ partyId }: { partyId: string }) {
  const { companyId } = useSession();
  const { data } = useLoad(() => query("/api/v1/companies/{companyId}/sales/proformas", { path: { companyId }, query: { partyId, status: "OPEN", limit: 200 } }), [companyId, partyId]);
  const open = (data?.items ?? []).filter((f) => f.partyId === partyId && Number(f.balance) > 0);
  if (open.length === 0) {
    return null;
  }
  return (
    <p className="notice" data-testid="open-proformas-notice">
      Este cliente tiene {open.length} proforma(s) con saldo ({open.map((f) => f.proformaNo).join(", ")}). Si el cobro es de una proforma, déjelo sin aplicar a facturas y asígnelo a la
      proforma en el recibo, después de registrarlo.
    </p>
  );
}

export function AllocateToProformas({ receipt, onDone }: { receipt: Receipt; onDone: () => void }) {
  const { companyId } = useSession();
  const h = receipt.header;
  const allocate = useCommand(`allocate-receipt:${h.receiptId}`, "/api/v1/companies/{companyId}/sales/allocate-receipt-to-proformas", `Cobro ${h.receiptNo} asignado a proformas.`);
  const [amounts, setAmounts] = useState<Record<string, string>>({});
  const [invalid, setInvalid] = useState<string | null>(null);
  const { data } = useLoad(
    () => query("/api/v1/companies/{companyId}/sales/proformas", { path: { companyId }, query: { partyId: h.partyId, status: "OPEN", limit: 200 } }),
    [companyId, h.partyId, h.version],
  );
  const open = (data?.items ?? []).filter((f) => Number(f.balance) > 0);
  if (open.length === 0) {
    return null;
  }
  return (
    <section data-testid="allocate-proformas">
      <h2>Asignar a proformas</h2>
      <p className="muted">
        El cliente tiene proformas abiertas (entregas que esperan su comprobante fiscal). Lo asignado queda reservado para la factura de esa proforma; no genera asiento hasta que se
        emita.
      </p>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Proforma</th>
              <th>Vence</th>
              <th className="num">Saldo (RD$)</th>
              <th className="num">Asignar (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {open.map((f) => (
              <tr key={f.proformaId}>
                <td className="mono">
                  <Link href={`/facturacion/proforma/?id=${f.proformaId}`}>{f.proformaNo}</Link>
                </td>
                <td>{formatDate(f.dueDate)}</td>
                <td className="num">
                  <Money value={f.balance} />
                </td>
                <td className="num">
                  <input
                    aria-label={`Asignar a ${f.proformaNo}`}
                    className="mono"
                    inputMode="decimal"
                    value={amounts[f.proformaId] ?? ""}
                    onChange={(e) => setAmounts({ ...amounts, [f.proformaId]: e.target.value })}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="actions form-actions">
        <button
          type="button"
          className="primary"
          disabled={allocate.busy}
          onClick={async () => {
            const allocations = Object.entries(amounts)
              .map(([proformaId, amount]) => ({ proformaId, amount: normalizeInput(amount) }))
              .filter((a) => a.amount !== "");
            if (allocations.length === 0) {
              setInvalid("Indique al menos un monto a asignar.");
              return;
            }
            if (allocations.some((a) => !isPositiveDecimal(a.amount, 2))) {
              setInvalid("Cada monto es mayor que cero, con hasta 2 decimales.");
              return;
            }
            setInvalid(null);
            if (await allocate.run({ receiptId: h.receiptId, expectedVersion: h.version, allocations })) {
              setAmounts({});
              onDone();
            }
          }}
        >
          Asignar a proformas
        </button>
        {invalid ? (
          <span className="error" role="alert">
            {invalid}
          </span>
        ) : null}
      </div>
      <ErrorBox error={allocate.error} />
    </section>
  );
}

function Release({ receiptId, eventId, proformas, onDone }: { receiptId: string; eventId: string; proformas: string; onDone: () => void }) {
  const release = useCommand(`release-allocation:${eventId}`, "/api/v1/companies/{companyId}/sales/release-proforma-allocation", `Asignación liberada (${proformas}): el monto vuelve a quedar disponible.`);
  return (
    <>
      <ReasonAction
        label="Liberar asignación"
        consequence={`La asignación a ${proformas} se deshace: la proforma recupera su saldo y el monto queda disponible en el recibo.`}
        busy={release.busy}
        onConfirm={async (reason) => {
          if (await release.run({ receiptId, allocationEventId: eventId, reason })) {
            onDone();
          }
        }}
      />
      <ErrorBox error={release.error} />
    </>
  );
}

export function ReceiptAllocations({ receipt, onDone }: { receipt: Receipt; onDone: () => void }) {
  const { can } = useSession();
  if (receipt.allocations.length === 0) {
    return null;
  }
  const live = receipt.allocations.filter((a) => a.live);
  const groups = [...new Set(live.map((a) => a.eventId))];
  return (
    <section data-testid="receipt-allocations">
      <h2>Asignaciones a proformas</h2>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Fecha</th>
              <th>Proforma</th>
              <th className="num">Monto (RD$)</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {receipt.allocations.map((a) => (
              <tr key={a.allocationId}>
                <td>{formatDateTime(a.at)}</td>
                <td className="mono">
                  <Link href={`/facturacion/proforma/?id=${a.proformaId}`}>{a.proformaNo}</Link>
                </td>
                <td className="num">
                  <Money value={a.reversesAllocationId ? `-${a.amount}` : a.amount} />
                </td>
                <td>{a.reversesAllocationId ? "Liberación" : a.live ? "Vigente" : "Liberada o pasada a la factura"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {receipt.header.status === "RECORDED" && can("receipt:apply")
        ? groups.map((eventId) => {
            const names = live.filter((a) => a.eventId === eventId).map((a) => a.proformaNo).join(", ");
            return (
              <div key={eventId} className="inline-form">
                <span>Asignación a {names}</span>
                <Release receiptId={receipt.header.receiptId} eventId={eventId} proformas={names} onDone={onDone} />
              </div>
            );
          })
        : null}
    </section>
  );
}

function PrepareRefund({ receipt, onDone }: { receipt: Receipt; onDone: () => void }) {
  const { companyId } = useSession();
  const h = receipt.header;
  const prepare = useCommand(`prepare-refund:${h.receiptId}`, "/api/v1/companies/{companyId}/sales/prepare-customer-refund", (_r, doc) => `Devolución ${doc ?? ""} preparada: falta que el Controller la libere.`);
  const { data: banks } = useLoad(() => query("/api/v1/companies/{companyId}/sales/bank-accounts", { path: { companyId } }), [companyId]);
  const [form, setForm] = useState({ bankAccountId: "", method: "TRANSFER", amount: h.available, reason: "", reference: "" });
  const fe = useFieldErrors<"bank" | "amount" | "reason">();
  const bank = form.bankAccountId || banks?.items[0]?.bankAccountId || "";
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const amount = normalizeInput(form.amount);
        if (
          !fe.check({
            bank: bank === "" && "Elija la cuenta bancaria desde la que se paga.",
            amount: !isPositiveDecimal(amount, 2) && "Indique un monto mayor que cero, con hasta 2 decimales.",
            reason: form.reason.trim() === "" && "Indique el motivo de la devolución.",
          })
        ) {
          return;
        }
        if (await prepare.run({ receiptId: h.receiptId, bankAccountId: bank, method: form.method, amount, reason: form.reason.trim(), reference: form.reference.trim() === "" ? null : form.reference.trim() })) {
          setForm({ ...form, reason: "", reference: "" });
          onDone();
        }
      }}
    >
      <h3 style={{ marginTop: 0 }}>Preparar devolución al cliente</h3>
      <Field label="Cuenta de la empresa" required error={fe.errors.bank}>
        <select aria-label="Cuenta de la devolución" value={bank} onChange={(e) => setForm({ ...form, bankAccountId: e.target.value })}>
          {(banks?.items ?? []).map((b) => (
            <option key={b.bankAccountId} value={b.bankAccountId}>
              {bankAccountLabel(b)}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Forma de pago" required>
        <select aria-label="Forma de la devolución" value={form.method} onChange={(e) => setForm({ ...form, method: e.target.value })}>
          <option value="TRANSFER">Transferencia</option>
          <option value="CHEQUE">Cheque</option>
        </select>
      </Field>
      <Field label="Monto (RD$)" required error={fe.errors.amount} hint="Hasta el saldo a favor disponible del recibo.">
        <input aria-label="Monto de la devolución" className="mono" inputMode="decimal" value={form.amount} onChange={(e) => setForm({ ...form, amount: e.target.value })} />
      </Field>
      <Field label="Motivo" required wide error={fe.errors.reason}>
        <input aria-label="Motivo de la devolución" value={form.reason} onChange={(e) => setForm({ ...form, reason: e.target.value })} />
      </Field>
      <Field label="Referencia (opcional)" hint="Número de la transferencia o del cheque.">
        <input value={form.reference} onChange={(e) => setForm({ ...form, reference: e.target.value })} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={prepare.busy}>
          Preparar devolución
        </button>
      </div>
      <ErrorBox error={prepare.error} />
    </form>
  );
}

function RefundRow({ refund, onDone }: { refund: Schemas["CustomerRefundSummary"]; onDone: () => void }) {
  const { can, isMine } = useSession();
  const release = useCommand(`release-refund:${refund.refundId}`, "/api/v1/companies/{companyId}/sales/release-customer-refund", `Devolución ${refund.refundNo} liberada: el dinero sale del banco.`);
  const cancel = useCommand(`void-refund:${refund.refundId}`, "/api/v1/companies/{companyId}/sales/void-customer-refund", `Devolución ${refund.refundNo} anulada.`);
  const prepared = refund.status === "PREPARED";
  return (
    <tr>
      <td className="mono">{refund.refundNo}</td>
      <td>{refund.method === "TRANSFER" ? "Transferencia" : "Cheque"}</td>
      <td className="num">
        <Money value={refund.amount} />
      </td>
      <td className="wrap">{refund.voidReason ?? refund.reason}</td>
      <td>
        <StatusBadge
          status={refund.status === "PREPARED" ? "DRAFT" : refund.status === "VOIDED" ? "REJECTED" : "ACTIVE"}
          label={REFUND_STATUS[refund.status] ?? refund.status}
          testId={`refund-status:${refund.refundNo}`}
        />
        {refund.refundDate ? <span className="muted"> {formatDate(refund.refundDate)}</span> : null}
      </td>
      <td className="wrap">
        {refund.preparedBy ?? "—"}
        {refund.releasedBy ? ` → ${refund.releasedBy}` : ""}
      </td>
      <td>
        <div className="actions row-buttons">
          {prepared && can("customer_refund:release") && !isMine(refund.preparedBy) ? (
            <ConfirmAction
              label="Liberar devolución"
              className="primary"
              stepUp
              busy={release.busy}
              consequence={`Salen RD$ ${refund.amount} del banco hacia el cliente y el saldo a favor del recibo baja por ese monto. No se puede deshacer desde el sistema.`}
              onConfirm={async () => {
                if (await release.run({ refundId: refund.refundId, expectedVersion: refund.version })) {
                  onDone();
                }
              }}
            />
          ) : null}
          {prepared && can("customer_refund:prepare") ? (
            <ReasonAction
              label="Anular devolución"
              consequence={`La devolución ${refund.refundNo} no se pagará; el saldo a favor queda disponible.`}
              busy={cancel.busy}
              onConfirm={async (reason) => {
                if (await cancel.run({ refundId: refund.refundId, expectedVersion: refund.version, reason })) {
                  onDone();
                }
              }}
            />
          ) : null}
        </div>
        <ErrorBox error={release.error ?? cancel.error} />
      </td>
    </tr>
  );
}

export function ReceiptRefunds({ receipt, onDone }: { receipt: Receipt; onDone: () => void }) {
  const { companyId, can } = useSession();
  const h = receipt.header;
  const { data, reload } = useLoad(
    () => query("/api/v1/companies/{companyId}/sales/customer-refunds", { path: { companyId }, query: { partyId: h.partyId, limit: 200 } }),
    [companyId, h.partyId, h.version],
  );
  const refunds = (data?.items ?? []).filter((r) => r.receiptId === h.receiptId);
  // One refund at a time: while one waits for its release no other is offered (the server checks the amounts either way).
  const waiting = refunds.some((r) => r.status === "PREPARED");
  const refundable = h.status === "RECORDED" && h.bankStatus !== "IN_TRANSIT" && Number(h.available) > 0 && !waiting;
  const done = () => {
    reload();
    onDone();
  };
  if (refunds.length === 0 && !(refundable && can("customer_refund:prepare"))) {
    return null;
  }
  return (
    <section data-testid="receipt-refunds">
      <h2>Devoluciones al cliente</h2>
      {refunds.length > 0 ? (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Devolución</th>
                <th>Forma</th>
                <th className="num">Monto (RD$)</th>
                <th>Motivo</th>
                <th>Estado</th>
                <th>Preparó → liberó</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {refunds.map((r) => (
                <RefundRow key={`${r.refundId}:${r.version}`} refund={r} onDone={done} />
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
      {refundable && can("customer_refund:prepare") ? <PrepareRefund key={h.version} receipt={receipt} onDone={done} /> : null}
    </section>
  );
}
