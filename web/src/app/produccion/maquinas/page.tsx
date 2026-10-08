"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { IdealCycles } from "@/components/Efficiency";
import { PlantSelect, useChosenPlant, usePlants } from "@/components/Production";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { formatTime, toApiTime } from "@/lib/production";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// MFG1-07 (E-MFG1-07-5): the machines and shifts of a plant, managed by the Gerente de planta (production_master:manage).
// UX4-03 (P-38): the headers say what each column is; the plant is the one chosen above, not repeated per row.

function CreateMachine({ plantId, onDone }: { plantId: string; onDone: () => void }) {
  const create = useCommand("create-machine", "/api/v1/companies/{companyId}/manufacturing/create-machine");
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const fe = useFieldErrors<"code" | "name">();
  return (
    <form
      className="inline-form"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (!fe.check({ code: code.trim() === "" && "Indique el código de la máquina.", name: name.trim() === "" && "Indique el nombre de la máquina." })) {
          return;
        }
        const machineCode = code.trim().toUpperCase();
        if (await create.run({ plantId, code: machineCode, name: name.trim() }, undefined, `Máquina ${machineCode} creada.`)) {
          setCode("");
          setName("");
          onDone();
        }
      }}
    >
      <Field label="Código de la máquina" required error={fe.errors.code}>
        <input value={code} onChange={(e) => setCode(e.target.value)} />
      </Field>
      <Field label="Nombre de la máquina" required error={fe.errors.name}>
        <input value={name} onChange={(e) => setName(e.target.value)} />
      </Field>
      <button type="submit" className="primary" disabled={create.busy}>
        Crear máquina
      </button>
      <ErrorBox error={create.error} />
    </form>
  );
}

