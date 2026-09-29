"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { todayInDominicanRepublic } from "@/lib/labels";
import { METHODS } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10b (E-VS3-10-7, E-VS3-07-2): a receipt from an ACTIVE customer. A transfer names our bank account (masked list, E-VS3-10-14)
// and its value date; a cheque its bank, number and date; cash nothing else. It is applied to invoices on its page.

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

export default function Page() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const record = useCommand<"/api/v1/companies/{companyId}/sales/record-receipt", Values>("record-receipt", "/api/v1/companies/{companyId}/sales/record-receipt");
  const today = todayInDominicanRepublic();
  const [values, setValues] = useState<Values>(
    () => record.restored ?? { partyId: "", method: "TRANSFER", amount: "", valueDate: today, bankAccountId: "", reference: "", chequeBank: "", chequeNo: "", chequeDate: today },
  );
  const [invalid, setInvalid] = useState<string | null>(null);
  const allowed = can("receipt:record");
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
    return <Loading error={error} />;
  }
  const set = (key: keyof Values) => (e: { target: { value: string } }) => setValues({ ...values, [key]: e.target.value });
  const bank = values.bankAccountId || data.banks[0]?.bankAccountId || "";
  return (
    <>
      <h1>Registrar cobro</h1>
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          const amount = normalizeInput(values.amount);
          if (!values.partyId || !isPositiveDecimal(amount, 2)) {
            setInvalid("Elija el cliente y un monto mayor que cero (hasta 2 decimales).");
            return;
          }
          if (values.method === "TRANSFER" && !bank) {
            setInvalid("Una transferencia necesita la cuenta bancaria a la que llegó.");
            return;
          }
          setInvalid(null);
          const optional = (v: string) => (v.trim() === "" ? null : v.trim());
          const response = await record.run(
            {
              partyId: values.partyId,
              method: values.method,
              amount,
              valueDate: values.method === "TRANSFER" ? values.valueDate : null,
              bankAccountId: values.method === "TRANSFER" ? bank : null,
              reference: optional(values.reference),
              chequeBank: values.method === "CHEQUE" ? optional(values.chequeBank) : null,
              chequeNo: values.method === "CHEQUE" ? optional(values.chequeNo) : null,
              chequeDate: values.method === "CHEQUE" ? values.chequeDate : null,
            },
            values,
          );
          if (response) {
            router.push(`/cobros/recibo/?id=${response.resultRef}`);
          }
        }}
      >
        <Field label="Cliente">
          <select aria-label="Cliente" value={values.partyId} onChange={set("partyId")}>
            <option value="">—</option>
            {data.customers.map((c) => (
              <option key={c.partyId} value={c.partyId}>
                {c.legalName} ({c.rnc})
              </option>
            ))}
          </select>
        </Field>
        <Field label="Medio">
          <select aria-label="Medio de cobro" value={values.method} onChange={set("method")}>
            {Object.entries(METHODS).map(([code, label]) => (
              <option key={code} value={code}>
                {label}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Monto">
          <input aria-label="Monto del cobro" inputMode="decimal" value={values.amount} onChange={set("amount")} />
        </Field>
        {values.method === "TRANSFER" ? (
          <>
            <Field label="Cuenta de la empresa">
              <select aria-label="Cuenta bancaria" value={bank} onChange={set("bankAccountId")}>
                {data.banks.map((b) => (
                  <option key={b.bankAccountId} value={b.bankAccountId}>
                    {b.bankCode} {b.accountNumber}
                  </option>
                ))}
              </select>
            </Field>
            <Field label="Fecha valor">
              <input type="date" value={values.valueDate} max={today} onChange={set("valueDate")} />
            </Field>
          </>
        ) : null}
        {values.method === "CHEQUE" ? (
          <>
            <Field label="Banco del cheque">
              <input value={values.chequeBank} onChange={set("chequeBank")} required />
            </Field>
            <Field label="Número del cheque">
              <input value={values.chequeNo} onChange={set("chequeNo")} required />
            </Field>
            <Field label="Fecha del cheque">
              <input type="date" value={values.chequeDate} max={today} onChange={set("chequeDate")} />
            </Field>
          </>
        ) : null}
        <Field label="Referencia (opcional)">
          <input value={values.reference} onChange={set("reference")} />
        </Field>
        <button type="submit" className="primary" disabled={record.busy}>
          Registrar cobro
        </button>
        {invalid ? <div className="error">{invalid}</div> : null}
        <ErrorBox error={record.error} />
      </form>
    </>
  );
}
