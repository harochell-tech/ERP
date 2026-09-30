"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmAction, ErrorBox, Field, Loading, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { formatQuantity, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-02-9…11): our trucks (plate, scale capacity) and drivers (cédula), managed by Despacho (fleet:manage).

function RegisterVehicle({ onDone }: { onDone: () => void }) {
  const register = useCommand("register-vehicle", "/api/v1/companies/{companyId}/sales/register-vehicle");
  const [plate, setPlate] = useState("");
  const [capacity, setCapacity] = useState("");
  const fe = useFieldErrors<"plate" | "capacity">();
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const capacityKg = normalizeInput(capacity);
        if (
          !fe.check({
            plate: plate.trim() === "" && "Indique la placa.",
            capacity: !isPositiveDecimal(capacityKg, 6) && "Indique la capacidad en kg, mayor que cero.",
          })
        ) {
          return;
        }
        if (await register.run({ plate: plate.trim(), capacityKg }, undefined, `Vehículo ${plate.trim()} registrado.`)) {
          setPlate("");
          setCapacity("");
          onDone();
        }
      }}
    >
      <Field label="Placa" required error={fe.errors.plate}>
        <input value={plate} onChange={(e) => setPlate(e.target.value)} />
      </Field>
      <Field label="Capacidad (kg)" required error={fe.errors.capacity}>
        <input inputMode="decimal" value={capacity} onChange={(e) => setCapacity(e.target.value)} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={register.busy}>
          Registrar vehículo
        </button>
      </div>
      <ErrorBox error={register.error} />
    </form>
  );
}

function VehicleRow({ vehicle, onDone }: { vehicle: Schemas["VehicleView"]; onDone: () => void }) {
  const { can } = useSession();
  const id = vehicle.vehicleId;
  const update = useCommand(`update-vehicle:${id}`, "/api/v1/companies/{companyId}/sales/update-vehicle", `Capacidad del vehículo ${vehicle.plate} actualizada.`);
  const deactivate = useCommand(`deactivate-vehicle:${id}`, "/api/v1/companies/{companyId}/sales/deactivate-vehicle", `Vehículo ${vehicle.plate} desactivado.`);
  const activate = useCommand(`activate-vehicle:${id}`, "/api/v1/companies/{companyId}/sales/activate-vehicle", `Vehículo ${vehicle.plate} activado.`);
  const [capacity, setCapacity] = useState<string | null>(null);
  const busy = update.busy || deactivate.busy || activate.busy;
  const target = { vehicleId: id, expectedVersion: vehicle.version };
  return (
    <tr>
      <td className="mono">{vehicle.plate}</td>
      <td className="num">
        {capacity === null ? (
          formatQuantity(vehicle.capacityKg)
        ) : (
          <input aria-label={`Capacidad ${vehicle.plate}`} inputMode="decimal" value={capacity} onChange={(e) => setCapacity(e.target.value)} />
        )}
      </td>
      <td>
        <StatusBadge status={vehicle.status} />
      </td>
      <td className="actions">
        {can("fleet:manage") ? (
          capacity === null ? (
            <>
              <button type="button" onClick={() => setCapacity(vehicle.capacityKg)}>
                Cambiar capacidad
              </button>
              {vehicle.status === "ACTIVE" ? (
                <ConfirmAction
                  label="Desactivar"
                  danger
                  busy={busy}
                  consequence={`El vehículo ${vehicle.plate} ya no se podrá asignar a conduces nuevos hasta que se active de nuevo.`}
                  onConfirm={async () => (await deactivate.run(target)) && onDone()}
                />
              ) : (
                <button type="button" disabled={busy} onClick={async () => (await activate.run(target)) && onDone()}>
                  Activar
                </button>
              )}
            </>
          ) : (
            <>
              <button
                type="button"
                disabled={busy || !isPositiveDecimal(normalizeInput(capacity), 6)}
                onClick={async () => {
                  if (await update.run({ ...target, capacityKg: normalizeInput(capacity) })) {
                    setCapacity(null);
                    onDone();
                  }
                }}
              >
                Guardar
              </button>
              <button type="button" onClick={() => setCapacity(null)}>
                Cancelar
              </button>
            </>
          )
        ) : null}
        <ErrorBox error={update.error ?? deactivate.error ?? activate.error} />
      </td>
    </tr>
  );
}

