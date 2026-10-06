"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { bankAccountLabel } from "@/lib/ux4a";

// USD1-07b (E-USD1-07-5, E-USD1-05b-1/2): Tesorería › Transferencias entre cuentas — buying or selling USD between the company's peso and
// USD accounts at the bank's rate, or moving money between two accounts of one currency. Tesorería prepares; someone else releases it
// (posting P-42 on its value date); it is voided while prepared and the Controller reverses it.

const STATUS: Readonly<Record<string, string>> = { PREPARED: "Preparada", RELEASED: "Liberada", VOIDED: "Anulada", REVERSED: "Reversada" };

type Bank = Schemas["BankAccountView"];

function PrepareTransfer({ banks, onDone }: { banks: readonly Bank[]; onDone: () => void }) {
  const prepare = useCommand("prepare-bank-transfer", "/api/v1/companies/{companyId}/treasury/prepare-bank-transfer");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [valueDate, setValueDate] = useState(todayInDominicanRepublic());
  const [amount, setAmount] = useState("");
  const [rate, setRate] = useState("");
  const [reference, setReference] = useState("");
  const fe = useFieldErrors<"from" | "to" | "amount" | "rate">();
  const fromBank = banks.find((b) => b.bankAccountId === from);
  const toBank = banks.find((b) => b.bankAccountId === to);
  const crosses = !!fromBank && !!toBank && fromBank.currency !== toBank.currency;
  const inUsd = fromBank?.currency === "USD" || toBank?.currency === "USD";
  return (
    <form
      className="card"
      noValidate
      data-testid="transfer-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const value = normalizeInput(amount);
        if (
          !fe.check({
            from: !from && "Elija la cuenta de origen.",
            to: (!to && "Elija la cuenta de destino.") || (to === from && "Las cuentas deben ser distintas."),
            amount: !isPositiveDecimal(value, 2) && "Monto mayor que cero, con hasta 2 decimales.",
            rate: crosses && !isPositiveDecimal(normalizeInput(rate), 4) && "Escriba la tasa que aplicó el banco (hasta 4 decimales).",
          })
        ) {
          return;
        }
        const body = {
          fromBankAccountId: from,
          toBankAccountId: to,
          valueDate,
          amount: value,
          exchangeRate: crosses ? normalizeInput(rate) : null,
          bankReference: reference.trim() || null,
        };
        if (await prepare.run(body, undefined, "Transferencia preparada: falta que otra persona la libere.")) {
          setAmount("");
          setRate("");
          setReference("");
          onDone();
        }
      }}
    >
      <h2>Nueva transferencia</h2>
      <Field label="Cuenta de origen" required error={fe.errors.from}>
        <select aria-label="Cuenta de origen" value={from} onChange={(e) => setFrom(e.target.value)}>
          <option value="">—</option>
          {banks.map((b) => (
            <option key={b.bankAccountId} value={b.bankAccountId}>
              {bankAccountLabel(b)} ({b.currency})
            </option>
          ))}
        </select>
      </Field>
      <Field label="Cuenta de destino" required error={fe.errors.to}>
        <select aria-label="Cuenta de destino" value={to} onChange={(e) => setTo(e.target.value)}>
          <option value="">—</option>
          {banks.map((b) => (
            <option key={b.bankAccountId} value={b.bankAccountId}>
              {bankAccountLabel(b)} ({b.currency})
            </option>
          ))}
        </select>
      </Field>
      <Field label="Fecha valor" required>
        <input type="date" aria-label="Fecha valor" value={valueDate} onChange={(e) => setValueDate(e.target.value)} />
      </Field>
      <Field label={inUsd ? "Monto en US$" : "Monto en RD$"} required error={fe.errors.amount}>
        <input aria-label="Monto" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
      </Field>
      {crosses ? (
        <Field label="Tasa que aplicó el banco (RD$ por US$)" required error={fe.errors.rate}>
          <input aria-label="Tasa del banco" inputMode="decimal" value={rate} onChange={(e) => setRate(e.target.value)} />
        </Field>
      ) : null}
      <Field label="Referencia bancaria (opcional)">
        <input aria-label="Referencia bancaria" maxLength={80} value={reference} onChange={(e) => setReference(e.target.value)} />
      </Field>
      <p className="muted">
        {crosses
          ? "El lado en pesos es US$ × la tasa del banco y lo calcula el sistema; no hay diferencia cambiaria al comprar o vender."
          : inUsd
            ? "Entre cuentas en dólares los pesos se valoran a la tasa aprobada del día."
            : "Entre cuentas en pesos se mueve el mismo monto."}
      </p>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={prepare.busy}>
          Preparar transferencia
        </button>
      </div>
      <ErrorBox error={prepare.error} />
    </form>
  );
}

