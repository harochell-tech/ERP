"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatDateTime, ROLES } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type User = Schemas["UserView"];

/** Roles offered in the request form (PROBADOR is for test databases only and is granted by the deployment CLI). */
const REQUESTABLE = Object.keys(ROLES).filter((code) => code !== "PROBADOR");

// UI-01 / E-UI01-4/5: users are created with the deployment CLI; here the security administrator requests company-wide role
// assignments and revocations (role:assign / role:revoke, step-up) and a second approver decides them (Solicitudes de rol).
function RequestForm({ users, onDone }: { users: User[]; onDone: () => void }) {
  const request = useCommand("request-role", "/api/v1/companies/{companyId}/identity/request-role-assignment");
  const [userId, setUserId] = useState("");
  const [role, setRole] = useState("");
  const [sent, setSent] = useState(false);
  return (
    <form
      className="card"
      onSubmit={async (e) => {
        e.preventDefault();
        setSent(false);
        if (await request.run({ targetUserId: userId, roleCode: role, plantId: null })) {
          setSent(true);
          setRole("");
          onDone();
        }
      }}
    >
      <h2 style={{ marginTop: 0 }}>Solicitar un rol</h2>
      <Field label="Usuario">
        <select value={userId} onChange={(e) => setUserId(e.target.value)} required>
          <option value="">Elegir…</option>
          {users
            .filter((u) => u.status === "ACTIVE" && u.kind === "HUMAN")
            .map((u) => (
              <option key={u.userId} value={u.userId}>
                {u.email ?? u.userId}
              </option>
            ))}
        </select>
      </Field>
      <Field label="Rol">
        <select value={role} onChange={(e) => setRole(e.target.value)} required>
          <option value="">Elegir…</option>
          {REQUESTABLE.map((code) => (
            <option key={code} value={code}>
              {ROLES[code]}
            </option>
          ))}
        </select>
      </Field>
      <div className="actions">
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
  const revoke = useCommand(`revoke-role:${user.userId}:${roleCode}:${plantId ?? ""}`, "/api/v1/companies/{companyId}/identity/request-role-revocation");
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
  const { companyId, can } = useSession();
  const { data, error, reload } = useLoad(can("iam:read") ? () => query("/api/v1/companies/{companyId}/identity/users", { path: { companyId } }) : null, [companyId]);
  if (!can("iam:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Usuarios y roles</h1>
      {data && can("role:assign") ? <RequestForm users={data.items} onDone={reload} /> : null}
      {data === null ? (
        <Loading error={error} />
      ) : (
        <table>
          <thead>
            <tr>
              <th>Usuario</th>
              <th>Tipo</th>
              <th>Estado</th>
              <th>Roles vigentes</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((u) => (
              <tr key={u.userId}>
                <td>{u.email ?? u.userId}</td>
                <td>{u.kind === "SYNTHETIC" ? "Identidad de prueba" : "Persona"}</td>
                <td>
                  <StatusBadge status={u.status} />
                </td>
                <td>
                  {u.roles.length === 0 ? (
                    <span className="muted">Sin roles</span>
                  ) : (
                    u.roles.map((r) => (
                      <div key={r.assignmentId} className="inline-form" style={{ marginBottom: 4 }}>
                        <span>
                          {ROLES[r.roleCode] ?? r.roleName}
                          {r.plantCode ? ` (planta ${r.plantCode})` : ""} <span className="muted">desde {formatDateTime(r.validFrom)}</span>
                        </span>
                        {can("role:revoke") ? <RevokeButton user={u} roleCode={r.roleCode} plantId={r.plantId} onDone={reload} /> : null}
                      </div>
                    ))
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
