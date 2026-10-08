"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, NoPermission } from "@/components/ui";
import { formatDecimal, fractionToPercent, isPositiveDecimal, normalizeInput, percentToFraction } from "@/lib/decimal";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// LAB1-01 (E-LAB1-3, E-LAB1-01-2/4): Calidad › Requisitos por ítem. Calidad (lab_spec:manage) saves, in one step, each finished good's
// lot prefix, nominal measures, net area and 28-day minimums — every save is a new version — and gives each machine its short code.
// Both make up a lot's field code (8070325P1); lots that were waiting for either get theirs at once.

type Spec = Schemas["ItemSpecView"];
type SpecValues = { lotPrefix: string; width: string; height: string; length: string; netPercent: string; minAvg: string; minOne: string };

function SpecForm({ spec, onDone }: { spec: Spec; onDone: () => void }) {
  // Saving asks for step-up: what was typed comes back after signing in again (E-PR18b-6).
  const save = useCommand<"/api/v1/companies/{companyId}/manufacturing/set-item-spec", SpecValues>(
    `item-spec:${spec.itemId}`,
    "/api/v1/companies/{companyId}/manufacturing/set-item-spec",
    `Requisitos de ${spec.itemCode} guardados.`,
  );
  const [v, setV] = useState<SpecValues>(save.restored ?? {
    lotPrefix: spec.lotPrefix ?? "",
    width: spec.nominalWidthCm ? formatDecimal(spec.nominalWidthCm, 1) : "",
    height: spec.nominalHeightCm ? formatDecimal(spec.nominalHeightCm, 1) : "",
    length: spec.nominalLengthCm ? formatDecimal(spec.nominalLengthCm, 1) : "",
    netPercent: fractionToPercent(spec.netAreaFraction) ?? "",
    minAvg: spec.minAvg28d ? formatDecimal(spec.minAvg28d, 0) : "",
    minOne: spec.minIndividual28d ? formatDecimal(spec.minIndividual28d, 0) : "",
  });
  const [open, setOpen] = useState(save.wasRestored);
  const optional = (value: string) => (normalizeInput(value) === "" ? null : normalizeInput(value));
  const valid =
    /^[A-Za-z0-9]{1,4}$/.test(v.lotPrefix.trim()) &&
    [v.width, v.height, v.length].every((x) => isPositiveDecimal(normalizeInput(x), 6)) &&
    [v.netPercent, v.minAvg, v.minOne].every((x) => normalizeInput(x) === "" || isPositiveDecimal(normalizeInput(x), 4));
  if (!open) {
    return (
      <button type="button" onClick={() => setOpen(true)}>
        {spec.version ? "Cambiar" : "Cargar requisitos"}
      </button>
    );
  }
  return (
    <form
      className="inline-form"
      data-testid={`item-spec-form:${spec.itemCode}`}
      onSubmit={async (e) => {
        e.preventDefault();
        if (
          await save.run(
            {
              itemId: spec.itemId,
              lotPrefix: v.lotPrefix.trim().toUpperCase(),
              nominalWidthCm: normalizeInput(v.width),
              nominalHeightCm: normalizeInput(v.height),
              nominalLengthCm: normalizeInput(v.length),
              netAreaFraction: percentToFraction(optional(v.netPercent)),
              minAvg28d: optional(v.minAvg),
              minIndividual28d: optional(v.minOne),
            },
            v,
          )
        ) {
          setOpen(false);
          onDone();
        }
      }}
    >
      <Field label="Prefijo de lote" required hint="4, 6, 8…">
        <input value={v.lotPrefix} maxLength={4} required onChange={(e) => setV({ ...v, lotPrefix: e.target.value })} />
      </Field>
      <Field label="Ancho nominal (cm)" required>
        <input inputMode="decimal" value={v.width} required onChange={(e) => setV({ ...v, width: e.target.value })} />
      </Field>
      <Field label="Alto nominal (cm)" required>
        <input inputMode="decimal" value={v.height} required onChange={(e) => setV({ ...v, height: e.target.value })} />
      </Field>
      <Field label="Largo nominal (cm)" required>
        <input inputMode="decimal" value={v.length} required onChange={(e) => setV({ ...v, length: e.target.value })} />
      </Field>
      <Field label="Área neta (%)">
        <input inputMode="decimal" value={v.netPercent} onChange={(e) => setV({ ...v, netPercent: e.target.value })} />
      </Field>
      <Field label="Mínimo del promedio a 28 d (kg/cm²)">
        <input inputMode="decimal" value={v.minAvg} onChange={(e) => setV({ ...v, minAvg: e.target.value })} />
      </Field>
      <Field label="Mínimo individual a 28 d (kg/cm²)">
        <input inputMode="decimal" value={v.minOne} onChange={(e) => setV({ ...v, minOne: e.target.value })} />
      </Field>
      <button type="submit" className="primary" disabled={save.busy || !valid}>
        Guardar requisitos
      </button>
      <button type="button" onClick={() => setOpen(false)}>
        Cancelar
      </button>
      <ErrorBox error={save.error} />
    </form>
  );
}

