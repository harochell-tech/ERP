"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { MoneyText } from "@/components/SalesUx4";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, Money, NoPermission, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { METHODS } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { bankAccountLabel, suggestionAmounts } from "@/lib/ux4bSales";

// VS3-10b (E-VS3-10-7, E-VS3-07-2): a receipt from an ACTIVE customer. A transfer names our bank account (masked list, E-VS3-10-14)
// and its value date; a cheque its bank, number and date; cash nothing else.
// UX4-03 (V-34, E-UX4-10): one flow — with the customer and the amount, the server suggests how to apply it (open invoices oldest
// first); the amounts can be changed, and "Registrar cobro" records the receipt and then applies it. V-35: the bank account reads
// "alias · banco ••••6789" and the value date is explained.

interface Values {
  partyId: string;
  method: string;
  amount: string;
  valueDate: string;
  bankAccountId: string;
  reference: string;
  chequeBank: string;
  chequeNo: string;
  chequeDate: string;
}

function Suggestion({
  data,
  error,
  amounts,
  onChange,
  errors,
}: {
  data: Schemas["ReceiptApplicationSuggestion"] | null;
  error: unknown;
  amounts: Record<string, string> | null;
  onChange: (amounts: Record<string, string> | null) => void;
  errors: Record<string, string | undefined>;
}) {
  if (data === null) {
    return <LoadingIndicator error={error}>Buscando las facturas pendientes del cliente…</LoadingIndicator>;
  }
  if (data.invoices.length === 0) {
    return (
      <p className="muted" data-testid="suggestion-none">
        El cliente no tiene facturas pendientes: el cobro quedará sin aplicar, como saldo a su favor.
      </p>
    );
  }
  const suggested = suggestionAmounts(data.invoices);
  const current = amounts ?? suggested;
  return (
    <>
      <h2>Aplicar a facturas</h2>
      <p className="muted">
        Sugerencia del sistema: las facturas más antiguas primero. Puede cambiar los montos; el sistema los verifica al aplicar.
      </p>
      <LineTable testId="suggested-application">
        <thead>
          <tr>
            <th>Factura</th>
            <th>Fecha</th>
            <th>Vence</th>
            <th className="num">Pendiente (RD$)</th>
            <th className="num">Aplicar (RD$)</th>
          </tr>
        </thead>
        <tbody>
          {data.invoices.map((i) => (
            <tr key={i.invoiceId}>
              <td className="mono">
                <Link href={`/facturacion/factura/?id=${i.invoiceId}`}>{i.invoiceNo}</Link>
                {i.encf ? <div className="muted">{i.encf}</div> : null}
              </td>
              <td>{formatDate(i.invoiceDate)}</td>
              <td>{formatDate(i.dueDate)}</td>
              <td className="num">
                <Money value={i.openAmount} />
              </td>
              <td className="num">
                <input
                  aria-label={`Aplicar a ${i.invoiceNo}`}
                  inputMode="decimal"
                  value={current[i.invoiceId] ?? ""}
                  onChange={(e) => onChange({ ...current, [i.invoiceId]: e.target.value })}
                  {...fieldAria(errors[i.invoiceId], `new-apply-${i.invoiceId}`)}
                />
                <FieldMessage id={`new-apply-${i.invoiceId}`} error={errors[i.invoiceId]} />
              </td>
            </tr>
          ))}
        </tbody>
      </LineTable>
      {amounts === null ? (
        <p data-testid="suggestion-totals">
          Se aplican <MoneyText value={data.applied} testId="suggestion-applied" /> · quedan sin aplicar <MoneyText value={data.unapplied} testId="suggestion-unapplied" />
        </p>
      ) : (
        <p className="muted">
          Montos cambiados a mano.{" "}
          <button type="button" onClick={() => onChange(null)}>
            Volver a la sugerencia
          </button>
        </p>
      )}
    </>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const record = useCommand<"/api/v1/companies/{companyId}/sales/record-receipt", Values>("record-receipt", "/api/v1/companies/{companyId}/sales/record-receipt", (_r, doc) => (doc ? `Cobro ${doc} registrado.` : "Cobro registrado."));
  const apply = useCommand("record-receipt-apply", "/api/v1/companies/{companyId}/sales/apply-receipt");
  const today = todayInDominicanRepublic();
  const [values, setValues] = useState<Values>(
    () => record.restored ?? { partyId: "", method: "TRANSFER", amount: "", valueDate: today, bankAccountId: "", reference: "", chequeBank: "", chequeNo: "", chequeDate: today },
  );
  // null: the server's suggestion as it comes; otherwise the amounts typed over it.
  const [applyAmounts, setApplyAmounts] = useState<Record<string, string> | null>(null);
  const fe = useFieldErrors<string>();
  const allowed = can("receipt:record");
  const canApply = can("receipt:apply");
  const amount = normalizeInput(values.amount);
  const amountValid = isPositiveDecimal(amount, 2);
  const suggestion = useLoad(
    allowed && canApply && values.partyId && amountValid
      ? () => query("/api/v1/companies/{companyId}/sales/customers/{partyId}/receipt-application-suggestion", { path: { companyId, partyId: values.partyId }, query: { amount } })
      : null,
    [companyId, values.partyId, amount, amountValid],
  );
  // The suggestion shown must be the current customer's (useLoad keeps the previous answer while the next one loads).
  const suggestionData = suggestion.data && suggestion.data.partyId === values.partyId ? suggestion.data : null;
  const suggested = suggestionData ? suggestionAmounts(suggestionData.invoices) : {};
  const { data, error } = useLoad(
    allowed
      ? async () => {
          const [customers, banks] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/customers", { path: { companyId }, query: { status: "ACTIVE", limit: 200 } }),
            query("/api/v1/companies/{companyId}/sales/bank-accounts", { path: { companyId } }),
          ]);
          return { customers: customers.items, banks: banks.items };
        }
      : null,
    [companyId, allowed],
  );
  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const set = (key: keyof Values) => (e: { target: { value: string } }) => {
    if (key === "partyId" || key === "amount") {
      setApplyAmounts(null);
    }
    setValues({ ...values, [key]: e.target.value });
  };
  const bank = values.bankAccountId || data.banks[0]?.bankAccountId || "";
  const busy = record.busy || apply.busy;
  return (
    <>
      <h1>Registrar cobro</h1>
      <form
        noValidate
        onSubmit={async (e) => {
          e.preventDefault();
          const transfer = values.method === "TRANSFER";
          const cheque = values.method === "CHEQUE";
          const applications = Object.entries(applyAmounts ?? suggested)
            .map(([invoiceId, a]) => ({ invoiceId, amount: normalizeInput(a) }))
            .filter((a) => a.amount !== "");
          const lineErrors = Object.fromEntries(applications.filter((a) => !isPositiveDecimal(a.amount, 2)).map((a) => [a.invoiceId, "Monto mayor que cero, hasta 2 decimales."]));
          if (
            !fe.check({
              partyId: !values.partyId && "Elija el cliente.",
              amount: !amountValid && "Indique un monto mayor que cero (hasta 2 decimales).",
              bankAccountId: transfer && !bank && "Una transferencia necesita la cuenta bancaria a la que llegó.",
              valueDate: transfer && !values.valueDate && "Indique la fecha valor de la transferencia.",
              chequeBank: cheque && values.chequeBank.trim() === "" && "Indique el banco del cheque.",
              chequeNo: cheque && values.chequeNo.trim() === "" && "Indique el número del cheque.",
              chequeDate: cheque && !values.chequeDate && "Indique la fecha del cheque.",
              ...lineErrors,
            })
          ) {
            return;
          }
          const optional = (v: string) => (v.trim() === "" ? null : v.trim());
          const response = await record.run(
            {
              partyId: values.partyId,
              method: values.method,
              amount,
              valueDate: transfer ? values.valueDate : null,
              bankAccountId: transfer ? bank : null,
              reference: optional(values.reference),
              chequeBank: cheque ? optional(values.chequeBank) : null,
              chequeNo: cheque ? optional(values.chequeNo) : null,
              chequeDate: cheque ? values.chequeDate : null,
            },
            values,
          );
          if (!response) {
            return;
          }
          const receiptId = response.resultRef;
          if (canApply && applications.length > 0) {
            try {
              const receipt = await query("/api/v1/companies/{companyId}/sales/receipts/{receiptId}", { path: { companyId, receiptId } });
              await apply.run(
                { receiptId, expectedVersion: receipt.header.version, applications },
                undefined,
                `Cobro ${receipt.header.receiptNo} registrado y aplicado a ${applications.length} factura(s).`,
              );
            } catch {
              // The receipt is recorded either way; its page offers the application again.
            }
          }
          router.push(`/cobros/recibo/?id=${receiptId}`);
        }}
      >
        <Field label="Cliente" required error={fe.errors.partyId}>
          <select aria-label="Cliente" value={values.partyId} onChange={set("partyId")}>
            <option value="">Seleccione…</option>
            {data.customers.map((c) => (
              <option key={c.partyId} value={c.partyId}>
                {c.legalName} ({c.rnc})
              </option>
            ))}
          </select>
        </Field>
        <Field label="Medio" required>
          <select aria-label="Medio de cobro" value={values.method} onChange={set("method")}>
            {Object.entries(METHODS).map(([code, label]) => (
              <option key={code} value={code}>
                {label}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Monto (RD$)" required error={fe.errors.amount}>
          <input aria-label="Monto del cobro" inputMode="decimal" value={values.amount} onChange={set("amount")} />
        </Field>
        {values.method === "TRANSFER" ? (
          <>
            <Field label="Cuenta de la empresa" required error={fe.errors.bankAccountId} hint="La cuenta nuestra donde entró la transferencia.">
              <select aria-label="Cuenta bancaria" value={bank} onChange={set("bankAccountId")}>
                {data.banks.map((b) => (
                  <option key={b.bankAccountId} value={b.bankAccountId}>
                    {bankAccountLabel(b)}
                  </option>
                ))}
              </select>
            </Field>
            <Field
              label="Fecha valor"
              required
              error={fe.errors.valueDate}
              hint="El día en que el banco acreditó el dinero en nuestra cuenta (el que aparece en el extracto), aunque el cliente la haya enviado antes."
            >
              <input type="date" aria-label="Fecha valor" value={values.valueDate} max={today} onChange={set("valueDate")} />
            </Field>
          </>
        ) : null}
        {values.method === "CHEQUE" ? (
          <>
            <Field label="Banco del cheque" required error={fe.errors.chequeBank}>
              <input value={values.chequeBank} onChange={set("chequeBank")} />
            </Field>
            <Field label="Número del cheque" required error={fe.errors.chequeNo}>
              <input value={values.chequeNo} onChange={set("chequeNo")} />
            </Field>
            <Field label="Fecha del cheque" required error={fe.errors.chequeDate}>
              <input type="date" value={values.chequeDate} max={today} onChange={set("chequeDate")} />
            </Field>
          </>
        ) : null}
        <Field label="Referencia (opcional)">
          <input value={values.reference} onChange={set("reference")} />
        </Field>
        {values.partyId && amountValid && canApply ? (
          <Suggestion data={suggestionData} error={suggestion.error} amounts={applyAmounts} errors={fe.errors} onChange={setApplyAmounts} />
        ) : null}
        <div className="actions form-actions">
          <button type="submit" className="primary" disabled={busy}>
            Registrar cobro
          </button>
        </div>
        <ErrorBox error={record.error ?? apply.error} />
      </form>
    </>
  );
}
