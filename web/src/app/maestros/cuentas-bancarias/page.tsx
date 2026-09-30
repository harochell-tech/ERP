"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission, ReasonAction, StatusBadge, useFieldErrors } from "@/components/ui";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { bankAccountLabel } from "@/lib/ux4a";

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
  const fe = useFieldErrors<"bankCode" | "accountNumber" | "glAccountCode">();
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (
          !fe.check({
            bankCode: bankCode.trim() === "" && "Indique el código del banco.",
            accountNumber: !/^\d{5,30}$/.test(accountNumber.trim()) && "Indique el número de cuenta (5 a 30 dígitos).",
            glAccountCode: glAccountCode === "" && "Elija la cuenta contable de control.",
          })
        ) {
          return;
        }
        if (await register.run({ bankCode: bankCode.trim(), accountNumber: accountNumber.trim(), glAccountCode }, undefined, `Cuenta bancaria ${bankAccountLabel({ bankCode: bankCode.trim(), accountNumber: accountNumber.trim() })} registrada.`)) {
          setBankCode("");
          setAccountNumber("");
          setGlAccountCode("");
          onDone();
        }
      }}
    >
      <h2>Registrar cuenta de la empresa</h2>
      <Field label="Banco (código)" required error={fe.errors.bankCode}>
        <input value={bankCode} onChange={(e) => setBankCode(e.target.value)} />
      </Field>
      <Field label="Número de cuenta" required error={fe.errors.accountNumber}>
        <input className="mono" inputMode="numeric" value={accountNumber} onChange={(e) => setAccountNumber(e.target.value)} />
      </Field>
      <Field label="Cuenta contable de control" required error={fe.errors.glAccountCode}>
        <select value={glAccountCode} onChange={(e) => setGlAccountCode(e.target.value)}>
          <option value="">Elegir…</option>
          {controls.map((a) => (
            <option key={a.accountId} value={a.code}>
              {a.code} — {a.name}
            </option>
          ))}
        </select>
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={register.busy}>
          Registrar cuenta
        </button>
      </div>
      <ErrorBox error={register.error} />
    </form>
  );
}

/** Most characters an alias may have (E-UX4-6; the server refuses more with BANK_ACCOUNT_ALIAS_INVALID). */
const ALIAS_MAX = 60;

// C-27 / E-UX4-6: the Controller names the account ("Operativa", "Nómina") so every screen reads "alias · banco ••••6789". It only
// labels the account: no step-up; a blank alias clears it.
function AliasEditor({ account, onDone }: { account: BankAccount; onDone: () => void }) {
  const setAlias = useCommand(`set-bank-account-alias:${account.bankAccountId}`, "/api/v1/companies/{companyId}/treasury/set-bank-account-alias");
  const [open, setOpen] = useState(false);
  const [alias, setAliasText] = useState(account.alias ?? "");
  const fe = useFieldErrors<"alias">();
  const number = bankAccountLabel({ bankCode: account.bankCode, accountNumber: account.accountNumber });
  if (!open) {
    return (
      <button
        type="button"
        onClick={() => {
          setAliasText(account.alias ?? "");
          setOpen(true);
        }}
      >
        {account.alias ? "Cambiar alias" : "Poner alias"}
      </button>
    );
  }
  return (
    <form
      className="inline-form"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const text = alias.trim();
        if (!fe.check({ alias: text.length > ALIAS_MAX && `Máximo ${ALIAS_MAX} caracteres.` })) {
          return;
        }
        const message = text === "" ? `Alias de la cuenta ${number} quitado.` : `Cuenta ${number} ahora se llama «${text}».`;
        if (await setAlias.run({ bankAccountId: account.bankAccountId, expectedVersion: account.version, alias: text === "" ? null : text }, undefined, message)) {
          setOpen(false);
          onDone();
        }
      }}
    >
      <Field label={`Alias de ${number}`} error={fe.errors.alias} hint="Ej. Operativa, Nómina. Vacío quita el alias.">
        <input value={alias} maxLength={ALIAS_MAX + 20} onChange={(e) => setAliasText(e.target.value)} />
      </Field>
      <button type="submit" className="primary" disabled={setAlias.busy}>
        Guardar alias
      </button>
      <button type="button" onClick={() => setOpen(false)}>
        Cancelar
      </button>
      <ErrorBox error={setAlias.error} />
    </form>
  );
}

function Row({ account, onDone }: { account: BankAccount; onDone: () => void }) {
  const { can } = useSession();
  const label = bankAccountLabel(account);
  const close = useCommand(`close-bank-account:${account.bankAccountId}`, "/api/v1/companies/{companyId}/treasury/close-bank-account", `Cuenta bancaria ${label} cerrada.`);
  return (
    <tr>
      <td className="wrap">
        <span className="mono" data-testid="bank-account-label">
          {label}
        </span>
      </td>
      <td>{account.currency}</td>
      <td className="wrap">
        {account.glAccountCode} — {account.glAccountName}
      </td>
      <td>
        <StatusBadge status={account.status} />
      </td>
      <td>
        {account.status === "ACTIVE" && can("bank_account:manage") ? <AliasEditor account={account} onDone={onDone} /> : null}{" "}
        {account.status === "ACTIVE" && can("bank_account:manage") ? (
          <ReasonAction
            label="Cerrar cuenta"
            consequence={`La cuenta ${label} quedará cerrada: no admitirá más pagos ni extractos, y no se puede reabrir.`}
            stepUp
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
  const [registering, setRegistering] = useState(false);
  if (!can("bank:read")) {
    return <NoPermission />;
  }
  return (
    <>
      {/* C-30: the create action beside the title, as on Pagos. */}
      <div className="actions" style={{ justifyContent: "space-between" }}>
        <h1>Cuentas bancarias de la empresa</h1>
        {can("bank_account:manage") && !registering ? (
          <button type="button" className="primary" onClick={() => setRegistering(true)}>
            Registrar cuenta de la empresa
          </button>
        ) : null}
      </div>
      {can("bank_account:manage") && registering ? (
        <>
          <RegisterForm
            onDone={() => {
              setRegistering(false);
              reload();
            }}
          />
          <div className="actions">
            <button type="button" onClick={() => setRegistering(false)}>
              Cancelar
            </button>
          </div>
        </>
      ) : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay cuentas bancarias registradas.</p>
      ) : (
        <div className="table-wrap"><table>
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
        </table></div>
      )}
    </>
  );
}
