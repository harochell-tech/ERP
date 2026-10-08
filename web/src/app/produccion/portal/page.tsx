"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { itemLabel, useItems, usePlants } from "@/components/Production";
import { ConfirmAction, ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
import { formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// MFG2-03 (E-MFG2-3, E-MFG2-01-3/4/7): Producción › Portal. The machines' portal (industriasrochell.com.do) sends each block machine's
// cycles and the batch plants' consumption; Core reads it every 15 minutes and prepares the shift summaries as drafts. Here the
// Gerente de planta (portal:manage) pairs what the portal calls each thing with Core's; everyone who reads production sees the
// pairings, the connection's state and what could not be imported.

type Setup = Schemas["PortalSetupView"];

function Remove({ kind, keyValue, what, onDone }: { kind: string; keyValue: string; what: string; onDone: () => void }) {
  const remove = useCommand(`remove-portal:${kind}:${keyValue}`, "/api/v1/companies/{companyId}/manufacturing/remove-portal-pairing", `Equivalencia de ${what} quitada.`);
  return (
    <>
      <ConfirmAction
        label="Quitar"
        danger
        busy={remove.busy}
        consequence={`Lo que el portal envíe de ${what} dejará de importarse hasta que se vuelva a emparejar.`}
        onConfirm={async () => (await remove.run({ kind, key: keyValue })) && onDone()}
      />
      <ErrorBox error={remove.error} />
    </>
  );
}

function Status({ setup }: { setup: Setup }) {
  return (
    <section className="card" data-testid="portal-status">
      <h2 style={{ marginTop: 0 }}>Conexión</h2>
      <dl className="facts">
        <dt>Última lectura buena</dt>
        <dd data-testid="portal-last-ok">{setup.lastOkAt ? formatDateTime(setup.lastOkAt) : "Todavía ninguna"}</dd>
        {setup.lastError ? (
          <>
            <dt>Último error</dt>
            <dd className="error-text" data-testid="portal-last-error">
              {formatDateTime(setup.lastErrorAt)}: {setup.lastError}
            </dd>
          </>
        ) : null}
        <dt>Resúmenes sin consumo de dosificadora</dt>
        <dd data-testid="portal-pending">{setup.pendingConsumption}</dd>
        <dt>Resúmenes con consumo fuera de tolerancia</dt>
        <dd data-testid="portal-out-of-tolerance">{setup.outOfTolerance}</dd>
      </dl>
      {setup.unpairedMachines.length > 0 ? (
        <p className="notice" data-testid="portal-unpaired">
          El portal envía datos de máquinas sin emparejar: {setup.unpairedMachines.join(", ")}.
        </p>
      ) : null}
      {setup.warnings.length > 0 ? (
        <>
          <h3>Avisos de la última lectura</h3>
          <ul data-testid="portal-warnings">
            {setup.warnings.map((w) => (
              <li key={w}>{w}</li>
            ))}
          </ul>
        </>
      ) : null}
    </section>
  );
}

function Machines({ setup, canManage, onDone }: { setup: Setup; canManage: boolean; onDone: () => void }) {
  const { companyId } = useSession();
  const machines = useLoad(() => query("/api/v1/companies/{companyId}/manufacturing/machines", { path: { companyId } }), [companyId]);
  const set = useCommand("set-portal-machine", "/api/v1/companies/{companyId}/manufacturing/set-portal-machine", "Máquina emparejada.");
  const [v, setV] = useState({ portalCode: "", machineId: "", batchPlant: "" });
  return (
    <section className="card">
      <h2 style={{ marginTop: 0 }}>Máquinas</h2>
      <p className="muted">Cada máquina del portal (planta1, planta2…) con su máquina de Core y la dosificadora que la alimenta. Sin dosificadora conectada, el Supervisor escribe el consumo.</p>
      <div className="table-wrap">
        <table data-testid="portal-machines">
          <thead>
            <tr>
              <th>En el portal</th>
              <th>Máquina de Core</th>
              <th>Dosificadora</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {setup.machines.map((m) => (
              <tr key={m.portalCode}>
                <td className="mono">{m.portalCode}</td>
                <td>{m.machineCode}</td>
                <td>{m.batchPlant ?? <span className="muted">Sin conexión: consumo a mano</span>}</td>
                <td>{canManage ? <Remove kind="MACHINE" keyValue={m.portalCode} what={`la máquina ${m.portalCode}`} onDone={onDone} /> : null}</td>
              </tr>
            ))}
            {setup.machines.length === 0 ? (
              <tr>
                <td colSpan={4} className="muted">
                  Sin máquinas emparejadas: no se importa nada.
                </td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
      {canManage ? (
        <form
          className="inline-form"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await set.run({ portalCode: v.portalCode, machineId: v.machineId, batchPlant: v.batchPlant.trim() || null })) {
              setV({ portalCode: "", machineId: "", batchPlant: "" });
              onDone();
            }
          }}
        >
          <Field label="En el portal">
            <input value={v.portalCode} placeholder="planta2" required onChange={(e) => setV({ ...v, portalCode: e.target.value })} />
          </Field>
          <Field label="Máquina de Core">
            <select value={v.machineId} required onChange={(e) => setV({ ...v, machineId: e.target.value })}>
              <option value="">Elija…</option>
              {(machines.data?.items ?? []).map((m) => (
                <option key={m.machineId} value={m.machineId}>
                  {m.code} — {m.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Dosificadora (vacío: sin conexión)">
            <input value={v.batchPlant} placeholder="dosificadora2" onChange={(e) => setV({ ...v, batchPlant: e.target.value })} />
          </Field>
          <button type="submit" className="primary" disabled={set.busy}>
            Emparejar máquina
          </button>
          <ErrorBox error={set.error} />
        </form>
      ) : null}
    </section>
  );
}

function Moulds({ setup, canManage, onDone }: { setup: Setup; canManage: boolean; onDone: () => void }) {
  const items = useItems();
  const set = useCommand("set-portal-mould", "/api/v1/companies/{companyId}/manufacturing/set-portal-mould", "Molde emparejado.");
  const [v, setV] = useState({ mould: "", itemId: "" });
  const goods = (items.data ?? []).filter((i) => i.itemType === "FINISHED_GOOD");
  return (
    <section className="card">
      <h2 style={{ marginTop: 0 }}>Moldes</h2>
      <p className="muted">El molde que la máquina tiene puesto (4, 6 u 8 pulgadas) decide qué producto se fabricó.</p>
      <div className="table-wrap">
        <table data-testid="portal-moulds">
          <thead>
            <tr>
              <th>Molde</th>
              <th>Producto</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {setup.moulds.map((m) => (
              <tr key={m.mould}>
                <td>{m.mould} pulg.</td>
                <td>
                  {m.itemCode} — {m.itemDescription}
                </td>
                <td>{canManage ? <Remove kind="MOULD" keyValue={m.mould} what={`el molde ${m.mould}"`} onDone={onDone} /> : null}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {canManage ? (
        <form
          className="inline-form"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await set.run({ mould: v.mould, itemId: v.itemId })) {
              setV({ mould: "", itemId: "" });
              onDone();
            }
          }}
        >
          <Field label="Molde (pulgadas)">
            <input value={v.mould} placeholder="6" required inputMode="numeric" onChange={(e) => setV({ ...v, mould: e.target.value })} />
          </Field>
          <Field label="Producto">
            <select value={v.itemId} required onChange={(e) => setV({ ...v, itemId: e.target.value })}>
              <option value="">Elija…</option>
              {goods.map((i) => (
                <option key={i.itemId} value={i.itemId}>
                  {itemLabel(i)}
                </option>
              ))}
            </select>
          </Field>
          <button type="submit" className="primary" disabled={set.busy}>
            Emparejar molde
          </button>
          <ErrorBox error={set.error} />
        </form>
      ) : null}
    </section>
  );
}

function Shifts({ setup, canManage, onDone }: { setup: Setup; canManage: boolean; onDone: () => void }) {
  const { companyId } = useSession();
  const shifts = useLoad(() => query("/api/v1/companies/{companyId}/manufacturing/shifts", { path: { companyId } }), [companyId]);
  const set = useCommand("set-portal-shift", "/api/v1/companies/{companyId}/manufacturing/set-portal-shift", "Turno emparejado.");
  const [v, setV] = useState({ shiftNo: "1", shiftId: "" });
  return (
    <section className="card">
      <h2 style={{ marginTop: 0 }}>Turnos</h2>
      <p className="muted">El número de turno del portal decide el turno de Core, aunque el horario del portal cambie según la máquina y el día.</p>
      <div className="table-wrap">
        <table data-testid="portal-shifts">
          <thead>
            <tr>
              <th>Turno del portal</th>
              <th>Turno de Core</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {setup.shifts.map((s) => (
              <tr key={s.shiftNo}>
                <td>Turno {s.shiftNo}</td>
                <td>
                  {s.shiftCode} ({s.startsAt.slice(0, 5)}–{s.endsAt.slice(0, 5)})
                </td>
                <td>{canManage ? <Remove kind="SHIFT" keyValue={String(s.shiftNo)} what={`el turno ${s.shiftNo}`} onDone={onDone} /> : null}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {canManage ? (
        <form
          className="inline-form"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await set.run({ shiftNo: Number(v.shiftNo), shiftId: v.shiftId })) {
              onDone();
            }
          }}
        >
          <Field label="Turno del portal">
            <select value={v.shiftNo} onChange={(e) => setV({ ...v, shiftNo: e.target.value })}>
              {[1, 2, 3].map((n) => (
                <option key={n} value={n}>
                  Turno {n}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Turno de Core">
            <select value={v.shiftId} required onChange={(e) => setV({ ...v, shiftId: e.target.value })}>
              <option value="">Elija…</option>
              {(shifts.data?.items ?? []).map((s) => (
                <option key={s.shiftId} value={s.shiftId}>
                  {s.plantCode} · {s.code} ({s.startsAt.slice(0, 5)}–{s.endsAt.slice(0, 5)})
                </option>
              ))}
            </select>
          </Field>
          <button type="submit" className="primary" disabled={set.busy}>
            Emparejar turno
          </button>
          <ErrorBox error={set.error} />
        </form>
      ) : null}
    </section>
  );
}

function Materials({ setup, canManage, onDone }: { setup: Setup; canManage: boolean; onDone: () => void }) {
  const items = useItems();
  const plants = usePlants();
  const set = useCommand("set-portal-material", "/api/v1/companies/{companyId}/manufacturing/set-portal-material", "Material emparejado.");
  const [v, setV] = useState({ batchPlant: "", materialCode: "", itemId: "", uom: "", locationId: "" });
  const raw = (items.data ?? []).filter((i) => i.itemType === "RAW_MATERIAL");
  const item = raw.find((i) => i.itemId === v.itemId);
  const units = item ? [item.baseUom, ...item.conversions.filter((c) => c.toUom === item.baseUom && !c.effectiveTo).map((c) => c.fromUom)] : [];
  const locations = (plants.data ?? []).flatMap((p) => p.locations.filter((l) => l.status !== "INACTIVE").map((l) => ({ ...l, plantCode: p.code })));
  return (
    <section className="card">
      <h2 style={{ marginTop: 0 }}>Materiales de la dosificadora</h2>
      <p className="muted">Cada material que envía la dosificadora, con su artículo, la unidad en que lo envía y de dónde se descarga (silo o patio).</p>
      <div className="table-wrap">
        <table data-testid="portal-materials">
          <thead>
            <tr>
              <th>Dosificadora</th>
              <th>Código</th>
              <th>Artículo</th>
              <th>Unidad</th>
              <th>Se descarga de</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {setup.materials.map((m) => (
              <tr key={`${m.batchPlant}/${m.materialCode}`}>
                <td>{m.batchPlant}</td>
                <td className="mono">{m.materialCode}</td>
                <td>{m.itemCode}</td>
                <td>{m.uom}</td>
                <td>{m.locationCode}</td>
                <td>{canManage ? <Remove kind="MATERIAL" keyValue={`${m.batchPlant}/${m.materialCode}`} what={`${m.materialCode} de ${m.batchPlant}`} onDone={onDone} /> : null}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {canManage ? (
        <form
          className="inline-form"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await set.run({ ...v })) {
              setV({ ...v, materialCode: "", itemId: "", uom: "" });
              onDone();
            }
          }}
        >
          <Field label="Dosificadora">
            <input value={v.batchPlant} placeholder="dosificadora2" required onChange={(e) => setV({ ...v, batchPlant: e.target.value })} />
          </Field>
          <Field label="Código del material">
            <input value={v.materialCode} placeholder="CEMENTO" required onChange={(e) => setV({ ...v, materialCode: e.target.value })} />
          </Field>
          <Field label="Artículo">
            <select value={v.itemId} required onChange={(e) => setV({ ...v, itemId: e.target.value, uom: raw.find((i) => i.itemId === e.target.value)?.baseUom ?? "" })}>
              <option value="">Elija…</option>
              {raw.map((i) => (
                <option key={i.itemId} value={i.itemId}>
                  {itemLabel(i)}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Unidad que envía">
            <select value={v.uom} required onChange={(e) => setV({ ...v, uom: e.target.value })}>
              {units.map((u) => (
                <option key={u} value={u}>
                  {u}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Se descarga de">
            <select value={v.locationId} required onChange={(e) => setV({ ...v, locationId: e.target.value })}>
              <option value="">Elija…</option>
              {locations.map((l) => (
                <option key={l.locationId} value={l.locationId}>
                  {l.plantCode} · {l.code}
                  {l.name ? ` — ${l.name}` : ""}
                </option>
              ))}
            </select>
          </Field>
          <button type="submit" className="primary" disabled={set.busy}>
            Emparejar material
          </button>
          <ErrorBox error={set.error} />
        </form>
      ) : null}
    </section>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("production:read");
  const { data, error, reload } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/manufacturing/portal", { path: { companyId } }) : null, [companyId]);
  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const canManage = can("portal:manage");
  return (
    <>
      <h1>Portal de máquinas</h1>
      <p className="muted">
        Core lee el portal cada 15 minutos y prepara el resumen de cada turno como borrador: unidades según los ciclos y el molde, y el consumo de la dosificadora cuando
        termina el turno. El Supervisor revisa y anota los rotos; el Gerente de planta contabiliza.
      </p>
      <Status setup={data} />
      <Machines setup={data} canManage={canManage} onDone={reload} />
      <Moulds setup={data} canManage={canManage} onDone={reload} />
      <Shifts setup={data} canManage={canManage} onDone={reload} />
      <Materials setup={data} canManage={canManage} onDone={reload} />
    </>
  );
}
