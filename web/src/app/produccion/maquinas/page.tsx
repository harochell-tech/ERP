"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { PlantSelect, useChosenPlant, usePlants } from "@/components/Production";
import { ErrorBox, Field, Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatTime, toApiTime } from "@/lib/production";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// MFG1-07 (E-MFG1-07-5): the machines and shifts of a plant, managed by the Gerente de planta (production_master:manage).

function CreateMachine({ plantId, onDone }: { plantId: string; onDone: () => void }) {
  const create = useCommand("create-machine", "/api/v1/companies/{companyId}/manufacturing/create-machine");
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  return (
    <form
      className="inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        if (await create.run({ plantId, code: code.trim().toUpperCase(), name: name.trim() })) {
          setCode("");
          setName("");
          onDone();
        }
      }}
    >
      <Field label="Código de la máquina">
        <input value={code} onChange={(e) => setCode(e.target.value)} required />
      </Field>
      <Field label="Nombre de la máquina">
        <input value={name} onChange={(e) => setName(e.target.value)} required />
      </Field>
      <button type="submit" disabled={create.busy}>
        Crear máquina
      </button>
      <ErrorBox error={create.error} />
    </form>
  );
}

function MachineRow({ machine, onDone }: { machine: Schemas["MachineView"]; onDone: () => void }) {
  const { can } = useSession();
  const id = machine.machineId;
  const rename = useCommand(`rename-machine:${id}`, "/api/v1/companies/{companyId}/manufacturing/rename-machine");
  const status = useCommand(`set-machine-status:${id}`, "/api/v1/companies/{companyId}/manufacturing/set-machine-status");
  const [name, setName] = useState<string | null>(null);
  const busy = rename.busy || status.busy;
  const target = { plantId: machine.plantId, machineId: id, expectedVersion: machine.version };
  return (
    <tr>
      <td className="mono">{machine.code}</td>
      <td>{name === null ? machine.name : <input aria-label={`Nombre ${machine.code}`} value={name} onChange={(e) => setName(e.target.value)} />}</td>
      <td>{machine.plantCode}</td>
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
              <button
                type="button"
                disabled={busy}
                onClick={async () => (await status.run({ ...target, status: machine.status === "ACTIVE" ? "INACTIVE" : "ACTIVE" })) && onDone()}
              >
                {machine.status === "ACTIVE" ? "Desactivar" : "Activar"}
              </button>
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
  const [invalid, setInvalid] = useState<string | null>(null);
  return (
    <form
      className="inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const startsAt = toApiTime(form.startsAt);
        const endsAt = toApiTime(form.endsAt);
        if (startsAt === null || endsAt === null || startsAt === endsAt) {
          setInvalid("Indique la hora de inicio y la de fin, distintas.");
          return;
        }
        setInvalid(null);
        if (await define.run({ plantId, code: form.code.trim().toUpperCase(), startsAt, endsAt })) {
          setForm({ code: "", startsAt: "", endsAt: "" });
          onDone();
        }
      }}
    >
      <Field label="Código del turno">
        <input value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} required />
      </Field>
      <Field label="Inicia">
        <input type="time" value={form.startsAt} onChange={(e) => setForm({ ...form, startsAt: e.target.value })} />
      </Field>
      <Field label="Termina">
        <input type="time" value={form.endsAt} onChange={(e) => setForm({ ...form, endsAt: e.target.value })} />
      </Field>
      <button type="submit" disabled={define.busy}>
        Definir turno
      </button>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={define.error} />
    </form>
  );
}

function ShiftRow({ shift, onDone }: { shift: Schemas["ShiftView"]; onDone: () => void }) {
  const { can } = useSession();
  const id = shift.shiftId;
  const times = useCommand(`update-shift-times:${id}`, "/api/v1/companies/{companyId}/manufacturing/update-shift-times");
  const status = useCommand(`set-shift-status:${id}`, "/api/v1/companies/{companyId}/manufacturing/set-shift-status");
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
      <td>{shift.crossesMidnight ? "Cruza la medianoche" : "—"}</td>
      <td>{shift.plantCode}</td>
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
              <button
                type="button"
                disabled={busy}
                onClick={async () => (await status.run({ ...target, status: shift.status === "ACTIVE" ? "INACTIVE" : "ACTIVE" })) && onDone()}
              >
                {shift.status === "ACTIVE" ? "Desactivar" : "Activar"}
              </button>
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
    return <Loading error={plants.error} />;
  }
  if (plants.data.length === 0) {
    return (
      <>
        <h1>Máquinas y turnos</h1>
        <p className="muted">No hay plantas disponibles.</p>
      </>
    );
  }
  return (
    <>
      <h1>Máquinas y turnos</h1>
      <PlantSelect plants={plants.data} value={plantId} onChange={setPlant} />
      {data === null ? (
        <Loading error={error} />
      ) : (
        <>
          <h2>Máquinas</h2>
          {can("production_master:manage") ? <CreateMachine plantId={plantId} onDone={reload} /> : null}
          {data.machines.length === 0 ? (
            <p className="muted">No hay máquinas en la planta.</p>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Código</th>
                  <th>Nombre</th>
                  <th>Planta</th>
                  <th>Estado</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {data.machines.map((m) => (
                  <MachineRow key={`${m.machineId}:${m.version}`} machine={m} onDone={reload} />
                ))}
              </tbody>
            </table>
          )}
          <h2>Turnos</h2>
          {can("production_master:manage") ? <DefineShift plantId={plantId} onDone={reload} /> : null}
          {data.shifts.length === 0 ? (
            <p className="muted">No hay turnos en la planta.</p>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Código</th>
                  <th>Inicia</th>
                  <th>Termina</th>
                  <th>Medianoche</th>
                  <th>Planta</th>
                  <th>Estado</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {data.shifts.map((s) => (
                  <ShiftRow key={`${s.shiftId}:${s.version}`} shift={s} onDone={reload} />
                ))}
              </tbody>
            </table>
          )}
        </>
      )}
    </>
  );
}
