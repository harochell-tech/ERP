"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmAction, ErrorBox, Field, Loading, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { ACCOUNT_CLASSES, accountClassLabel } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { CONTROL_ACCOUNT_HELP, isZeroDecimal } from "@/lib/ux4a-contabilidad";

type Account = Schemas["AccountView"];

function ClassSelect({ value, onChange, label }: { value: string; onChange: (value: string) => void; label: string }) {
  return (
    <select aria-label={label} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">—</option>
      {Object.entries(ACCOUNT_CLASSES).map(([code, name]) => (
        <option key={code} value={code}>
          {name}
        </option>
      ))}
    </select>
  );
}

/** E-FIN1-04-3: the Controller renames an account, sets its class and activates or deactivates it (refused with a balance). */
function AccountRow({ account, onDone }: { account: Account; onDone: () => void }) {
  const { can } = useSession();
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(account.name);
  const [accountClass, setAccountClass] = useState(account.accountClass ?? "");
  const update = useCommand(`update-account:${account.accountId}`, "/api/v1/companies/{companyId}/finance/update-account", `Cuenta ${account.code} actualizada.`);
  const deactivate = useCommand(`deactivate-account:${account.accountId}`, "/api/v1/companies/{companyId}/finance/deactivate-account", `Cuenta ${account.code} desactivada.`);
  const activate = useCommand(`activate-account:${account.accountId}`, "/api/v1/companies/{companyId}/finance/activate-account", `Cuenta ${account.code} activada.`);
  const busy = update.busy || deactivate.busy || activate.busy;
  const manage = can("account:manage");
  const after = (response: unknown) => {
    if (response) {
      setEditing(false);
      onDone();
    }
  };
  return (
    <tr>
      <td className="mono">{account.code}</td>
      <td>
        {editing ? <input aria-label={`Nombre de ${account.code}`} value={name} maxLength={200} onChange={(e) => setName(e.target.value)} /> : account.name}
        <ErrorBox error={update.error ?? deactivate.error ?? activate.error} />
      </td>
      <td>
        {editing ? (
          <ClassSelect label={`Clase de ${account.code}`} value={accountClass} onChange={setAccountClass} />
        ) : account.accountClass ? (
          accountClassLabel(account.accountClass)
        ) : (
          <span className="badge tone-attention">Sin clasificar</span>
        )}
      </td>
      <td>
        {account.isControl ? (
          <span title={CONTROL_ACCOUNT_HELP}>De control</span>
        ) : (
          "Normal"
        )}
      </td>
      <td className="num">
        <Money value={account.balance} />
      </td>
      <td>
        <StatusBadge status={account.status} label={account.status === "ACTIVE" ? "Activa" : "Inactiva"} />
      </td>
      {manage ? (
        <td>
          <span className="inline-form">
            {editing ? (
              <>
                <button type="button" className="primary" disabled={busy || !name.trim() || !accountClass} onClick={async () => after(await update.run({ accountId: account.accountId, name: name.trim(), accountClass }))}>
                  Guardar
                </button>
                <button type="button" onClick={() => setEditing(false)}>
                  Cancelar
                </button>
              </>
            ) : (
              <button type="button" onClick={() => setEditing(true)}>
                Editar
              </button>
            )}
            {account.status === "ACTIVE" && !isZeroDecimal(account.balance) ? (
              <span className="muted" data-testid={`no-deactivate-${account.code}`}>
                Tiene saldo: no se puede desactivar hasta llevarlo a cero.
              </span>
            ) : account.status === "ACTIVE" ? (
              <ConfirmAction
                label="Desactivar"
                title={`¿Desactivar la cuenta ${account.code}?`}
                danger
                busy={busy}
                consequence={`La cuenta ${account.code} ${account.name} deja de aceptar movimientos y ajustes; se puede volver a activar.`}
                onConfirm={async () => after(await deactivate.run({ accountId: account.accountId }))}
              />
            ) : (
              <button type="button" disabled={busy} onClick={async () => after(await activate.run({ accountId: account.accountId }))}>
                Activar
              </button>
            )}
          </span>
        </td>
      ) : null}
    </tr>
  );
}

