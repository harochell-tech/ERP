"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { Person } from "@/components/Person";
import { ConfirmAction, ErrorBox, Field, NoPermission, ReasonAction, StatusBadge, useFieldErrors } from "@/components/ui";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { matchesSearch } from "@/lib/ux4b";
import { personLabel } from "@/lib/identities";
import { formatDateTime, ROLES } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type User = Schemas["UserView"];
type Invitation = Schemas["InvitationView"];
type StatusRequest = Schemas["UserStatusRequestView"];
type Role = Schemas["RoleView"];

/** Roles offered in the request form (PROBADOR is for test databases only and is granted by the deployment CLI). */
const REQUESTABLE = Object.keys(ROLES).filter((code) => code !== "PROBADOR");

function userText(user: User): string {
  return personLabel(user.displayName, user.email, user.userId);
}

// UI-01 / E-UI01-4/5: the security administrator requests company-wide role
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
            .filter((u) => u.status === "ACTIVE" && (u.kind === "HUMAN" || u.kind === "GUEST"))
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
      <p className="muted">Para dar de alta a una persona nueva use «Invitar usuario».</p>
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

// SEC-02 (E-SEC-02-1…8): invitations — an e-mail, a name and roles, approved by a second person; the person enters with Google the
// first time with that exact e-mail. Outside the company's domain it is a guest: never a security role, and its roles end.
function InviteForm({ roles, onDone }: { roles: readonly Role[]; onDone: () => void }) {
  const invite = useCommand("invite-user", "/api/v1/companies/{companyId}/identity/invite-user");
  const { company, plantName } = useSession();
  const plants = company?.plants ?? [];
  const [email, setEmail] = useState("");
  const [name, setName] = useState("");
  const [chosen, setChosen] = useState<{ roleCode: string; plantId: string }[]>([{ roleCode: "", plantId: "" }]);
  const [until, setUntil] = useState("");
  const fe = useFieldErrors<"email" | "name" | "roles">();
  const offered = roles.length > 0 ? roles.map((r) => r.code) : REQUESTABLE;
  const setRow = (i: number, change: Partial<{ roleCode: string; plantId: string }>) => setChosen(chosen.map((r, j) => (j === i ? { ...r, ...change } : r)));
  return (
    <details className="card" data-testid="invite-user">
      <summary>
        <strong>Invitar usuario</strong>
      </summary>
      <form
        noValidate
        onSubmit={async (e) => {
          e.preventDefault();
          const picked = chosen.filter((r) => r.roleCode);
          if (!fe.check({ email: !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email.trim()) && "Escriba un correo válido.", name: !name.trim() && "Escriba el nombre de la persona.", roles: picked.length === 0 && "Elija al menos un rol." })) {
            return;
          }
          const sent = await invite.run(
            { email: email.trim(), displayName: name.trim(), roles: picked.map((r) => ({ roleCode: r.roleCode, plantId: r.plantId || null })), guestRolesUntil: until || null },
            undefined,
            `Invitación de ${name.trim()} creada: la aprueba el segundo aprobador y entonces sale el correo.`,
          );
          if (sent) {
            setEmail("");
            setName("");
            setChosen([{ roleCode: "", plantId: "" }]);
            setUntil("");
            onDone();
          }
        }}
      >
        <Field label="Correo (cuenta de Google)" required error={fe.errors.email} hint="Con @rochell.com.do entra como persona de la empresa; con otro dominio, como invitado.">
          <input type="email" aria-label="Correo de la invitación" value={email} onChange={(e) => setEmail(e.target.value)} />
        </Field>
        <Field label="Nombre" required error={fe.errors.name}>
          <input aria-label="Nombre del invitado" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        {chosen.map((r, i) => (
          <div className="inline-form" key={i}>
            <Field label={`Rol ${i + 1}`} required={i === 0} error={i === 0 ? fe.errors.roles : undefined}>
              <select aria-label={`Rol ${i + 1} de la invitación`} value={r.roleCode} onChange={(e) => setRow(i, { roleCode: e.target.value })}>
                <option value="">Elegir…</option>
                {offered.map((code) => (
                  <option key={code} value={code}>
                    {ROLES[code] ?? roles.find((x) => x.code === code)?.name ?? code}
                  </option>
                ))}
              </select>
            </Field>
            <Field label="Planta">
              <select aria-label={`Planta del rol ${i + 1}`} value={r.plantId} onChange={(e) => setRow(i, { plantId: e.target.value })}>
                <option value="">Toda la empresa</option>
                {plants.map((p) => (
                  <option key={p.plantId} value={p.plantId}>
                    {plantName(p.plantId, p.code)}
                  </option>
                ))}
              </select>
            </Field>
          </div>
        ))}
        <Field label="Fin de los roles (solo invitados de fuera)" hint="Si se deja vacío, los roles de un invitado duran 90 días.">
          <input type="date" aria-label="Fin de los roles del invitado" value={until} onChange={(e) => setUntil(e.target.value)} />
        </Field>
        <div className="actions form-actions">
          <button type="button" onClick={() => setChosen([...chosen, { roleCode: "", plantId: "" }])}>
            Agregar otro rol
          </button>
          <button type="submit" className="primary" disabled={invite.busy}>
            Invitar
          </button>
        </div>
        <p className="muted">
          La invitación vale 7 días desde que se aprueba. A los invitados de fuera se les pide la verificación en dos pasos de Google; para las cuentas de la empresa, actívela como
          obligatoria en la consola de Google Workspace.
        </p>
        <ErrorBox error={invite.error} />
      </form>
    </details>
  );
}

