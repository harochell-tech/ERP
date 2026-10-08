"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, NoPermission } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// MFG3-04 (E-MFG3-8…10, E-MFG3-01-3/5): Producción › Mantenimiento preventivo. The Gerente de planta (maintenance_plan:manage) defines
// tasks per machine every N cycles, running hours or days; the mechanic marks them done in the portal (choosing the task's code when
// ending a maintenance) or the manager records them here. Each task shows how much of its interval has gone.

type Task = Schemas["MaintenanceTaskView"];

const KIND: Record<string, string> = { CYCLES: "ciclos", RUNNING_HOURS: "horas en marcha", DAYS: "días" };
const STATE: Record<string, { label: string; tone: string }> = {
  OK: { label: "Al día", tone: "tone-done" },
  POR_VENCER: { label: "Por vencer", tone: "tone-attention" },
  VENCIDA: { label: "Vencida", tone: "tone-error" },
  INACTIVE: { label: "Inactiva", tone: "tone-neutral" },
};

function TaskRow({ task, canManage, onDone }: { task: Task; canManage: boolean; onDone: () => void }) {
  const done = useCommand(`maintenance-done:${task.taskId}`, "/api/v1/companies/{companyId}/manufacturing/record-maintenance-done", `Tarea ${task.code} registrada como hecha.`);
  const status = useCommand(`maintenance-status:${task.taskId}`, "/api/v1/companies/{companyId}/manufacturing/set-maintenance-task-status");
  const state = STATE[task.state] ?? { label: task.state, tone: "tone-neutral" };
  return (
    <tr data-testid={`maintenance-task:${task.code}`}>
      <td>{task.machineCode}</td>
      <td className="mono">{task.code}</td>
      <td className="wrap">
        {task.name}
        {task.instructions ? <div className="muted">{task.instructions}</div> : null}
      </td>
      <td>
        Cada {formatQuantity(String(task.every))} {KIND[task.frequencyKind] ?? task.frequencyKind}
      </td>
      <td className="num">
        {formatQuantity(task.since)} de {task.every}
      </td>
      <td>
        <span className={`badge ${state.tone}`} data-testid={`maintenance-state:${task.code}`}>
          {state.label}
        </span>
      </td>
      <td>{task.lastDoneAt ? `${task.lastDoneAt}${task.done[0]?.source === "PORTAL" ? " (portal)" : ""}` : "Nunca"}</td>
      <td className="actions">
        {canManage && task.status === "ACTIVE" ? (
          <ConfirmAction
            label="Marcar hecha"
            busy={done.busy}
            consequence={`La tarea ${task.code} queda hecha ahora y su contador vuelve a cero.`}
            onConfirm={async () => (await done.run({ taskId: task.taskId, doneAt: new Date().toISOString(), note: null })) && onDone()}
          />
        ) : null}
        {canManage ? (
          <button
            type="button"
            disabled={status.busy}
            onClick={async () => (await status.run({ taskId: task.taskId, expectedVersion: task.version, status: task.status === "ACTIVE" ? "INACTIVE" : "ACTIVE" })) && onDone()}
          >
            {task.status === "ACTIVE" ? "Desactivar" : "Activar"}
          </button>
        ) : null}
        <ErrorBox error={done.error ?? status.error} />
      </td>
    </tr>
  );
}

function DefineTask({ machines, onDone }: { machines: Schemas["MachineView"][]; onDone: () => void }) {
  const define = useCommand("define-maintenance-task", "/api/v1/companies/{companyId}/manufacturing/define-maintenance-task", "Tarea de mantenimiento creada.");
  const [v, setV] = useState({ machineId: "", code: "", name: "", frequencyKind: "CYCLES", every: "", instructions: "" });
  return (
    <form
      className="inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const every = Number(v.every.replace(/\D/g, ""));
        if (
          await define.run({
            machineId: v.machineId,
            code: v.code.trim().toUpperCase(),
            name: v.name.trim(),
            frequencyKind: v.frequencyKind,
            every,
            instructions: v.instructions.trim() || null,
          })
        ) {
          setV({ ...v, code: "", name: "", every: "", instructions: "" });
          onDone();
        }
      }}
    >
      <Field label="Máquina">
        <select value={v.machineId} required onChange={(e) => setV({ ...v, machineId: e.target.value })}>
          <option value="">Elija…</option>
          {machines.map((m) => (
            <option key={m.machineId} value={m.machineId}>
              {m.code} — {m.name}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Código (el mecánico lo elige en el portal)">
        <input value={v.code} required placeholder="ZAPATAS" onChange={(e) => setV({ ...v, code: e.target.value })} />
      </Field>
      <Field label="Tarea">
        <input value={v.name} required placeholder="Cambiar zapatas" onChange={(e) => setV({ ...v, name: e.target.value })} />
      </Field>
      <Field label="Cada">
        <input inputMode="numeric" value={v.every} required placeholder="40000" onChange={(e) => setV({ ...v, every: e.target.value })} />
      </Field>
      <Field label="Unidad">
        <select value={v.frequencyKind} onChange={(e) => setV({ ...v, frequencyKind: e.target.value })}>
          {Object.entries(KIND).map(([k, label]) => (
            <option key={k} value={k}>
              {label}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Instrucciones (opcional)" wide>
        <input value={v.instructions} onChange={(e) => setV({ ...v, instructions: e.target.value })} />
      </Field>
      <button type="submit" className="primary" disabled={define.busy}>
        Crear tarea
      </button>
      <ErrorBox error={define.error} />
    </form>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("production:read");
  const { data, error, reload } = useLoad(
    allowed
      ? async () => {
          const [tasks, machines] = await Promise.all([
            query("/api/v1/companies/{companyId}/manufacturing/maintenance-tasks", { path: { companyId } }),
            query("/api/v1/companies/{companyId}/manufacturing/machines", { path: { companyId } }),
          ]);
          return { tasks, machines: machines.items };
        }
      : null,
    [companyId],
  );
  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const canManage = can("maintenance_plan:manage");
  return (
    <>
      <h1>Mantenimiento preventivo</h1>
      <p className="muted">
        Cada tarea se cuenta desde la última vez que se hizo: por ciclos de la máquina, por horas en marcha (turno sin mantenimientos ni paros) o por días. Al 90 % queda «Por vencer» y
        al 100 % «Vencida». El mecánico la marca hecha en el portal, en Mantenimientos, eligiendo su código.
      </p>
      <p data-testid="maintenance-counts">
        {data.tasks.overdue} vencida(s) · {data.tasks.dueSoon} por vencer
      </p>
      {canManage ? <DefineTask machines={data.machines} onDone={reload} /> : null}
      <div className="table-wrap">
        <table data-testid="maintenance-tasks">
          <thead>
            <tr>
              <th>Máquina</th>
              <th>Código</th>
              <th>Tarea</th>
              <th>Frecuencia</th>
              <th className="num">Transcurrido</th>
              <th>Estado</th>
              <th>Última vez</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.tasks.items.map((t) => (
              <TaskRow key={`${t.taskId}:${t.version}:${t.lastDoneAt ?? ""}`} task={t} canManage={canManage} onDone={reload} />
            ))}
            {data.tasks.items.length === 0 ? (
              <tr>
                <td colSpan={8} className="muted">
                  Todavía no hay tareas de mantenimiento preventivo.
                </td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
    </>
  );
}