function MachineRow({ machine, onDone }: { machine: Schemas["MachineView"]; onDone: () => void }) {
  const { can } = useSession();
  const id = machine.machineId;
  const rename = useCommand(`rename-machine:${id}`, "/api/v1/companies/{companyId}/manufacturing/rename-machine", `Máquina ${machine.code} renombrada.`);
  const status = useCommand(
    `set-machine-status:${id}`,
    "/api/v1/companies/{companyId}/manufacturing/set-machine-status",
    `Máquina ${machine.code} ${machine.status === "ACTIVE" ? "desactivada" : "activada"}.`,
  );
  const [name, setName] = useState<string | null>(null);
  const busy = rename.busy || status.busy;
  const target = { plantId: machine.plantId, machineId: id, expectedVersion: machine.version };
  return (
    <tr>
      <td className="mono">{machine.code}</td>
      <td>{name === null ? machine.name : <input aria-label={`Nombre ${machine.code}`} value={name} onChange={(e) => setName(e.target.value)} />}</td>
      <td>
        <StatusBadge status={machine.status} />
      </td>
      <td className="actions">
        {can("production_master:manage") ? (
          name === null ? (
            <>
              <button type="button" onClick={() => setName(machine.name)}>
                Cambiar nombre
              </button>
              {machine.status === "ACTIVE" ? (
                <ConfirmAction
                  label="Desactivar"
                  title={`¿Desactivar la máquina ${machine.code}?`}
                  consequence="No se podrán iniciar corridas nuevas en esta máquina hasta que se active otra vez. Las corridas registradas no cambian."
                  danger
                  busy={busy}
                  onConfirm={async () => (await status.run({ ...target, status: "INACTIVE" })) && onDone()}
                />
              ) : (
                <button type="button" disabled={busy} onClick={async () => (await status.run({ ...target, status: "ACTIVE" })) && onDone()}>
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
                  if (await rename.run({ ...target, name: name.trim() })) {
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
        <ErrorBox error={rename.error ?? status.error} />
      </td>
    </tr>
  );
}

function DefineShift({ plantId, onDone }: { plantId: string; onDone: () => void }) {
  const define = useCommand("define-shift", "/api/v1/companies/{companyId}/manufacturing/define-shift");
  const [form, setForm] = useState({ code: "", startsAt: "", endsAt: "" });
  const fe = useFieldErrors<"code" | "startsAt" | "endsAt">();
  return (
    <form
      className="inline-form"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const startsAt = toApiTime(form.startsAt);
        const endsAt = toApiTime(form.endsAt);
        const valid = fe.check({
          code: form.code.trim() === "" && "Indique el código del turno.",
          startsAt: startsAt === null && "Indique la hora de inicio.",
          endsAt: endsAt === null ? "Indique la hora de fin." : startsAt === endsAt && "La hora de fin debe ser distinta de la de inicio.",
        });
        if (!valid || startsAt === null || endsAt === null) {
          return;
        }
        const shiftCode = form.code.trim().toUpperCase();
        if (await define.run({ plantId, code: shiftCode, startsAt, endsAt }, undefined, `Turno ${shiftCode} definido.`)) {
          setForm({ code: "", startsAt: "", endsAt: "" });
          onDone();
        }
      }}
    >
      <Field label="Código del turno" required error={fe.errors.code}>
        <input value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} />
      </Field>
      <Field label="Inicia" required error={fe.errors.startsAt}>
        <input type="time" value={form.startsAt} onChange={(e) => setForm({ ...form, startsAt: e.target.value })} />
      </Field>
      <Field label="Termina" required error={fe.errors.endsAt}>
        <input type="time" value={form.endsAt} onChange={(e) => setForm({ ...form, endsAt: e.target.value })} />
      </Field>
      <button type="submit" className="primary" disabled={define.busy}>
        Definir turno
      </button>
      <ErrorBox error={define.error} />
    </form>
  );
}

function ShiftRow({ shift, onDone }: { shift: Schemas["ShiftView"]; onDone: () => void }) {
  const { can } = useSession();
  const id = shift.shiftId;
  const times = useCommand(`update-shift-times:${id}`, "/api/v1/companies/{companyId}/manufacturing/update-shift-times", `Horario del turno ${shift.code} actualizado.`);
  const status = useCommand(
    `set-shift-status:${id}`,
    "/api/v1/companies/{companyId}/manufacturing/set-shift-status",
    `Turno ${shift.code} ${shift.status === "ACTIVE" ? "desactivado" : "activado"}.`,
  );
  const [edit, setEdit] = useState<{ startsAt: string; endsAt: string } | null>(null);
  const busy = times.busy || status.busy;
  const target = { plantId: shift.plantId, shiftId: id, expectedVersion: shift.version };
  const startsAt = edit ? toApiTime(edit.startsAt) : null;
  const endsAt = edit ? toApiTime(edit.endsAt) : null;
  return (
    <tr>
      <td className="mono">{shift.code}</td>
      <td>
        {edit === null ? (
          formatTime(shift.startsAt)
        ) : (
          <input type="time" aria-label={`Inicia ${shift.code}`} value={edit.startsAt} onChange={(e) => setEdit({ ...edit, startsAt: e.target.value })} />
        )}
      </td>
      <td>
        {edit === null ? (
          formatTime(shift.endsAt)
        ) : (
          <input type="time" aria-label={`Termina ${shift.code}`} value={edit.endsAt} onChange={(e) => setEdit({ ...edit, endsAt: e.target.value })} />
        )}
      </td>
      <td>{shift.crossesMidnight ? "Sí (termina al día siguiente)" : "No"}</td>
      <td>
        <StatusBadge status={shift.status} />
      </td>
      <td className="actions">
        {can("production_master:manage") ? (
          edit === null ? (
            <>
              <button type="button" onClick={() => setEdit({ startsAt: formatTime(shift.startsAt), endsAt: formatTime(shift.endsAt) })}>
                Cambiar horario
              </button>
              {shift.status === "ACTIVE" ? (
                <ConfirmAction
                  label="Desactivar"
                  title={`¿Desactivar el turno ${shift.code}?`}
                  consequence="No se podrán iniciar corridas nuevas en este turno hasta que se active otra vez. Las corridas registradas no cambian."
                  danger
                  busy={busy}
                  onConfirm={async () => (await status.run({ ...target, status: "INACTIVE" })) && onDone()}
                />
              ) : (
                <button type="button" disabled={busy} onClick={async () => (await status.run({ ...target, status: "ACTIVE" })) && onDone()}>
                  Activar
                </button>
              )}
            </>
          ) : (
            <>
              <button
                type="button"
                disabled={busy || startsAt === null || endsAt === null || startsAt === endsAt}
                onClick={async () => {
                  if (startsAt !== null && endsAt !== null && (await times.run({ ...target, startsAt, endsAt }))) {
                    setEdit(null);
                    onDone();
                  }
                }}
              >
                Guardar
              </button>
              <button type="button" onClick={() => setEdit(null)}>
                Cancelar
              </button>
            </>
          )
        ) : null}
        <ErrorBox error={times.error ?? status.error} />
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const plants = usePlants();
  const { plant, setPlant } = useChosenPlant(plants.data);
  const plantId = plant?.plantId ?? "";
  const { data, error, reload } = useLoad(
    can("production:read") && plantId
      ? async () => {
          const [machines, shifts] = await Promise.all([
            query("/api/v1/companies/{companyId}/manufacturing/machines", { path: { companyId }, query: { plantId } }),
            query("/api/v1/companies/{companyId}/manufacturing/shifts", { path: { companyId }, query: { plantId } }),
          ]);
          return { machines: machines.items, shifts: shifts.items };
        }
      : null,
    [companyId, plantId],
  );
  if (!can("production:read")) {
    return <NoPermission />;
  }
  if (plants.data === null) {
    return <LoadingIndicator error={plants.error} />;
  }
  if (plants.data.length === 0) {
    return (
      <>
        <h1>Máquinas y turnos</h1>
        <EmptyState title="No hay plantas disponibles." />
      </>
    );
  }
  return (
    <>
      <h1>Máquinas y turnos</h1>
      <PlantSelect plants={plants.data} value={plantId} onChange={setPlant} />
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : (
        <>
          <h2>Máquinas</h2>
          {can("production_master:manage") ? <CreateMachine plantId={plantId} onDone={reload} /> : null}
          {data.machines.length === 0 ? (
            <EmptyState title="No hay máquinas en la planta.">
              <p>{can("production_master:manage") ? "Cree la primera con el formulario de arriba." : "El Gerente de planta crea las máquinas."}</p>
            </EmptyState>
          ) : (
            <div className="table-wrap"><table>
              <thead>
                <tr>
                  <th>Código de la máquina</th>
                  <th>Nombre</th>
                  <th>Estado</th>
                  <th>Acciones</th>
                </tr>
              </thead>
              <tbody>
                {data.machines.map((m) => (
                  <MachineRow key={`${m.machineId}:${m.version}`} machine={m} onDone={reload} />
                ))}
              </tbody>
            </table></div>
          )}
          <h2>Turnos</h2>
          {can("production_master:manage") ? <DefineShift plantId={plantId} onDone={reload} /> : null}
          {data.shifts.length === 0 ? (
            <EmptyState title="No hay turnos en la planta.">
              <p>{can("production_master:manage") ? "Defina el primero con el formulario de arriba." : "El Gerente de planta define los turnos."}</p>
            </EmptyState>
          ) : (
            <div className="table-wrap"><table>
              <thead>
                <tr>
                  <th>Código del turno</th>
                  <th>Hora de inicio</th>
                  <th>Hora de fin</th>
                  <th>Cruza la medianoche</th>
                  <th>Estado</th>
                  <th>Acciones</th>
                </tr>
              </thead>
              <tbody>
                {data.shifts.map((s) => (
                  <ShiftRow key={`${s.shiftId}:${s.version}`} shift={s} onDone={reload} />
                ))}
              </tbody>
            </table></div>
          )}
          {/* MFG3-04 (E-MFG3-5, E-MFG3-01-2) */}
          <IdealCycles machines={data.machines} />
        </>
      )}
    </>
  );
}
