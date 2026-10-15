"use client";

import { query } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { Field } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { licenseWarning, vehicleName } from "@/lib/fleet";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

export interface Transport {
  vehicleId: string;
  driverId: string;
  plate: string;
  driverName: string;
}

export const NO_TRANSPORT: Transport = { vehicleId: "", driverId: "", plate: "", driverName: "" };

/** DSP-01 (E-DSP-2): the problems of the transport, or nothing — our truck and driver, or the customer's plate and driver. */
export function transportErrors(own: boolean, t: Transport): Partial<Record<keyof Transport, string>> {
  return own
    ? { vehicleId: t.vehicleId ? undefined : "Elija el camión.", driverId: t.driverId ? undefined : "Elija el chofer." }
    : {
        plate: t.plate.trim() ? undefined : "Indique la placa del vehículo del cliente.",
        driverName: t.driverName.trim() ? undefined : "Indique el nombre del chofer del cliente.",
      };
}

/** The transport as the plan-delivery command takes it. */
export function transportBody(own: boolean, t: Transport) {
  return own
    ? { vehicleId: t.vehicleId, driverId: t.driverId, customerVehiclePlate: null, customerDriverName: null }
    : { vehicleId: null, driverId: null, customerVehiclePlate: t.plate.trim(), customerDriverName: t.driverName.trim() };
}

/** DSP-01 (E-DSP-2): who takes the delivery — chosen with the delivery itself. */
export function TransportFields({ own, value, onChange, errors }: { own: boolean; value: Transport; onChange: (t: Transport) => void; errors: Partial<Record<keyof Transport, string>> }) {
  const { companyId } = useSession();
  const { data, error } = useLoad(
    own
      ? async () => {
          const [vehicles, drivers] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/vehicles", { path: { companyId }, query: { status: "ACTIVE" } }),
            query("/api/v1/companies/{companyId}/sales/drivers", { path: { companyId }, query: { status: "ACTIVE" } }),
          ]);
          return { vehicles: vehicles.items, drivers: drivers.items };
        }
      : null,
    [companyId, own],
  );
  if (own && data === null) {
    return <LoadingIndicator error={error} />;
  }
  return own && data ? (
    <>
      <Field label="Camión" required error={errors.vehicleId}>
        <select aria-label="Camión" value={value.vehicleId} onChange={(e) => onChange({ ...value, vehicleId: e.target.value })}>
          <option value="">Seleccione…</option>
          {data.vehicles.map((v) => (
            <option key={v.vehicleId} value={v.vehicleId}>
              {vehicleName(v)} ({formatQuantity(v.capacityKg)} kg)
            </option>
          ))}
        </select>
      </Field>
      <Field label="Chofer" required error={errors.driverId}>
        <select aria-label="Chofer" value={value.driverId} onChange={(e) => onChange({ ...value, driverId: e.target.value })}>
          <option value="">Seleccione…</option>
          {data.drivers.map((d) => (
            <option key={d.driverId} value={d.driverId}>
              {d.fullName}
            </option>
          ))}
        </select>
      </Field>
      {licenseWarning(data.drivers.find((d) => d.driverId === value.driverId)?.daysToLicenseExpiry) ? (
        <p className="notice" data-testid="license-warning">
          {licenseWarning(data.drivers.find((d) => d.driverId === value.driverId)?.daysToLicenseExpiry)}. Puede despachar; actualice la fecha en Maestros › Vehículos y choferes cuando el chofer
          renueve la licencia.
        </p>
      ) : null}
    </>
  ) : (
    <>
      <Field label="Placa del cliente" required error={errors.plate}>
        <input aria-label="Placa del cliente" value={value.plate} onChange={(e) => onChange({ ...value, plate: e.target.value })} />
      </Field>
      <Field label="Chofer del cliente" required error={errors.driverName}>
        <input aria-label="Chofer del cliente" value={value.driverName} onChange={(e) => onChange({ ...value, driverName: e.target.value })} />
      </Field>
    </>
  );
}
