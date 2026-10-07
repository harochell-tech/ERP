"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, NoPermission, StatusBadge } from "@/components/ui";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// PLT-01 (E-PLT-1…5): the company's plants and their locations. With company:manage (Controller, Superadministrador) a plant is created
// with its four locations (RECEPCION, PATIO, CURADO, TRANSITO), more locations are added and renamed, and plants and locations are
// deactivated when nothing is left in them (never deleted); a deactivated one leaves the lists of new documents. Plant names change in
// Configuración › Empresa; machines and shifts in Producción › Máquinas y turnos; people per plant in Seguridad › Usuarios y roles.

const STATUS: Readonly<Record<string, string>> = { ACTIVE: "En uso", INACTIVE: "Desactivada" };

type Plant = Schemas["PlantView"];

function NewPlant({ onDone }: { onDone: () => void }) {
  const create = useCommand("create-plant", "/api/v1/companies/{companyId}/master-data/create-plant");
  const [form, setForm] = useState({ code: "", name: "" });
  return (
    <form
      className="card"
      noValidate
      data-testid="plant-form"
      onSubmit={async (e) => {
        e.preventDefault();
        if (await create.run({ code: form.code, name: form.name }, undefined, `Planta ${form.code.toUpperCase()} creada con RECEPCION, PATIO, CURADO y TRANSITO.`)) {
          setForm({ code: "", name: "" });
          onDone();
        }
      }}
    >
      <h2>Nueva planta</h2>
      <p className="muted">Nace con cuatro ubicaciones: RECEPCION (materiales), PATIO (producto terminado), CURADO (lotes en curado) y TRANSITO (despachos en camino). El código no cambia nunca.</p>
      <Field label="Código" hint="De 2 a 20 letras mayúsculas, dígitos, «-» o «_».">
        <input aria-label="Código de la planta" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value.toUpperCase() })} />
      </Field>
      <Field label="Nombre">
        <input aria-label="Nombre de la planta" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
      </Field>
      <div className="actions">
        <button type="submit" className="primary" disabled={create.busy || !form.code.trim() || !form.name.trim()}>
          Crear planta
        </button>
      </div>
      <ErrorBox error={create.error} />
    </form>
  );
}