function ShortCode({ machine, canManage, onDone }: { machine: Schemas["MachineView"]; canManage: boolean; onDone: () => void }) {
  const save = useCommand(`machine-short-code:${machine.machineId}`, "/api/v1/companies/{companyId}/manufacturing/set-machine-short-code", `Código corto de ${machine.code} guardado.`);
  const [code, setCode] = useState(machine.shortCode ?? "");
  return (
    <tr data-testid={`machine-short-code:${machine.code}`}>
      <td className="mono">{machine.code}</td>
      <td className="wrap">{machine.name}</td>
      <td>
        {canManage ? (
          <form
            className="inline-form"
            onSubmit={async (e) => {
              e.preventDefault();
              if (await save.run({ plantId: machine.plantId, machineId: machine.machineId, expectedVersion: machine.version, shortCode: code.trim().toUpperCase() })) {
                onDone();
              }
            }}
          >
            <input aria-label={`Código corto de ${machine.code}`} value={code} maxLength={6} placeholder="P1" required onChange={(e) => setCode(e.target.value)} />
            <button type="submit" disabled={save.busy || !/^[A-Za-z0-9]{1,6}$/.test(code.trim()) || code.trim().toUpperCase() === machine.shortCode}>
              Guardar
            </button>
            <ErrorBox error={save.error} />
          </form>
        ) : (
          <span className="mono">{machine.shortCode ?? "—"}</span>
        )}
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("lab:read");
  const seesMachines = can("production:read");
  const { data, error, reload } = useLoad(
    allowed
      ? async () => {
          const [specs, machines] = await Promise.all([
            query("/api/v1/companies/{companyId}/manufacturing/lab/item-specs", { path: { companyId } }),
            seesMachines ? query("/api/v1/companies/{companyId}/manufacturing/machines", { path: { companyId } }) : Promise.resolve({ items: [] as Schemas["MachineView"][] }),
          ]);
          return { specs: specs.items, machines: machines.items };
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
  const canManage = can("lab_spec:manage");
  return (
    <>
      <h1>Requisitos por ítem</h1>
      <p className="muted">
        El prefijo y el código corto de la máquina forman el código de campo del lote (prefijo + día/mes/año + máquina, por ejemplo 8070325P1). Las medidas nominales se usan cuando una
        probeta no trae las suyas. Sin mínimo a 28 días, el veredicto del lote es «Sin requisito». Cada cambio queda como una versión nueva.
      </p>
      <div className="table-wrap">
        <table data-testid="item-specs">
          <thead>
            <tr>
              <th>Ítem</th>
              <th>Prefijo</th>
              <th>Nominal (cm)</th>
              <th className="num">Área neta</th>
              <th className="num">Mín. promedio 28 d</th>
              <th className="num">Mín. individual 28 d</th>
              <th>Versión</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.specs.map((s) => (
              <tr key={`${s.itemId}:${s.version ?? 0}`} data-testid={`item-spec:${s.itemCode}`}>
                <td className="wrap">
                  <span className="mono">{s.itemCode}</span> {s.itemDescription}
                </td>
                <td className="mono">{s.lotPrefix ?? "—"}</td>
                <td>{s.version ? `${formatDecimal(s.nominalWidthCm, 1)} × ${formatDecimal(s.nominalHeightCm, 1)} × ${formatDecimal(s.nominalLengthCm, 1)}` : "—"}</td>
                <td className="num">{s.netAreaFraction ? `${fractionToPercent(s.netAreaFraction)} %` : "—"}</td>
                <td className="num">{s.minAvg28d ? formatDecimal(s.minAvg28d) : "Sin requisito"}</td>
                <td className="num">{s.minIndividual28d ? formatDecimal(s.minIndividual28d) : "—"}</td>
                <td>{s.version ? `v${s.version} · ${s.setBy ?? ""}` : "Sin cargar"}</td>
                <td className="actions">{canManage ? <SpecForm spec={s} onDone={reload} /> : null}</td>
              </tr>
            ))}
            {data.specs.length === 0 ? (
              <tr>
                <td colSpan={8} className="muted">
                  No hay productos terminados activos.
                </td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
      {seesMachines ? (
        <>
          <h2>Código corto de las máquinas</h2>
          <div className="table-wrap">
            <table data-testid="machine-short-codes">
              <thead>
                <tr>
                  <th>Máquina</th>
                  <th>Nombre</th>
                  <th>Código corto</th>
                </tr>
              </thead>
              <tbody>
                {data.machines.map((m) => (
                  <ShortCode key={`${m.machineId}:${m.version}`} machine={m} canManage={canManage} onDone={reload} />
                ))}
              </tbody>
            </table>
          </div>
        </>
      ) : null}
    </>
  );
}
