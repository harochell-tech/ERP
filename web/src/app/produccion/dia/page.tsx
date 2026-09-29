"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { PlantSelect, useChosenPlant, usePlants } from "@/components/Production";
import { ErrorBox, Field, Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// MFG1-07 (E-MFG1-07-2): the production day of a plant — its runs and the materials consumed against the recipes' theory. The
// Supervisor de producción starts a run (machine, shift, product with an active recipe on that machine, business date).

function StartRun({ plantId, businessDate, onDone }: { plantId: string; businessDate: string; onDone: (runId: string) => void }) {
  const { companyId } = useSession();
  const start = useCommand("start-production-run", "/api/v1/companies/{companyId}/manufacturing/start-production-run");
  const { data, error } = useLoad(async () => {
    const [machines, shifts, recipes] = await Promise.all([
      query("/api/v1/companies/{companyId}/manufacturing/machines", { path: { companyId }, query: { plantId, status: "ACTIVE" } }),
      query("/api/v1/companies/{companyId}/manufacturing/shifts", { path: { companyId }, query: { plantId, status: "ACTIVE" } }),
      query("/api/v1/companies/{companyId}/manufacturing/recipes", { path: { companyId }, query: { plantId, status: "ACTIVE" } }),
    ]);
    return { machines: machines.items, shifts: shifts.items, recipes: recipes.items };
  }, [companyId, plantId]);
  const [form, setForm] = useState({ machineId: "", shiftId: "", itemId: "", date: "" });
  const [invalid, setInvalid] = useState<string | null>(null);
  if (data === null) {
    return <Loading error={error} />;
  }
  const date = form.date || businessDate;
  const products = data.recipes.filter((r) => r.machineId === form.machineId);
  return (
    <form
      className="card"
      onSubmit={async (e) => {
        e.preventDefault();
        if (!form.machineId || !form.shiftId || !form.itemId || !date) {
          setInvalid("Elija máquina, turno, producto y fecha.");
          return;
        }
        setInvalid(null);
        const response = await start.run({ plantId, machineId: form.machineId, shiftId: form.shiftId, itemId: form.itemId, businessDate: date });
        if (response) {
          setForm({ machineId: "", shiftId: "", itemId: "", date: "" });
          onDone(response.resultRef);
        }
      }}
    >
      <h2>Iniciar corrida</h2>
      <Field label="Máquina">
        <select aria-label="Máquina" value={form.machineId} onChange={(e) => setForm({ ...form, machineId: e.target.value, itemId: "" })}>
          <option value="">—</option>
          {data.machines.map((m) => (
            <option key={m.machineId} value={m.machineId}>
              {m.code} — {m.name}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Turno">
        <select aria-label="Turno" value={form.shiftId} onChange={(e) => setForm({ ...form, shiftId: e.target.value })}>
          <option value="">—</option>
          {data.shifts.map((s) => (
            <option key={s.shiftId} value={s.shiftId}>
              {s.code} ({s.startsAt.slice(0, 5)}–{s.endsAt.slice(0, 5)})
            </option>
          ))}
        </select>
      </Field>
      <Field label="Producto">
        <select aria-label="Producto" value={form.itemId} onChange={(e) => setForm({ ...form, itemId: e.target.value })}>
          <option value="">{form.machineId ? "—" : "Elija la máquina primero"}</option>
          {products.map((r) => (
            <option key={r.recipeVersionId} value={r.itemId}>
              {r.itemCode} — {r.itemName}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Fecha de producción">
        <input type="date" value={date} onChange={(e) => setForm({ ...form, date: e.target.value })} />
      </Field>
      <button type="submit" disabled={start.busy}>
        Iniciar corrida
      </button>
      {form.machineId && products.length === 0 ? <p className="muted">La máquina no tiene recetas activas.</p> : null}
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={start.error} />
    </form>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const plants = usePlants();
  const { plant, setPlant } = useChosenPlant(plants.data);
  const plantId = plant?.plantId ?? "";
  const [businessDate, setBusinessDate] = useState(() => todayInDominicanRepublic());
  const [started, setStarted] = useState<string | null>(null);
  const { data, error, reload } = useLoad(
    can("production:read") && plantId && businessDate
      ? () => query("/api/v1/companies/{companyId}/manufacturing/production-day", { path: { companyId }, query: { plantId, businessDate } })
      : null,
    [companyId, plantId, businessDate],
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
        <h1>Producción del día</h1>
        <p className="muted">No hay plantas con producción.</p>
      </>
    );
  }
  return (
    <>
      <h1>Producción del día</h1>
      <div>
        <PlantSelect plants={plants.data} value={plantId} onChange={setPlant} />
        <Field label="Día">
          <input type="date" value={businessDate} onChange={(e) => setBusinessDate(e.target.value)} />
        </Field>
      </div>
      {can("production_run:manage") ? (
        <StartRun
          plantId={plantId}
          businessDate={businessDate}
          onDone={(runId) => {
            setStarted(runId);
            reload();
          }}
        />
      ) : null}
      {started ? (
        <p className="notice" data-testid="run-started">
          Corrida iniciada. <Link href={`/produccion/corrida/?id=${started}`}>Abrir la corrida</Link>
        </p>
      ) : null}
      {data === null ? (
        <Loading error={error} />
      ) : (
        <>
          <h2>Corridas</h2>
          {data.runs.length === 0 ? (
            <p className="muted">No hay corridas ese día.</p>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Corrida</th>
                  <th>Máquina</th>
                  <th>Turno</th>
                  <th>Producto</th>
                  <th>Estado</th>
                  <th>Resumen</th>
                  <th className="num">Unidades buenas</th>
                  <th className="num">Scrap de mezcla</th>
                  <th className="num">Scrap fresco</th>
                  <th>Lote</th>
                </tr>
              </thead>
              <tbody>
                {data.runs.map((r) => (
                  <tr key={r.runId}>
                    <td className="mono">
                      <Link href={`/produccion/corrida/?id=${r.runId}`}>{r.runNo}</Link>
                    </td>
                    <td className="mono">{r.machineCode}</td>
                    <td className="mono">{r.shiftCode}</td>
                    <td>{r.itemCode}</td>
                    <td>
                      <StatusBadge status={r.status} />
                    </td>
                    <td>{r.summaryStatus ? <StatusBadge status={r.summaryStatus} /> : "—"}</td>
                    <td className="num">{formatQuantity(r.goodUnits)}</td>
                    <td className="num">{formatQuantity(r.mixScrapUnits)}</td>
                    <td className="num">{formatQuantity(r.freshScrapUnits)}</td>
                    <td>
                      {r.lotCode ? (
                        <>
                          <span className="mono">{r.lotCode}</span> {r.lotStatus ? <StatusBadge status={r.lotStatus} /> : null}
                        </>
                      ) : (
                        "—"
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <p>
            Unidades buenas del día: <span className="mono" data-testid="day-good-units">{formatQuantity(data.goodUnits)}</span>
          </p>
          <h2>Materiales: real contra teórico</h2>
          {data.materials.length === 0 ? (
            <p className="muted">Sin consumos registrados.</p>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Material</th>
                  <th>Unidad</th>
                  <th className="num">Real</th>
                  <th className="num">Teórico</th>
                  <th className="num">Diferencia</th>
                </tr>
              </thead>
              <tbody>
                {data.materials.map((m) => (
                  <tr key={m.materialItemId}>
                    <td>{m.materialCode}</td>
                    <td>{m.baseUom}</td>
                    <td className="num">{formatQuantity(m.qty)}</td>
                    <td className="num">{formatQuantity(m.theoreticalQty)}</td>
                    <td className="num">{formatQuantity(m.difference)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      )}
    </>
  );
}