function PlantCard({ plant, manage, onDone }: { plant: Plant; manage: boolean; onDone: () => void }) {
  const status = useCommand(`plant-status:${plant.plantId}`, "/api/v1/companies/{companyId}/master-data/set-plant-status");
  const addLocation = useCommand(`create-location:${plant.plantId}`, "/api/v1/companies/{companyId}/master-data/create-location");
  const rename = useCommand(`rename-location:${plant.plantId}`, "/api/v1/companies/{companyId}/master-data/rename-location");
  const locationStatus = useCommand(`location-status:${plant.plantId}`, "/api/v1/companies/{companyId}/master-data/set-location-status");
  const [form, setForm] = useState({ code: "", name: "" });
  const [names, setNames] = useState<Record<string, string>>({});
  const active = (plant.status ?? "ACTIVE") === "ACTIVE";
  const label = plant.name ? `${plant.name} (${plant.code})` : plant.code;
  return (
    <section className="card" data-testid={`plant:${plant.code}`}>
      <h2>
        {label} <StatusBadge status={plant.status ?? "ACTIVE"} label={STATUS[plant.status ?? "ACTIVE"]} />
      </h2>
      <p className="muted">Área de valuación {plant.valuationAreaCode}</p>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Ubicación</th>
              <th>Nombre</th>
              <th>Estado</th>
              {manage ? <th /> : null}
            </tr>
          </thead>
          <tbody>
            {plant.locations.map((l) => {
              const system = l.code === "CURADO" || l.code === "TRANSITO";
              const inUse = (l.status ?? "ACTIVE") === "ACTIVE";
              const edited = names[l.locationId];
              return (
                <tr key={l.locationId} data-testid={`location:${plant.code}:${l.code}`}>
                  <td className="mono">{l.code}</td>
                  <td>
                    {manage ? (
                      <input
                        aria-label={`Nombre de ${l.code} en ${plant.code}`}
                        value={names[l.locationId] ?? l.name ?? ""}
                        onChange={(e) => setNames({ ...names, [l.locationId]: e.target.value })}
                      />
                    ) : (
                      (l.name ?? "—")
                    )}
                  </td>
                  <td>
                    <StatusBadge status={l.status ?? "ACTIVE"} label={inUse ? "En uso" : "Desactivada"} />
                  </td>
                  {manage ? (
                    <td>
                      <div className="actions row-buttons">
                        {edited !== undefined && edited.trim() && edited !== l.name ? (
                          <button
                            type="button"
                            disabled={rename.busy}
                            onClick={async () =>
                              (await rename.run({ locationId: l.locationId, name: edited.trim() }, undefined, `${l.code} renombrada.`)) && onDone()
                            }
                          >
                            Guardar nombre
                          </button>
                        ) : null}
                        {!system ? (
                          <ConfirmAction
                            label={inUse ? `Desactivar ${l.code}` : `Reactivar ${l.code}`}
                            danger={inUse}
                            stepUp
                            busy={locationStatus.busy}
                            consequence={
                              inUse
                                ? `${l.code} deja de ofrecerse en documentos nuevos; su historia se conserva. Solo si no tiene inventario.`
                                : `${l.code} vuelve a ofrecerse en los documentos.`
                            }
                            onConfirm={async () =>
                              (await locationStatus.run({ locationId: l.locationId, active: !inUse }, undefined, `${l.code} actualizada.`)) && onDone()
                            }
                          />
                        ) : null}
                      </div>
                    </td>
                  ) : null}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {manage ? (
        <>
          <div className="inline-form">
            <Field label="Nueva ubicación">
              <input
                aria-label={`Código de la ubicación nueva en ${plant.code}`}
                placeholder="Código"
                value={form.code}
                onChange={(e) => setForm({ ...form, code: e.target.value.toUpperCase() })}
              />
            </Field>
            <Field label="Nombre">
              <input aria-label={`Nombre de la ubicación nueva en ${plant.code}`} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
            </Field>
            <button
              type="button"
              disabled={addLocation.busy || !form.code.trim() || !form.name.trim()}
              onClick={async () => {
                if (await addLocation.run({ plantId: plant.plantId, code: form.code, name: form.name }, undefined, `Ubicación ${form.code} agregada a ${plant.code}.`)) {
                  setForm({ code: "", name: "" });
                  onDone();
                }
              }}
            >
              Agregar ubicación
            </button>
          </div>
          <div className="actions">
            <ConfirmAction
              label={active ? `Desactivar planta ${plant.code}` : `Reactivar planta ${plant.code}`}
              danger={active}
              stepUp
              busy={status.busy}
              consequence={
                active
                  ? `${label} deja de ofrecerse en documentos nuevos; su historia se conserva. Solo si no tiene inventario, corridas en proceso ni despachos en camino.`
                  : `${label} vuelve a ofrecerse en los documentos.`
              }
              onConfirm={async () => (await status.run({ plantId: plant.plantId, active: !active }, undefined, `Planta ${plant.code} actualizada.`)) && onDone()}
            />
          </div>
        </>
      ) : null}
      <ErrorBox error={status.error ?? addLocation.error ?? rename.error ?? locationStatus.error} />
    </section>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const manage = can("company:manage");
  const { data, error, reload } = useLoad(
    can("master_data:read") ? () => query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId }, query: { includeInactive: "true" } }) : null,
    [companyId],
  );
  if (!can("master_data:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Plantas y ubicaciones</h1>
      <p className="muted">
        Las plantas de la empresa y sus ubicaciones. El nombre de cada planta se cambia en Configuración › Empresa; las máquinas y los turnos en Producción › Máquinas y
        turnos; las personas de cada planta en Seguridad › Usuarios y roles.
      </p>
      {manage ? <NewPlant onDone={reload} /> : null}
      {data === null ? <LoadingIndicator error={error} /> : data.items.map((p) => <PlantCard key={p.plantId} plant={p} manage={manage} onDone={reload} />)}
    </>
  );
}