function NewAccount({ onDone }: { onDone: () => void }) {
  const [code, setCode] = useState("");
  const create = useCommand("create-account", "/api/v1/companies/{companyId}/finance/create-account", (_, doc) => `Cuenta ${doc ?? code.trim()} creada.`);
  const [name, setName] = useState("");
  const [accountClass, setAccountClass] = useState("");
  const [isControl, setIsControl] = useState(false);
  const fe = useFieldErrors<"code" | "name" | "accountClass">();
  return (
    <div className="card">
      <strong>Nueva cuenta</strong>
      <div>
        <Field label="Código" required error={fe.errors.code}>
          <input aria-label="Código de la cuenta" maxLength={20} value={code} onChange={(e) => setCode(e.target.value)} />
        </Field>
        <Field label="Nombre" required error={fe.errors.name}>
          <input aria-label="Nombre de la cuenta" maxLength={200} value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Clase" required error={fe.errors.accountClass}>
          <ClassSelect label="Clase de la cuenta" value={accountClass} onChange={setAccountClass} />
        </Field>
        <label className="field">
          <span>Cuenta de control</span>
          <span>
            <input type="checkbox" checked={isControl} onChange={(e) => setIsControl(e.target.checked)} /> Se mueve solo por documentos
          </span>
          <span className="field-hint">{CONTROL_ACCOUNT_HELP} No se puede cambiar después de crearla.</span>
        </label>
      </div>
      <div className="actions form-actions">
        <button
          type="button"
          className="primary"
          disabled={create.busy}
          onClick={async () => {
            const valid = fe.check({
              code: !code.trim() && "Indique el código de la cuenta.",
              name: !name.trim() && "Indique el nombre de la cuenta.",
              accountClass: !accountClass && "Elija la clase de la cuenta.",
            });
            if (valid && (await create.run({ code: code.trim(), name: name.trim(), accountClass, isControl }))) {
              setCode("");
              setName("");
              setAccountClass("");
              setIsControl(false);
              onDone();
            }
          }}
        >
          Crear cuenta
        </button>
      </div>
      <ErrorBox error={create.error} />
    </div>
  );
}

// UI-01, FIN1-04 (E-FIN1-3/4/7, E-FIN1-04-3): the chart of accounts with class and status; the Controller maintains it.
// UX4-02 (A-16, E-UX4-2): the server's balance per account; "Desactivar" only at zero; control accounts and "sin clasificar"
// explained; the control mark cannot change after creation.
export default function Page() {
  const { companyId, can } = useSession();
  const [onlyUnclassed, setOnlyUnclassed] = useState(false);
  const { data, error, reload } = useLoad(can("configuration:read") ? () => query("/api/v1/companies/{companyId}/finance/accounts", { path: { companyId } }) : null, [companyId]);
  if (!can("configuration:read")) {
    return <NoPermission />;
  }
  const rows = data ? data.items.filter((a) => !onlyUnclassed || a.accountClass === null) : [];
  return (
    <>
      <h1>Catálogo de cuentas</h1>
      <p className="muted">
        El código y la marca de control no cambian después de crear la cuenta. Una cuenta con saldo no se desactiva, y ninguna se borra.
      </p>
      <p className="muted">
        <strong>Cuentas de control:</strong> {CONTROL_ACCOUNT_HELP} Una cuenta <strong>sin clasificar</strong> no tiene clase (activo, pasivo, patrimonio,
        ingreso, costo o gasto) y bloquea los estados financieros hasta asignársela.
      </p>
      {can("account:manage") ? <NewAccount onDone={reload} /> : null}
      <label>
        <input type="checkbox" checked={onlyUnclassed} onChange={(e) => setOnlyUnclassed(e.target.checked)} /> Solo cuentas sin clasificar
      </label>
      {data === null ? (
        <Loading error={error} />
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Código</th>
              <th>Nombre</th>
              <th>Clase</th>
              <th>Tipo</th>
              <th className="num">Saldo (RD$)</th>
              <th>Estado</th>
              {can("account:manage") ? <th /> : null}
            </tr>
          </thead>
          <tbody>
            {rows.map((a) => (
              <AccountRow key={`${a.accountId}:${a.name}:${a.accountClass}:${a.status}`} account={a} onDone={reload} />
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