const INVITATION_STATES: Record<string, string> = {
  REQUESTED: "Pendiente de aprobación",
  APPROVED: "Enviada",
  ACCEPTED: "Aceptada",
  EXPIRED: "Vencida",
  REJECTED: "Rechazada",
  CANCELLED: "Anulada",
};

function InvitationActions({ invitation, onDone }: { invitation: Invitation; onDone: () => void }) {
  const { can } = useSession();
  const approve = useCommand(`approve-invitation:${invitation.invitationId}`, "/api/v1/companies/{companyId}/identity/approve-invitation", `Invitación de ${invitation.displayName} aprobada: el correo sale a ${invitation.email}.`);
  const reject = useCommand(`reject-invitation:${invitation.invitationId}`, "/api/v1/companies/{companyId}/identity/reject-invitation", "Invitación rechazada.");
  const cancel = useCommand(`cancel-invitation:${invitation.invitationId}`, "/api/v1/companies/{companyId}/identity/cancel-invitation", "Invitación anulada.");
  const open = invitation.state === "REQUESTED" || invitation.state === "APPROVED";
  return (
    <div className="inline-form">
      {invitation.status === "REQUESTED" && can("role:second_approve") ? (
        <ConfirmAction
          label="Aprobar"
          title={`¿Aprobar la invitación de ${invitation.displayName}?`}
          consequence={`Sale el correo a ${invitation.email}; la invitación vale 7 días.`}
          stepUp
          busy={approve.busy}
          testId={`approve-invitation:${invitation.email}`}
          onConfirm={async () => {
            if (await approve.run({ invitationId: invitation.invitationId })) {
              onDone();
            }
          }}
        />
      ) : null}
      {invitation.status === "REQUESTED" && can("role:second_approve") ? (
        <ReasonAction label="Rechazar" stepUp busy={reject.busy} consequence="La invitación no se envía." onConfirm={async (reason) => (await reject.run({ invitationId: invitation.invitationId, reason })) && onDone()} />
      ) : null}
      {open && invitation.state !== "EXPIRED" && can("role:assign") ? (
        <ReasonAction label="Anular" stepUp busy={cancel.busy} consequence="El correo ya no podrá entrar con esta invitación." onConfirm={async (reason) => (await cancel.run({ invitationId: invitation.invitationId, reason })) && onDone()} />
      ) : null}
      <ErrorBox error={approve.error ?? reject.error ?? cancel.error} />
    </div>
  );
}

