"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Loading, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatDateTime, ROLES } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type RoleRequest = Schemas["RoleRequestView"];

// UI-01 / E-UI01-4/6: role change requests. The second approver — never the requester nor the affected user — approves (step-up;
// the change takes effect at once, SoD checked) or rejects with a reason.
function Decision({ request, onDone }: { request: RoleRequest; onDone: () => void }) {
  const { state } = useSession();
  const approve = useCommand(`approve-role:${request.requestId}`, "/api/v1/companies/{companyId}/identity/approve-role-change");
  const reject = useCommand(`reject-role:${request.requestId}`, "/api/v1/companies/{companyId}/identity/reject-role-change");
  const me = state.status === "ready" ? state.session.userId : null;
  if (me === request.requestedById || me === request.userId) {
    return <span className="muted">La decide otra persona.</span>;
  }
  const busy = approve.busy || reject.busy;
  return (
    <div className="inline-form">
      <button
        type="button"
        className="primary"
        disabled={busy}
        onClick={async () => {
          if (await approve.run({ requestId: request.requestId })) {
            onDone();
          }
        }}
      >
        Aprobar
      </button>
      <ReasonAction
        label="Rechazar"
        busy={busy}
        onConfirm={async (reason) => {
          if (await reject.run({ requestId: request.requestId, reason })) {
            onDone();
          }
        }}
      />
      <ErrorBox error={approve.error ?? reject.error} />
    </div>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const [status, setStatus] = useState("REQUESTED");
  const { data, error, reload } = useLoad(
    can("iam:read") ? () => query("/api/v1/companies/{companyId}/identity/role-requests", { path: { companyId }, query: { status, limit: 200 } }) : null,
    [companyId, status],
  );
  if (!can("iam:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Solicitudes de rol</h1>
      <label className="field">
        <span>Estado</span>
        <select value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="REQUESTED">Pendientes</option>
          <option value="APPROVED">Aprobadas</option>
          <option value="REJECTED">Rechazadas</option>
          <option value="">Todas</option>
        </select>
      </label>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay solicitudes.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Usuario</th>
              <th>Cambio</th>
              <th>Solicitó</th>
              <th>Estado</th>
              <th>Decisión</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((r) => (
              <tr key={r.requestId}>
                <td>{r.userEmail ?? r.userId}</td>
                <td>
                  {r.action === "ASSIGN" ? "Asignar" : "Revocar"} {ROLES[r.roleCode] ?? r.roleName}
                  {r.plantCode ? ` (planta ${r.plantCode})` : ""}
                </td>
                <td>
                  {r.requestedBy ?? "—"} <span className="muted">{formatDateTime(r.requestedAt)}</span>
                </td>
                <td>
                  <StatusBadge status={r.status} />
                </td>
                <td>
                  {r.status === "REQUESTED" && can("role:second_approve") ? (
                    <Decision request={r} onDone={reload} />
                  ) : r.status === "APPROVED" ? (
                    <span>{r.approvedBy}</span>
                  ) : r.status === "REJECTED" ? (
                    <span>
                      {r.rejectedBy} <span className="muted">— {r.rejectionReason}</span>
                    </span>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
