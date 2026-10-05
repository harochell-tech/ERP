"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// PRS-05 (E-PRS-05-2, E-SRV1-9, E-PRS-03-2): the delivery zones — Higüey, Bávaro, Cap Cana… The Controller or Crédito adds, renames,
// deactivates and reactivates them without approval; an own-truck order chooses one and its freight comes from the customer's list.

const STATUS_LABELS: Readonly<Record<string, string>> = { ACTIVE: "Activa", INACTIVE: "Inactiva" };

function ZoneRow({ zone, canManage, onDone }: { zone: Schemas["DeliveryZoneView"]; canManage: boolean; onDone: () => void }) {
  const rename = useCommand(`rename-zone:${zone.zoneId}`, "/api/v1/companies/{companyId}/sales/rename-delivery-zone");
  const deactivate = useCommand(`deactivate-zone:${zone.zoneId}`, "/api/v1/companies/{companyId}/sales/deactivate-delivery-zone");
  const reactivate = useCommand(`reactivate-zone:${zone.zoneId}`, "/api/v1/companies/{companyId}/sales/reactivate-delivery-zone");
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(zone.name);
  const busy = rename.busy || deactivate.busy || reactivate.busy;
  return (
    <tr data-testid={`zone:${zone.name}`}>
      <td>
        {editing ? (
          <input aria-label={`Nuevo nombre de ${zone.name}`} maxLength={60} value={name} onChange={(e) => setName(e.target.value)} />
        ) : (
          zone.name
        )}
      </td>
      <td>
        <StatusBadge status={zone.status} label={STATUS_LABELS[zone.status]} />
      </td>
      <td className="actions">
        {canManage && editing ? (
          <>
            <button
              type="button"
              className="primary"
              disabled={busy || !name.trim()}
              onClick={async () => {
                if (await rename.run({ zoneId: zone.zoneId, expectedVersion: zone.version, name: name.trim() }, undefined, `Zona renombrada a ${name.trim()}.`)) {
                  setEditing(false);
                  onDone();
                }
              }}
            >
              Guardar
            </button>
            <button type="button" onClick={() => setEditing(false)}>
              Cancelar
            </button>
          </>
        ) : null}
        {canManage && !editing ? (
          <button type="button" onClick={() => setEditing(true)}>
            Renombrar
          </button>
        ) : null}
        {canManage && zone.status === "ACTIVE" ? (
          <ConfirmAction
            label="Desactivar"
            danger
            busy={busy}
            consequence="Deja de ofrecerse en pedidos y cotizaciones nuevos; los que ya la tienen la conservan."
            onConfirm={async () => (await deactivate.run({ zoneId: zone.zoneId, expectedVersion: zone.version }, undefined, `Zona ${zone.name} desactivada.`)) && onDone()}
          />
        ) : null}
        {canManage && zone.status === "INACTIVE" ? (
          <ConfirmAction
            label="Reactivar"
            busy={busy}
            consequence="Vuelve a ofrecerse en pedidos y cotizaciones."
            onConfirm={async () => (await reactivate.run({ zoneId: zone.zoneId, expectedVersion: zone.version }, undefined, `Zona ${zone.name} reactivada.`)) && onDone()}
          />
        ) : null}
        <ErrorBox error={rename.error ?? deactivate.error ?? reactivate.error} />
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const canManage = can("delivery_zone:manage");
  const create = useCommand("create-zone", "/api/v1/companies/{companyId}/sales/create-delivery-zone");
  const [name, setName] = useState("");
  const fe = useFieldErrors();
  const zones = useLoad(can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/delivery-zones", { path: { companyId } }) : null, [companyId]);
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (zones.data === null) {
    return <LoadingIndicator error={zones.error} />;
  }
  return (
    <>
      <h1>Zonas de entrega</h1>
      <p className="muted">
        Un pedido entregado con nuestro camión elige su zona; el flete de cada producto a esa zona sale de la lista de precios del cliente.
      </p>
      {canManage ? (
        <div className="actions">
          <Field label="Nueva zona" error={fe.errors.name}>
            <input aria-label="Nombre de la zona" maxLength={60} value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <button
            type="button"
            className="primary"
            disabled={create.busy}
            onClick={async () => {
              if (!fe.check({ name: !name.trim() && "Indique el nombre de la zona." })) {
                return;
              }
              if (await create.run({ name: name.trim() }, undefined, `Zona ${name.trim()} creada.`)) {
                setName("");
                zones.reload();
              }
            }}
          >
            Agregar zona
          </button>
        </div>
      ) : null}
      <ErrorBox error={create.error} />
      {zones.data.items.length === 0 ? (
        <p className="muted">Todavía no hay zonas.</p>
      ) : (
        <div className="table-wrap">
          <table data-testid="zones">
            <thead>
              <tr>
                <th>Zona</th>
                <th>Estado</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {zones.data.items.map((z) => (
                <ZoneRow key={`${z.zoneId}:${z.version}`} zone={z} canManage={canManage} onDone={zones.reload} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