function Invitations({ version }: { version: number }) {
  const { companyId } = useSession();
  const { data, error, reload } = useLoad(() => query("/api/v1/companies/{companyId}/identity/invitations", { path: { companyId } }), [companyId, version]);
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  if (data.items.length === 0) {
    return null;
  }
  return (
    <>
      <h2>Invitaciones</h2>
      <div className="table-wrap">
        <table data-testid="invitations">
          <thead>
            <tr>
              <th>Nombre · correo</th>
              <th>Tipo</th>
              <th>Roles</th>
              <th>Estado</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.items.map((i) => (
              <tr key={i.invitationId} data-testid={`invitation:${i.email}`}>
                <td className="wrap">
                  {i.displayName} · <span className="muted">{i.email}</span>
                </td>
                <td>{i.guest ? `Invitado hasta ${i.rolesValidTo ? formatDateTime(i.rolesValidTo) : "—"}` : "Persona de la empresa"}</td>
                <td className="wrap">{i.roles.map((r) => (ROLES[r.roleCode] ?? r.roleName) + (r.plantCode ? ` (${r.plantCode})` : "")).join(", ")}</td>
                <td>
                  {INVITATION_STATES[i.state] ?? i.state}
                  {i.state === "APPROVED" && i.expiresAt ? <span className="muted"> · vence {formatDateTime(i.expiresAt)}</span> : null}
                  {i.state === "ACCEPTED" && i.acceptedAt ? <span className="muted"> · entró {formatDateTime(i.acceptedAt)}</span> : null}
                </td>
                <td>
                  <InvitationActions invitation={i} onDone={reload} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

/** E-SEC-02-7: disabling cuts the person's access at once; enabling gives it back. Both are decided by the second approver. */
function StatusButton({ user, onDone }: { user: User; onDone: () => void }) {
  const disable = user.status === "ACTIVE";
  const request = useCommand(`user-status:${user.userId}`, "/api/v1/companies/{companyId}/identity/request-user-status-change", disable ? "Desactivación solicitada." : "Reactivación solicitada.");
  return (
    <>
      <ReasonAction
        label={disable ? "Desactivar" : "Reactivar"}
        stepUp
        busy={request.busy}
        consequence={disable ? "Cuando el segundo aprobador lo apruebe, la persona pierde el acceso al instante; sus documentos quedan intactos." : "Cuando el segundo aprobador lo apruebe, la persona vuelve a entrar con sus roles."}
        onConfirm={async (reason) => (await request.run({ userId: user.userId, action: disable ? "DISABLE" : "ENABLE", reason })) && onDone()}
      />
      <ErrorBox error={request.error} />
    </>
  );
}

function StatusRequests({ onDone }: { onDone: () => void }) {
  const { companyId, can } = useSession();
  const { data, reload } = useLoad(() => query("/api/v1/companies/{companyId}/identity/user-status-requests", { path: { companyId }, query: { status: "REQUESTED" } }), [companyId]);
  const pending: StatusRequest[] = data?.items ?? [];
  if (pending.length === 0) {
    return null;
  }
  return (
    <>
      <h2>Desactivaciones y reactivaciones pendientes</h2>
      <div className="table-wrap">
        <table data-testid="user-status-requests">
          <thead>
            <tr>
              <th>Persona</th>
              <th>Acción</th>
              <th>Motivo</th>
              <th>Pidió</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {pending.map((q) => (
              <tr key={q.requestId}>
                <td className="wrap">
                  <Person name={q.userName} email={q.userEmail} fallback={q.userId} />
                </td>
                <td>{q.action === "DISABLE" ? "Desactivar" : "Reactivar"}</td>
                <td className="wrap">{q.reason}</td>
                <td>{q.requestedBy ?? "—"}</td>
                <td>{can("role:second_approve") ? <StatusDecision request={q} onDone={() => { reload(); onDone(); }} /> : null}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

function StatusDecision({ request, onDone }: { request: StatusRequest; onDone: () => void }) {
  const approve = useCommand(`approve-status:${request.requestId}`, "/api/v1/companies/{companyId}/identity/approve-user-status-change", "Cambio aplicado.");
  const reject = useCommand(`reject-status:${request.requestId}`, "/api/v1/companies/{companyId}/identity/reject-user-status-change", "Solicitud rechazada.");
  return (
    <div className="inline-form">
      <ConfirmAction
        label="Aprobar"
        title={request.action === "DISABLE" ? "¿Desactivar a esta persona?" : "¿Reactivar a esta persona?"}
        consequence={request.action === "DISABLE" ? "Pierde el acceso al instante." : "Vuelve a entrar con sus roles."}
        stepUp
        busy={approve.busy}
        onConfirm={async () => {
          if (await approve.run({ requestId: request.requestId })) {
            onDone();
          }
        }}
      />
      <ReasonAction label="Rechazar" stepUp busy={reject.busy} consequence="La persona queda como está." onConfirm={async (reason) => (await reject.run({ requestId: request.requestId, reason })) && onDone()} />
      <ErrorBox error={approve.error ?? reject.error} />
    </div>
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
  const [invitesVersion, setInvitesVersion] = useState(0);
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
      {can("role:assign") ? (
        <InviteForm
          roles={roleItems}
          onDone={() => {
            reload();
            setInvitesVersion((v) => v + 1);
          }}
        />
      ) : null}
      <Invitations version={invitesVersion} />
      <StatusRequests onDone={reload} />
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
                <td>{u.kind === "SYNTHETIC" ? "Identidad de prueba" : u.kind === "GUEST" ? "Invitado" : "Persona"}</td>
                <td>
                  <StatusBadge status={u.status} />
                  {can(u.status === "ACTIVE" ? "role:revoke" : "role:assign") && u.kind !== "SYNTHETIC" ? <StatusButton user={u} onDone={reload} /> : null}
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