function TransferRow({ transfer, banks, onDone }: { transfer: Schemas["BankTransferView"]; banks: readonly Bank[]; onDone: () => void }) {
  const { can, isMine } = useSession();
  const release = useCommand(`release-transfer:${transfer.transferId}`, "/api/v1/companies/{companyId}/treasury/release-bank-transfer");
  const voidIt = useCommand(`void-transfer:${transfer.transferId}`, "/api/v1/companies/{companyId}/treasury/void-bank-transfer");
  const reverse = useCommand(`reverse-transfer:${transfer.transferId}`, "/api/v1/companies/{companyId}/treasury/reverse-bank-transfer");
  const [reason, setReason] = useState("");
  const busy = release.busy || voidIt.busy || reverse.busy;
  const label = (id: string) => {
    const b = banks.find((x) => x.bankAccountId === id);
    return b ? bankAccountLabel(b) : "—";
  };
  const key = { transferId: transfer.transferId, expectedVersion: transfer.version };
  return (
    <tr data-testid={`transfer:${transfer.transferNo}`}>
      <td className="mono">{transfer.transferNo}</td>
      <td>{formatDate(transfer.valueDate)}</td>
      <td>
        {label(transfer.fromBankAccountId)} → {label(transfer.toBankAccountId)}
      </td>
      <td className="num">
        {transfer.fromCurrency} <Money value={transfer.fromAmount} />
      </td>
      <td className="num">
        {transfer.toCurrency} <Money value={transfer.toAmount} />
      </td>
      <td className="num">{transfer.exchangeRate ?? "—"}</td>
      <td>
        <StatusBadge status={transfer.status} label={STATUS[transfer.status]} />
      </td>
      <td>
        <div className="actions row-buttons">
          {transfer.status === "PREPARED" && can("payment:release") && !isMine(transfer.preparedBy) ? (
            <ConfirmAction
              label="Liberar"
              className="primary"
              stepUp
              busy={busy}
              consequence="Se contabiliza en la fecha valor: entra a la cuenta de destino y sale de la de origen."
              onConfirm={async () => (await release.run(key, undefined, `Transferencia ${transfer.transferNo} liberada.`)) && onDone()}
            />
          ) : null}
          {transfer.status === "PREPARED" && can("payment:void") ? (
            <ConfirmAction
              label="Anular"
              danger
              busy={busy}
              consequence="La transferencia preparada se anula."
              onConfirm={async () =>
                (await voidIt.run({ ...key, reason: "Anulada en Tesorería" }, undefined, `Transferencia ${transfer.transferNo} anulada.`)) && onDone()
              }
            />
          ) : null}
          {transfer.status === "RELEASED" && can("payment:reverse") ? (
            <>
              <input
                aria-label={`Motivo de la reversa de ${transfer.transferNo}`}
                placeholder="Motivo (10+ caracteres)"
                value={reason}
                onChange={(e) => setReason(e.target.value)}
              />
              <ConfirmAction
                label="Reversar"
                danger
                stepUp
                busy={busy}
                disabled={reason.trim().length < 10}
                consequence="Se reversa el asiento de la transferencia en las dos cuentas."
                onConfirm={async () =>
                  (await reverse.run({ ...key, reason: reason.trim() }, undefined, `Transferencia ${transfer.transferNo} reversada.`)) && onDone()
                }
              />
            </>
          ) : null}
        </div>
        <ErrorBox error={release.error ?? voidIt.error ?? reverse.error} />
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("payment:read") && can("bank:read");
  const data = useLoad(
    allowed
      ? async () => {
          const [banks, transfers] = await Promise.all([
            query("/api/v1/companies/{companyId}/treasury/bank-accounts", { path: { companyId } }),
            query("/api/v1/companies/{companyId}/treasury/bank-transfers", { path: { companyId } }),
          ]);
          return { banks: banks.items, transfers: transfers.items };
        }
      : null,
    [companyId],
  );
  if (!allowed) {
    return <NoPermission />;
  }
  if (data.data === null) {
    return <LoadingIndicator error={data.error} />;
  }
  const active = data.data.banks.filter((b) => b.status === "ACTIVE");
  return (
    <>
      <h1>Transferencias entre cuentas</h1>
      <p className="muted">Compra y venta de dólares entre las cuentas de la empresa, o traspasos entre cuentas de la misma moneda.</p>
      {can("payment:prepare") ? <PrepareTransfer banks={active} onDone={data.reload} /> : null}
      {data.data.transfers.length === 0 ? (
        <p className="muted">Todavía no hay transferencias.</p>
      ) : (
        <div className="table-wrap">
          <table data-testid="transfers">
            <thead>
              <tr>
                <th>Número</th>
                <th>Fecha valor</th>
                <th>Cuentas</th>
                <th className="num">Sale</th>
                <th className="num">Entra</th>
                <th className="num">Tasa</th>
                <th>Estado</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {data.data.transfers.map((t) => (
                <TransferRow key={`${t.transferId}:${t.version}`} transfer={t} banks={data.data!.banks} onDone={data.reload} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
