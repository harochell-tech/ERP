"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { Person } from "@/components/Person";
import { ConfirmAction, ErrorBox, Field, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { matchesSearch } from "@/lib/ux4b";
import { personLabel } from "@/lib/identities";
import { formatDateTime, ROLES } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type User = Schemas["UserView"];
type Role = Schemas["RoleView"];

/** Roles offered in the request form (PROBADOR is for test databases only and is granted by the deployment CLI). */
const REQUESTABLE = Object.keys(ROLES).filter((code) => code !== "PROBADOR");

function userText(user: User): string {
  return personLabel(user.displayName, user.email, user.userId);
}

// UI-01 / E-UI01-4/5: users are created with the deployment CLI; here the security administrator requests company-wide role
// assignments and revocations (role:assign / role:revoke, step-up) and a second approver decides them (Solicitudes de rol).
// UX1-01b (E-UX1-01-3): people read "Name · e-mail" — the same text in the table and in the user select.
function RequestForm({ users, roles, onDone }: { users: User[]; roles: readonly Role[]; onDone: () => void }) {
  const request = useCommand("request-role", "/api/v1/companies/{companyId}/identity/request-role-assignment");
  const [userId, setUserId] = useState("");
  const [role, setRole] = useState("");
  const [sent, setSent] = useState(false);
  const fe = useFieldErrors<"userId" | "role">();
  // UX2-02 (E-UX2-12): the roles people may hold, from Identity.ListRoles, each with what it is for.
  const offered = roles.length > 0 ? roles.map((r) => r.code) : REQUESTABLE;
  const description = roles.find((r) => r.code === role)?.description;
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        setSent(false);
        if (!fe.check({ userId: !userId && "Elija el usuario.", role: !role && "Elija el rol." })) {
          return;
        }
        const user = users.find((u) => u.userId === userId);
        const roleName = ROLES[role] ?? role;
        if (
          await request.run({ targetUserId: userId, roleCode: role, plantId: null }, undefined, `Rol ${roleName} solicitado para ${user ? userText(user) : "el usuario"}; lo decide el segundo aprobador.`)
        ) {
          setSent(true);
          setRole("");
          onDone();
        }
      }}
    >
      <h2 style={{ marginTop: 0 }}>Solicitar un rol</h2>
      <Field label="Usuario" required error={fe.errors.userId}>
        <select value={userId} onChange={(e) => setUserId(e.target.value)}>
          <option value="">Elegir…</option>
          {users
            .filter((u) => u.status === "ACTIVE" && u.kind === "HUMAN")
            .map((u) => (
              <option key={u.userId} value={u.userId}>
                {userText(u)}
              </option>
            ))}
        </select>
      </Field>
      <Field label="Rol" required error={fe.errors.role} hint={description ?? undefined}>
        <select value={role} onChange={(e) => setRole(e.target.value)}>
          <option value="">Elegir…</option>
          {offered.map((code) => (
            <option key={code} value={code}>
              {ROLES[code] ?? roles.find((r) => r.code === code)?.name ?? code}
            </option>
          ))}
        </select>
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={request.busy}>
          Solicitar
        </button>
        {sent ? (
          <span className="muted">
            Solicitud enviada: la decide el segundo aprobador en <Link href="/seguridad/solicitudes/">Solicitudes de rol</Link>.
          </span>
        ) : null}
      </div>
      <p className="muted">Para dar de alta a una persona nueva o darle un rol en una sola planta, pídalo al equipo de sistemas.</p>
      <ErrorBox error={request.error} />
    </form>
  );
}

/**
 * UX4-03 (A-19): the revocation of one role, a quiet button beside that role (not a red button on every line) confirmed in a
 * dialog that says what follows: the second approver decides it in Solicitudes de rol.
 */
function RevokeButton({ user, roleCode, plantId, onDone }: { user: User; roleCode: string; plantId: string | null; onDone: () => void }) {
  const roleName = ROLES[roleCode] ?? roleCode;
  const revoke = useCommand(
    `revoke-role:${user.userId}:${roleCode}:${plantId ?? ""}`,
    "/api/v1/companies/{companyId}/identity/request-role-revocation",
    `Revocación del rol ${roleName} de ${userText(user)} solicitada.`,
  );
  return (
    <>
      <ConfirmAction
        label="Solicitar revocación"
        className="link"
        title={`¿Solicitar que se revoque el rol ${roleName} a ${userText(user)}?`}
        consequence="La solicitud queda pendiente: el segundo aprobador la decide en Solicitudes de rol, y hasta entonces la persona conserva el rol."
        stepUp
        busy={revoke.busy}
        testId={`revoke:${user.userId}:${roleCode}`}
        onConfirm={async () => {
          if (await revoke.run({ targetUserId: user.userId, roleCode, plantId })) {
            onDone();
          }
        }}
      />
      <ErrorBox error={revoke.error} />
    </>
  );
}

