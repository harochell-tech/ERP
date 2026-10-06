"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmAction, ErrorBox, Field, Loading, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { ITEM_CATEGORIES, itemCategoryLabel } from "@/lib/fiscalRuleForm";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { SearchSelect } from "@/components/SearchSelect";

type RoleMap = Schemas["AccountRoleMapView"];
type AccountRole = Schemas["AccountRoleView"];
type Account = Schemas["AccountView"];

// E-B03-15-2 / UX2-02 (E-UX2-5, 6): account role maps by the role's name. The Contador or the Controller prepares a DRAFT mapping on
// screen (account_role_map:prepare, E-UX2-6 b) — the CLI import stays — and someone else approves it (four eyes, step-up).

function roleName(roles: readonly AccountRole[], code: string, fallback?: string | null): string {
  return roles.find((r) => r.roleCode === code)?.name ?? fallback ?? code;
}

function PrepareMap({ roles, accounts, onDone }: { roles: readonly AccountRole[]; accounts: readonly Account[]; onDone: () => void }) {
  const prepare = useCommand("prepare-account-role-map", "/api/v1/companies/{companyId}/finance/prepare-account-role-map");
  const [accountRole, setAccountRole] = useState("");
  const [itemCategory, setItemCategory] = useState("");
  const [accountId, setAccountId] = useState("");
  const [effectiveFrom, setEffectiveFrom] = useState(todayInDominicanRepublic());
  const fe = useFieldErrors<"accountRole" | "accountId" | "effectiveFrom">();
  const role = roles.find((r) => r.roleCode === accountRole);
  // A control role posts to a control account and a regular role to a regular account (MAP_ACCOUNT_INVALID otherwise).
  const offered = accounts.filter((a) => a.status === "ACTIVE" && (!role || a.isControl === role.isControl));
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (
          !fe.check({
            accountRole: !accountRole && "Elija el rol contable.",
            accountId: !accountId && "Elija la cuenta.",
            effectiveFrom: !effectiveFrom && "Indique desde cuándo rige.",
          })
        ) {
          return;
        }
        const account = accounts.find((a) => a.accountId === accountId);
        const text = `Mapa ${roleName(roles, accountRole)}${itemCategory ? ` (${itemCategoryLabel(itemCategory)})` : ""} → ${account?.code ?? ""} guardado en borrador; falta su aprobación.`;
        if (await prepare.run({ accountRole, itemCategory: itemCategory || null, accountId, effectiveFrom }, undefined, text)) {
          setAccountRole("");
          setItemCategory("");
          setAccountId("");
          onDone();
        }
      }}
    >
      <h2 style={{ marginTop: 0 }}>Preparar mapa</h2>
      <Field label="Rol contable" required error={fe.errors.accountRole} hint={role?.description}>
        <select
          aria-label="Rol contable"
          value={accountRole}
          onChange={(e) => {
            setAccountRole(e.target.value);
            setAccountId("");
          }}
        >
          <option value="">Elegir…</option>
          {roles.map((r) => (
            <option key={r.roleCode} value={r.roleCode}>
              {r.name ?? r.roleCode}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Categoría de artículo" hint="Opcional: vacía aplica a todas las categorías.">
        <select aria-label="Categoría de artículo" value={itemCategory} onChange={(e) => setItemCategory(e.target.value)}>
          <option value="">Todas</option>
          {ITEM_CATEGORIES.map((c) => (
            <option key={c.code} value={c.code}>
              {c.label}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Cuenta" required error={fe.errors.accountId} hint={role ? (role.isControl ? "Rol de control: solo cuentas de control." : "Solo cuentas que no son de control.") : undefined}>
        <SearchSelect
          aria-label="Cuenta"
          value={accountId}
          onChange={setAccountId}
          options={offered.map((a) => ({ value: a.accountId, label: `${a.code} — ${a.name}` }))}
        />
      </Field>
      <Field label="Vigente desde" required error={fe.errors.effectiveFrom}>
        <input type="date" value={effectiveFrom} onChange={(e) => setEffectiveFrom(e.target.value)} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={prepare.busy}>
          Guardar borrador
        </button>
      </div>
      <ErrorBox error={prepare.error} />
    </form>
  );
}

function MapRow({ map, roles, onDone }: { map: RoleMap; roles: readonly AccountRole[]; onDone: () => void }) {
  const { can, isMine } = useSession();
  const name = roleName(roles, map.accountRole, map.accountRoleName);
  const approve = useCommand(`approve-map:${map.mapId}`, "/api/v1/companies/{companyId}/finance/approve-account-role-map", `Mapa ${name} → ${map.accountCode} aprobado y activo.`);
  // Four eyes: the preparer is not offered the approval (the database refuses it anyway).
  const preparedByMe = isMine(map.preparedBy);
  return (
    <tr>
      <td className="wrap">{name}</td>
      <td>{map.itemCategory ? itemCategoryLabel(map.itemCategory) : "Todas"}</td>
      <td className="wrap">
        {map.accountCode} — {map.accountName}
      </td>
      <td>
        {formatDate(map.effectiveFrom)}
        {map.effectiveTo ? ` – ${formatDate(map.effectiveTo)}` : ""}
      </td>
      <td>
        <StatusBadge status={map.status} />
      </td>
      <td className="wrap">{map.preparedBy ?? "Despliegue"}</td>
      <td className="wrap">{map.approvedBy ?? "—"}</td>
      <td>
        {map.status === "DRAFT" && can("account_role_map:approve") && !preparedByMe ? (
          <ConfirmAction
            label="Aprobar"
            title={`¿Aprobar el mapa de ${name}?`}
            stepUp
            busy={approve.busy}
            consequence={`${name}${map.itemCategory ? ` (${itemCategoryLabel(map.itemCategory)})` : ""} se contabilizará en la cuenta ${map.accountCode} — ${map.accountName} desde ${formatDate(map.effectiveFrom)}; el mapa activo anterior del mismo rol termina ese día. No se puede volver a borrador.`}
            onConfirm={async () => (await approve.run({ mapId: map.mapId })) && onDone()}
          />
        ) : null}
        <ErrorBox error={approve.error} />
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("configuration:read");
  const maps = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/finance/account-role-maps", { path: { companyId } }) : null, [companyId]);
  const accounts = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/finance/accounts", { path: { companyId } }) : null, [companyId]);
  const roles = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/finance/account-roles", { path: { companyId } }) : null, [companyId]);

  if (!allowed) {
    return <NoPermission />;
  }
  const roleItems = roles.data?.items ?? [];
  const unmapped = roleItems.filter((r) => r.usedByActiveRule && !r.mappedToday);
  const reload = () => {
    maps.reload();
    roles.reload();
  };
  return (
    <>
      <h1>Cuentas por rol</h1>
      <p className="muted">Cada rol contable de las reglas se contabiliza en la cuenta que le asigna su mapa. Un mapa nuevo se prepara en borrador y lo aprueba otra persona.</p>
      {unmapped.length > 0 ? (
        <div className="alert-block" role="status" data-testid="unmapped-roles">
          <strong>Roles sin cuenta</strong>
          Una regla contable activa usa estos roles y hoy no tienen un mapa activo; lo que los necesite no se contabiliza:
          <ul>
            {unmapped.map((r) => (
              <li key={r.roleCode}>{r.name ?? r.roleCode}</li>
            ))}
          </ul>
        </div>
      ) : null}
      {can("account_role_map:prepare") && roles.data && accounts.data ? <PrepareMap roles={roleItems} accounts={accounts.data.items} onDone={reload} /> : null}
      {maps.data === null || roles.data === null ? (
        <Loading error={maps.error ?? roles.error} />
      ) : maps.data.items.length === 0 ? (
        <p className="muted">No hay mapas.</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Rol contable</th>
                <th>Categoría</th>
                <th>Cuenta</th>
                <th>Vigencia</th>
                <th>Estado</th>
                <th>Preparado por</th>
                <th>Aprobado por</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {maps.data.items.map((m) => (
                <MapRow key={`${m.mapId}:${m.status}`} map={m} roles={roleItems} onDone={reload} />
              ))}
            </tbody>
          </table>
        </div>
      )}
      <h2>Roles contables</h2>
      {roles.data === null ? (
        <Loading error={roles.error} />
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Rol</th>
                <th>Para qué</th>
                <th>Tipo</th>
                <th>Hoy</th>
              </tr>
            </thead>
            <tbody>
              {roleItems.map((r) => (
                <tr key={r.roleCode}>
                  <td className="wrap">{r.name ?? r.roleCode}</td>
                  <td className="wrap muted">{r.description}</td>
                  <td>{r.isControl ? "Control" : "Normal"}</td>
                  <td>{r.mappedToday ? "Con cuenta" : r.usedByActiveRule ? <span className="badge tone-attention">Sin cuenta</span> : <span className="muted">Sin uso</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
