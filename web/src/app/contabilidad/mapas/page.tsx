"use client";

import { query, type Schemas } from "@/api/client";
import { ErrorBox, Loading, NoPermission } from "@/components/ui";
import { formatDate, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type RoleMap = Schemas["AccountRoleMapView"];

// E-B03-15-2: maps are loaded as DRAFT by `rochell-migrate import-account-map`; this screen approves them (someone other
// than the preparer, step-up) and shows the chart of accounts they point to.

function MapRow({ map, onDone }: { map: RoleMap; onDone: () => void }) {
  const { can } = useSession();
  const approve = useCommand(`approve-map:${map.mapId}`, "/api/v1/companies/{companyId}/finance/approve-account-role-map");
  return (
    <tr>
      <td>{map.accountRole}</td>
      <td>{map.itemCategory ?? "Todas"}</td>
      <td>
        {map.accountCode} — {map.accountName}
      </td>
      <td>
        {formatDate(map.effectiveFrom)}
        {map.effectiveTo ? ` – ${formatDate(map.effectiveTo)}` : ""}
      </td>
      <td>{statusLabel(map.status)}</td>
      <td>{map.preparedBy ?? "Despliegue"}</td>
      <td>{map.approvedBy ?? "—"}</td>
      <td>
        {map.status === "DRAFT" && can("account_role_map:approve") ? (
          <button type="button" disabled={approve.busy} onClick={async () => (await approve.run({ mapId: map.mapId })) && onDone()}>
            Aprobar
          </button>
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

  if (!allowed) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Mapas de cuentas</h1>
      <p className="muted">Los mapas se cargan en borrador con la herramienta de despliegue; aquí se aprueban.</p>
      {maps.data === null ? (
        <Loading error={maps.error} />
      ) : maps.data.items.length === 0 ? (
        <p className="muted">No hay mapas cargados.</p>
      ) : (
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
              <MapRow key={`${m.mapId}:${m.status}`} map={m} onDone={maps.reload} />
            ))}
          </tbody>
        </table>
      )}
      <h2>Catálogo de cuentas</h2>
      {accounts.data === null ? (
        <Loading error={accounts.error} />
      ) : (
        <table>
          <thead>
            <tr>
              <th>Código</th>
              <th>Nombre</th>
              <th>Control</th>
            </tr>
          </thead>
          <tbody>
            {accounts.data.items.map((a) => (
              <tr key={a.accountId}>
                <td>{a.code}</td>
                <td>{a.name}</td>
                <td>{a.isControl ? "Sí" : "No"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