export default function Page() {
  const { companyId, can, plantName } = useSession();
  const { data, error, reload } = useLoad(can("iam:read") ? () => query("/api/v1/companies/{companyId}/identity/users", { path: { companyId } }) : null, [companyId]);
  const roles = useLoad(can("iam:read") ? () => query("/api/v1/companies/{companyId}/identity/roles", { path: { companyId } }) : null, [companyId]);
  const roleItems = roles.data?.items ?? [];
  const describe = (code: string) => roleItems.find((r) => r.code === code)?.description ?? undefined;
  const [search, setSearch] = useState("");
  const [roleFilter, setRoleFilter] = useState("");
  if (!can("iam:read")) {
    return <NoPermission />;
  }
  // UX4-03 (A-19): search by name or e-mail and filter by role ("Sin roles" finds the people still waiting for one).
  const heldRoles = [...new Set((data?.items ?? []).flatMap((u) => u.roles.map((r) => r.roleCode)))].sort((a, b) => (ROLES[a] ?? a).localeCompare(ROLES[b] ?? b, "es"));
  const shown = (data?.items ?? []).filter(
    (u) =>
      matchesSearch(search, u.displayName, u.email) &&
      (roleFilter === "" || (roleFilter === "NONE" ? u.roles.length === 0 : u.roles.some((r) => r.roleCode === roleFilter))),
  );
  return (
    <>
      <h1>Usuarios y roles</h1>
      {data && can("role:assign") ? <RequestForm users={data.items} roles={roleItems} onDone={reload} /> : null}
      <div className="inline-form" role="search">
        <Field label="Buscar persona">
          <input type="search" placeholder="Nombre o correo" value={search} onChange={(e) => setSearch(e.target.value)} />
        </Field>
        <Field label="Con el rol">
          <select value={roleFilter} onChange={(e) => setRoleFilter(e.target.value)}>
            <option value="">Todos</option>
            <option value="NONE">Sin roles</option>
            {heldRoles.map((code) => (
              <option key={code} value={code}>
                {ROLES[code] ?? code}
              </option>
            ))}
          </select>
        </Field>
      </div>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : shown.length === 0 ? (
        <EmptyState title="Nadie coincide con la búsqueda.">
          <p>Pruebe con otra parte del nombre o del correo, o elija «Todos» los roles.</p>
        </EmptyState>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Nombre · correo</th>
              <th>Tipo</th>
              <th>Estado</th>
              <th>Roles vigentes</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((u) => (
              <tr key={u.userId}>
                <td className="wrap">
                  <Person name={u.displayName} email={u.email} fallback={u.userId} />
                </td>
                <td>{u.kind === "SYNTHETIC" ? "Identidad de prueba" : "Persona"}</td>
                <td>
                  <StatusBadge status={u.status} />
                </td>
                <td className="wrap">
                  {u.roles.length === 0 ? (
                    <span className="muted">Sin roles</span>
                  ) : (
                    u.roles.map((r) => (
                      <div key={r.assignmentId} className="inline-form" style={{ marginBottom: 4 }}>
                        <span title={describe(r.roleCode)}>
                          {ROLES[r.roleCode] ?? r.roleName}
                          {r.plantId || r.plantCode ? ` (planta ${plantName(r.plantId ?? r.plantCode, r.plantCode ?? undefined)})` : ""}{" "}
                          <span className="muted">desde {formatDateTime(r.validFrom)}</span>
                        </span>
                        {can("role:revoke") ? <RevokeButton user={u} roleCode={r.roleCode} plantId={r.plantId} onDone={reload} /> : null}
                      </div>
                    ))
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
      <h2>Roles y para qué sirven</h2>
      {roles.data === null ? (
        <LoadingIndicator error={roles.error} />
      ) : (
        <div className="table-wrap">
          <table data-testid="role-catalogue">
            <thead>
              <tr>
                <th>Rol</th>
                <th>Para qué sirve</th>
              </tr>
            </thead>
            <tbody>
              {roleItems.map((r) => (
                <tr key={r.code}>
                  <td>{ROLES[r.code] ?? r.name}</td>
                  <td className="wrap">{r.description ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
