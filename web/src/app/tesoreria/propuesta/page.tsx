"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, Loading, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { formatDecimal, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { bankAccountLabel } from "@/lib/ux4a";
import { activeShortcut, ALL_DUE, DUE_SHORTCUTS, dueUntilFor, laterDueText } from "@/lib/ux4a-tesoreria";
import { SearchSelect } from "@/components/SearchSelect";

type Supplier = Schemas["ProposalSupplier"];

/** Money typed by the treasurer: at most 2 decimals (E-VS2-03-5). */
const MONEY_SCALE = 2;

// VS2-08 / E-VS2-07-4: POSTED invoices with a balance due by the chosen date, per supplier with its payability; the treasurer
// picks invoices and amounts and prepares one payment for one supplier. Preparing reserves nothing and posts nothing (E-VS2-6);
// the payment's total is the server's, shown on the payment once prepared (E-UI-3).
// USD1-07b (E-USD1-07-5, E-USD1-05-2/3): a foreign supplier's invoices are paid in USD — from a USD account at the day's approved rate, or
// from a peso account at the rate the bank charged, typed here; the exchange difference is the server's, at release.
function PrepareForm({ supplier, today }: { supplier: Supplier; today: string }) {
  const { companyId, can } = useSession();
  const router = useRouter();
  const prepare = useCommand(
    `prepare-payment:${supplier.supplierId}`,
    "/api/v1/companies/{companyId}/treasury/prepare-supplier-payment",
    (_, doc) => `Pago ${doc ? `${doc} ` : ""}preparado para ${supplier.supplierName}; falta que otra persona lo libere.`,
  );
  const { data: banks } = useLoad(() => query("/api/v1/companies/{companyId}/treasury/bank-accounts", { path: { companyId } }), [companyId]);
  const usd = supplier.invoices.some((i) => i.currency === "USD");
  // Peso invoices leave a peso account; USD invoices a USD or a peso account.
  const active = (banks?.items ?? []).filter((b) => b.status === "ACTIVE" && (usd || b.currency === "DOP"));
  const [rate, setRate] = useState("");
  const [bankAccountId, setBankAccountId] = useState("");
  const [valueDate, setValueDate] = useState(today);
  const [reference, setReference] = useState("");
  // What the treasurer typed per invoice; an invoice not typed yet pays its open balance — also one that shows up later, when the
  // due-date filter widens (GAS1-07 found it: «Todo» added an invoice without an amount).
  const [typed, setChosen] = useState<Record<string, string>>({});
  const chosen: Record<string, string> = Object.fromEntries(
    supplier.invoices.map((i) => [i.apDocId, typed[i.apDocId] ?? normalizeInput(formatDecimal(usd ? (i.openAmountUsd ?? i.openAmount) : i.openAmount))]),
  );
  const [picked, setPicked] = useState<Record<string, boolean>>({});
  const fe = useFieldErrors();
  const canPrepare = can("payment:prepare") && supplier.partyBankAccountId !== null;
  const bank = bankAccountId || active[0]?.bankAccountId || "";
  const needsRate = usd && active.find((b) => b.bankAccountId === bank)?.currency === "DOP";

  return (
    <form
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (!supplier.partyBankAccountId) {
          return;
        }
        const pickedInvoices = supplier.invoices.filter((i) => picked[i.apDocId]);
        const applications = pickedInvoices.map((i) => ({ apDocId: i.apDocId, amount: normalizeInput(chosen[i.apDocId] ?? "") }));
        const found: Record<string, string | false> = {
          invoices: applications.length === 0 && "Marque al menos una factura para pagar.",
          bank: bank === "" && "Elija la cuenta de la empresa.",
          valueDate: valueDate === "" && "Indique la fecha valor.",
          rate: needsRate && !isPositiveDecimal(normalizeInput(rate), 4) && "Escriba la tasa que cobró el banco (hasta 4 decimales).",
        };
        for (const a of applications) {
          found[`amount-${a.apDocId}`] = !isPositiveDecimal(a.amount, MONEY_SCALE) && "Monto mayor que cero, con máximo 2 decimales.";
        }
        if (!fe.check(found)) {
          return;
        }
        const response = await prepare.run({
          partyId: supplier.supplierId,
          bankAccountId: bank,
          partyBankAccountId: supplier.partyBankAccountId,
          valueDate,
          bankReference: reference.trim() === "" ? null : reference.trim(),
          applications,
          exchangeRate: needsRate ? normalizeInput(rate) : null,
        });
        if (response) {
          router.push(`/tesoreria/pago/?id=${response.resultRef}`);
        }
      }}
    >
      <LineTable>
        <thead>
          <tr>
            <th>Pagar</th>
            <th>NCF</th>
            <th>Fecha</th>
            <th>Vence</th>
            <th className="num">Saldo abierto (RD$)</th>
            {usd ? <th className="num">Saldo abierto (US$)</th> : null}
            <th className="num">A pagar ({usd ? "US$" : "RD$"})</th>
          </tr>
        </thead>
        <tbody>
          {supplier.invoices.map((i) => {
            const amountError = fe.errors[`amount-${i.apDocId}`];
            const messageId = `amount-${i.apDocId}-message`;
            return (
              <tr key={i.apDocId}>
                <td>
                  <input
                    type="checkbox"
                    aria-label={`Pagar ${i.supplierFiscalNumber}`}
                    checked={picked[i.apDocId] ?? false}
                    onChange={(e) => setPicked({ ...picked, [i.apDocId]: e.target.checked })}
                    style={{ minHeight: 20, width: 20 }}
                  />
                </td>
                <td className="mono">{i.supplierFiscalNumber}</td>
                <td>{formatDate(i.docDate)}</td>
                <td>{i.dueDate < today ? <StatusBadge status="MATCH_EXCEPTION" label={`Vencida ${formatDate(i.dueDate)}`} /> : formatDate(i.dueDate)}</td>
                <td className="num">
                  <Money value={i.openAmount} />
                </td>
                {usd ? (
                  <td className="num">
                    <Money value={i.openAmountUsd} />
                  </td>
                ) : null}
                <td className="num">
                  <input
                    aria-label={`A pagar ${i.supplierFiscalNumber}`}
                    className="mono"
                    style={{ width: "100%", maxWidth: 160, textAlign: "right" }}
                    inputMode="decimal"
                    value={chosen[i.apDocId] ?? ""}
                    disabled={!picked[i.apDocId]}
                    onChange={(e) => setChosen({ ...typed, [i.apDocId]: e.target.value })}
                    {...fieldAria(amountError, messageId, picked[i.apDocId] ?? false)}
                  />
                  <FieldMessage id={messageId} error={amountError} />
                </td>
              </tr>
            );
          })}
        </tbody>
      </LineTable>
      {fe.errors.invoices ? (
        <div className="error" role="alert">
          {fe.errors.invoices}
        </div>
      ) : null}
      {canPrepare ? (
        <div className="card">
          <h2 style={{ marginTop: 0 }}>Preparar pago</h2>
          <Field label="Cuenta de la empresa" required error={fe.errors.bank}>
            <select value={bank} onChange={(e) => setBankAccountId(e.target.value)}>
              {active.map((b) => (
                <option key={b.bankAccountId} value={b.bankAccountId}>
                  {bankAccountLabel(b)}
                  {usd ? ` (${b.currency})` : ""}
                </option>
              ))}
            </select>
          </Field>
          {needsRate ? (
            <Field label="Tasa que cobró el banco (RD$ por US$)" required error={fe.errors.rate}>
              <input aria-label="Tasa del banco" inputMode="decimal" value={rate} onChange={(e) => setRate(e.target.value)} />
            </Field>
          ) : usd ? (
            <p className="muted">Desde una cuenta en dólares el pago se valora a la tasa aprobada de la fecha valor.</p>
          ) : null}
          <Field label="Fecha valor" required error={fe.errors.valueDate}>
            <input type="date" value={valueDate} onChange={(e) => setValueDate(e.target.value)} />
          </Field>
          <Field label="Referencia (opcional)">
            <input value={reference} onChange={(e) => setReference(e.target.value)} />
          </Field>
          <p className="muted">
            Preparar no reserva saldo ni contabiliza. Al liberar se vuelven a validar saldos, la cuenta del proveedor y la fecha valor. El número PAG- y el
            total salen del servidor: escriba el número en la transferencia.
          </p>
          <ErrorBox error={prepare.error} />
          <div className="actions form-actions">
            <button type="submit" className="primary" disabled={prepare.busy}>
              Preparar pago
            </button>
          </div>
        </div>
      ) : supplier.partyBankAccountId === null ? (
        <p className="notice">El proveedor no tiene una cuenta bancaria verificada: solicítela desde su ficha.</p>
      ) : null}
    </form>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const today = todayInDominicanRepublic();
  // C-26 / E-UX4-12: the proposal opens with what falls due this week (today + 7).
  const [dueUntil, setDueUntil] = useState(() => dueUntilFor("week", today));
  const [supplierId, setSupplierId] = useState("");
  const { data, error } = useLoad(
    can("payment:read") ? () => query("/api/v1/companies/{companyId}/treasury/payment-proposal", { path: { companyId }, query: { dueUntil } }) : null,
    [companyId, dueUntil],
  );
  const empty = data !== null && data.suppliers.length === 0 && dueUntil !== ALL_DUE;
  // When nothing falls due by the date, the same proposal without a limit tells how much falls due later (rows counted only).
  const later = useLoad(
    can("payment:read") && empty ? () => query("/api/v1/companies/{companyId}/treasury/payment-proposal", { path: { companyId }, query: { dueUntil: ALL_DUE } }) : null,
    [companyId, empty],
  );
  if (!can("payment:read")) {
    return <NoPermission />;
  }
  const supplier = data?.suppliers.find((s) => s.supplierId === supplierId) ?? data?.suppliers[0];
  const shortcut = activeShortcut(dueUntil, today);
  const laterText = empty && later.data ? laterDueText(later.data.suppliers) : null;
  return (
    <>
      <h1>Propuesta de pago</h1>
      <p className="muted">Facturas contabilizadas con saldo que vencen hasta la fecha elegida. Un pago es para un solo proveedor.</p>
      <div className="card">
        <div className="actions" role="group" aria-label="Vence hasta: atajos">
          {DUE_SHORTCUTS.map((s) => (
            <button
              key={s.id}
              type="button"
              aria-pressed={shortcut === s.id}
              className={shortcut === s.id ? "primary" : undefined}
              onClick={() => setDueUntil(dueUntilFor(s.id, today))}
            >
              {s.label}
            </button>
          ))}
        </div>
        <Field label="Vence hasta" hint={shortcut === "all" ? "Todas las facturas con saldo, venzan cuando venzan." : undefined}>
          <input type="date" value={dueUntil} onChange={(e) => setDueUntil(e.target.value)} />
        </Field>
        {data && data.suppliers.length > 0 ? (
          <Field label="Proveedor">
            <SearchSelect
              aria-label="Proveedor"
              value={supplier?.supplierId ?? ""}
              onChange={setSupplierId}
              clearable={false}
              options={data.suppliers.map((s) => ({ value: s.supplierId, label: s.supplierName, hint: `RD$ ${formatDecimal(s.openAmount)}` }))}
            />
          </Field>
        ) : null}
        {supplier ? (
          <p>
            Cuenta del proveedor: <StatusBadge status={supplier.payability} testId="supplier-payability" />{" "}
            {supplier.accountNumber ? (
              <span className="mono">
                {supplier.bankCode} {supplier.accountNumber}
              </span>
            ) : null}
            {supplier.payability === "HOLD_PENDING" ? <span className="muted"> · pagable desde {formatDateTime(supplier.payableFrom)}</span> : null}
          </p>
        ) : null}
      </div>
      {data === null ? (
        <Loading error={error} />
      ) : !supplier ? (
        <div className="notice" data-testid="proposal-empty">
          <p style={{ margin: 0 }}>
            {shortcut === "all" ? (
              "No hay facturas contabilizadas con saldo pendiente de pago."
            ) : (
              <>
                No hay facturas con saldo que venzan hasta el {formatDate(dueUntil)}.{" "}
                {laterText ? laterText : later.data ? "Tampoco hay facturas que venzan después." : null}
              </>
            )}
          </p>
          {laterText ? (
            <button type="button" onClick={() => setDueUntil(ALL_DUE)}>
              Ver todas
            </button>
          ) : null}
        </div>
      ) : (
        <PrepareForm key={`${supplier.supplierId}:${dueUntil}`} supplier={supplier} today={today} />
      )}
    </>
  );
}
