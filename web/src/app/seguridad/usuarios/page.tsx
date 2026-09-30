"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { Person } from "@/components/Person";
import { ErrorBox, Field, Loading, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
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
      <p className="muted">Las asignaciones por planta y el alta de usuarios se hacen con la herramienta de despliegue.</p>
      <ErrorBox error={request.error} />
    </form>
  );
}

function RevokeButton({ user, roleCode, plantId, onDone }: { user: User; roleCode: string; plantId: string | null; onDone: () => void }) {
  const revoke = useCommand(
    `revoke-role:${user.userId}:${roleCode}:${plantId ?? ""}`,
    "/api/v1/companies/{companyId}/identity/request-role-revocation",
    `Revocación del rol ${ROLES[roleCode] ?? roleCode} de ${userText(user)} solicitada.`,
  );
  return (
    <>
      <button
        type="button"
        className="danger"
        disabled={revoke.busy}
        onClick={async () => {
          if (await revoke.run({ targetUserId: user.userId, roleCode, plantId })) {
            onDone();
          }
        }}
      >
        Solicitar revocación
      </button>
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
  if (!can("iam:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Usuarios y roles</h1>
      {data && can("role:assign") ? <RequestForm users={data.items} roles={roleItems} onDone={reload} /> : null}
      {data === null ? (
        <Loading error={error} />
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
            {data.items.map((u) => (
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
        <Loading error={roles.error} />
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