function RegisterDriver({ onDone }: { onDone: () => void }) {
  const register = useCommand("register-driver", "/api/v1/companies/{companyId}/sales/register-driver");
  const [fullName, setFullName] = useState("");
  const [nationalId, setNationalId] = useState("");
  const fe = useFieldErrors<"fullName" | "nationalId">();
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const digits = nationalId.replace(/\D/g, "");
        if (
          !fe.check({
            fullName: fullName.trim() === "" && "Indique el nombre completo del chofer.",
            nationalId: digits.length !== 11 && "La cédula debe tener 11 dígitos.",
          })
        ) {
          return;
        }
        if (await register.run({ fullName: fullName.trim(), nationalId: digits }, undefined, `Chofer ${fullName.trim()} registrado.`)) {
          setFullName("");
          setNationalId("");
          onDone();
        }
      }}
    >
      <Field label="Nombre completo" required error={fe.errors.fullName}>
        <input value={fullName} onChange={(e) => setFullName(e.target.value)} />
      </Field>
      <Field label="Cédula" required error={fe.errors.nationalId}>
        <input inputMode="numeric" value={nationalId} onChange={(e) => setNationalId(e.target.value)} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={register.busy}>
          Registrar chofer
        </button>
      </div>
      <ErrorBox error={register.error} />
    </form>
  );
}

function DriverRow({ driver, onDone }: { driver: Schemas["DriverView"]; onDone: () => void }) {
  const { can } = useSession();
  const id = driver.driverId;
  const update = useCommand(`update-driver:${id}`, "/api/v1/companies/{companyId}/sales/update-driver", `Nombre del chofer ${driver.nationalId} corregido.`);
  const deactivate = useCommand(`deactivate-driver:${id}`, "/api/v1/companies/{companyId}/sales/deactivate-driver", `Chofer ${driver.fullName} desactivado.`);
  const activate = useCommand(`activate-driver:${id}`, "/api/v1/companies/{companyId}/sales/activate-driver", `Chofer ${driver.fullName} activado.`);
  const [name, setName] = useState<string | null>(null);
  const busy = update.busy || deactivate.busy || activate.busy;
  const target = { driverId: id, expectedVersion: driver.version };
  return (
    <tr>
      <td className="wrap">{name === null ? driver.fullName : <input aria-label={`Nombre ${driver.nationalId}`} value={name} onChange={(e) => setName(e.target.value)} />}</td>
      <td className="mono">{driver.nationalId}</td>
      <td>
        <StatusBadge status={driver.status} />
      </td>
      <td className="actions">
        {can("fleet:manage") ? (
          name === null ? (
            <>
              <button type="button" onClick={() => setName(driver.fullName)}>
                Corregir nombre
              </button>
              {driver.status === "ACTIVE" ? (
                <ConfirmAction
                  label="Desactivar"
                  danger
                  busy={busy}
                  consequence={`El chofer ${driver.fullName} ya no se podrá asignar a conduces nuevos hasta que se active de nuevo.`}
                  onConfirm={async () => (await deactivate.run(target)) && onDone()}
                />
              ) : (
                <button type="button" disabled={busy} onClick={async () => (await activate.run(target)) && onDone()}>
                  Activar
                </button>
              )}
            </>
          ) : (
            <>
              <button
                type="button"
                disabled={busy || name.trim() === ""}
                onClick={async () => {
                  if (await update.run({ ...target, fullName: name.trim() })) {
                    setName(null);
                    onDone();
                  }
                }}
              >
                Guardar
              </button>
              <button type="button" onClick={() => setName(null)}>
                Cancelar
              </button>
            </>
          )
        ) : null}
        <ErrorBox error={update.error ?? deactivate.error ?? activate.error} />
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const { data, error, reload } = useLoad(
    can("sales:read")
      ? async () => {
          const [vehicles, drivers] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/vehicles", { path: { companyId } }),
            query("/api/v1/companies/{companyId}/sales/drivers", { path: { companyId } }),
          ]);
          return { vehicles: vehicles.items, drivers: drivers.items };
        }
      : null,
    [companyId],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <h1>Vehículos y choferes</h1>
      <h2>Vehículos</h2>
      {can("fleet:manage") ? <RegisterVehicle onDone={reload} /> : null}
      {data.vehicles.length === 0 ? (
        <p className="muted">No hay vehículos.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Placa</th>
              <th className="num">Capacidad (kg)</th>
              <th>Estado</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.vehicles.map((v) => (
              <VehicleRow key={`${v.vehicleId}:${v.version}`} vehicle={v} onDone={reload} />
            ))}
          </tbody>
        </table></div>
      )}
      <h2>Choferes</h2>
      {can("fleet:manage") ? <RegisterDriver onDone={reload} /> : null}
      {data.drivers.length === 0 ? (
        <p className="muted">No hay choferes.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Nombre</th>
              <th>Cédula</th>
              <th>Estado</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.drivers.map((d) => (
              <DriverRow key={`${d.driverId}:${d.version}`} driver={d} onDone={reload} />
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
