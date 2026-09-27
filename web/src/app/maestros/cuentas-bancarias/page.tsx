"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type BankAccount = Schemas["BankAccountView"];

// VS2-08: the company's own bank accounts (E-VS2-1). The Controller registers them on their own GL control account and closes
// them with a reason (both with step-up; closing is refused with open payments or unmatched lines, E-VS2-02-3).
function RegisterForm({ onDone }: { onDone: () => void }) {
  const { companyId, can } = useSession();
  const register = useCommand("register-bank-account", "/api/v1/companies/{companyId}/treasury/register-bank-account");
  const { data: accounts } = useLoad(
    can("configuration:read") ? () => query("/api/v1/companies/{companyId}/finance/accounts", { path: { companyId } }) : null,
    [companyId],
  );
  const [bankCode, setBankCode] = useState("");
  const [accountNumber, setAccountNumber] = useState("");
  const [glAccountCode, setGlAccountCode] = useState("");
  const controls = (accounts?.items ?? []).filter((a) => a.isControl);
  return (
    <form
      className="card"
      onSubmit={async (e) => {
        e.preventDefault();
        if (await register.run({ bankCode: bankCode.trim(), accountNumber: accountNumber.trim(), glAccountCode })) {
          setBankCode("");
          setAccountNumber("");
          setGlAccountCode("");
          onDone();
        }
      }}
    >
      <h2 style={{ marginTop: 0 }}>Registrar cuenta de la empresa</h2>
      <Field label="Banco (código)">
        <input value={bankCode} onChange={(e) => setBankCode(e.target.value)} required />
      </Field>
      <Field label="Número de cuenta">
        <input className="mono" value={accountNumber} onChange={(e) => setAccountNumber(e.target.value)} required />
      </Field>
      <Field label="Cuenta contable de control">
        <select value={glAccountCode} onChange={(e) => setGlAccountCode(e.target.value)} required>
          <option value="">Elegir…</option>
          {controls.map((a) => (
            <option key={a.accountId} value={a.code}>
              {a.code} — {a.name}
            </option>
          ))}
        </select>
      </Field>
      <div className="actions">
        <button type="submit" className="primary" disabled={register.busy}>
          Registrar cuenta
        </button>
      </div>
      <ErrorBox error={register.error} />
    </form>
  );
}

function Row({ account, onDone }: { account: BankAccount; onDone: () => void }) {
  const { can } = useSession();
  const close = useCommand(`close-bank-account:${account.bankAccountId}`, "/api/v1/companies/{companyId}/treasury/close-bank-account");
  return (
    <tr>
      <td className="mono">
        {account.bankCode} {account.accountNumber}
      </td>
      <td>{account.currency}</td>
      <td>
        {account.glAccountCode} — {account.glAccountName}
      </td>
      <td>
        <StatusBadge status={account.status} />
      </td>
      <td>
        {account.status === "ACTIVE" && can("bank_account:manage") ? (
          <ReasonAction
            label="Cerrar cuenta"
            busy={close.busy}
            onConfirm={async (reason) => {
              if (await close.run({ bankAccountId: account.bankAccountId, expectedVersion: account.version, reason })) {
                onDone();
              }
            }}
          />
        ) : null}
        <ErrorBox error={close.error} />
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const { data, error, reload } = useLoad(
    can("bank:read") ? () => query("/api/v1/companies/{companyId}/treasury/bank-accounts", { path: { companyId } }) : null,
    [companyId],
  );
  if (!can("bank:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Cuentas bancarias de la empresa</h1>
      {can("bank_account:manage") ? <RegisterForm onDone={reload} /> : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay cuentas bancarias registradas.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Banco y cuenta</th>
              <th>Moneda</th>
              <th>Cuenta contable</th>
              <th>Estado</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((a) => (
              <Row key={a.bankAccountId} account={a} onDone={reload} />
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
