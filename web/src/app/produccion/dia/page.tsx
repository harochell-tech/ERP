"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { PlantSelect, ProductionBadge, Quantity, useChosenPlant, usePlants } from "@/components/Production";
import { EmptyState, LoadingIndicator, StepsHelp } from "@/components/StateNotices";
import { ErrorBox, Field, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { dayFromQuery, lotHref, runNextStep, signedPercent, signedQuantity, toleranceText, uomLabel, varianceMark } from "@/lib/ux4bProduction";

// MFG1-07 (E-MFG1-07-2): the production day of a plant — its runs and the materials consumed against the recipes' theory. The
// Supervisor de producción starts a run (machine, shift, product with an active recipe on that machine, business date).
// UX4-03: the form's date is the day shown (P-13), the form is folded behind a button (P-18/19), a totals card (P-15, the server's
// sums), the next step per run (P-14), variances with sign, % and the tolerance mark (P-16), lot links (P-17), merma (E-UX4-14).

function StartRun({
  plantId,
  businessDate,
  onDateChange,
  onDone,
}: {
  plantId: string;
  businessDate: string;
  onDateChange: (date: string) => void;
  onDone: (runId: string) => void;
}) {
  const { companyId } = useSession();
  const start = useCommand("start-production-run", "/api/v1/companies/{companyId}/manufacturing/start-production-run", (_, doc) =>
    doc ? `Corrida ${doc} iniciada.` : "Corrida iniciada.",
  );
  const { data, error } = useLoad(async () => {
    const [machines, shifts, recipes] = await Promise.all([
      query("/api/v1/companies/{companyId}/manufacturing/machines", { path: { companyId }, query: { plantId, status: "ACTIVE" } }),
      query("/api/v1/companies/{companyId}/manufacturing/shifts", { path: { companyId }, query: { plantId, status: "ACTIVE" } }),
      query("/api/v1/companies/{companyId}/manufacturing/recipes", { path: { companyId }, query: { plantId, status: "ACTIVE" } }),
    ]);
    return { machines: machines.items, shifts: shifts.items, recipes: recipes.items };
  }, [companyId, plantId]);
  const [form, setForm] = useState({ machineId: "", shiftId: "", itemId: "" });
  const fe = useFieldErrors<"machineId" | "shiftId" | "itemId" | "date">();
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const products = data.recipes.filter((r) => r.machineId === form.machineId);
  return (
    <form
      className="card ux4b-fold"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const valid = fe.check({
          machineId: !form.machineId && "Elija la máquina.",
          shiftId: !form.shiftId && "Elija el turno.",
          itemId: !form.itemId && (form.machineId ? "Elija el producto." : "Elija la máquina y luego el producto."),
          date: !businessDate && "Indique la fecha de producción.",
        });
        if (!valid) {
          return;
        }
        const response = await start.run({ plantId, machineId: form.machineId, shiftId: form.shiftId, itemId: form.itemId, businessDate });
        if (response) {
          setForm({ machineId: "", shiftId: "", itemId: "" });
          onDone(response.resultRef);
        }
      }}
    >
      <h2 style={{ marginTop: 0 }}>Iniciar corrida</h2>
      <Field label="Máquina" required error={fe.errors.machineId}>
        <select aria-label="Máquina" value={form.machineId} onChange={(e) => setForm({ ...form, machineId: e.target.value, itemId: "" })}>
          <option value="">Seleccione…</option>
          {data.machines.map((m) => (
            <option key={m.machineId} value={m.machineId}>
              {m.code} — {m.name}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Turno" required error={fe.errors.shiftId}>
        <select aria-label="Turno" value={form.shiftId} onChange={(e) => setForm({ ...form, shiftId: e.target.value })}>
          <option value="">Seleccione…</option>
          {data.shifts.map((s) => (
            <option key={s.shiftId} value={s.shiftId}>
              {s.code} ({s.startsAt.slice(0, 5)}–{s.endsAt.slice(0, 5)})
            </option>
          ))}
        </select>
      </Field>
      <Field label="Producto" required error={fe.errors.itemId} hint="Solo los productos con receta activa en esa máquina.">
        <select aria-label="Producto" value={form.itemId} onChange={(e) => setForm({ ...form, itemId: e.target.value })}>
          <option value="">{form.machineId ? "Seleccione…" : "Elija la máquina primero"}</option>
          {products.map((r) => (
            <option key={r.recipeVersionId} value={r.itemId}>
              {r.itemCode === r.itemName ? r.itemCode : `${r.itemCode} — ${r.itemName}`}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Fecha de producción" required error={fe.errors.date} hint="Es el día que muestra esta pantalla: cambiarla cambia el día de la lista.">
        <input type="date" value={businessDate} onChange={(e) => onDateChange(e.target.value)} />
      </Field>
      {form.machineId && products.length === 0 ? <p className="muted">La máquina no tiene recetas activas: prepare y apruebe una en Recetas.</p> : null}
      <ErrorBox error={start.error} />
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={start.busy}>
          Iniciar corrida
        </button>
      </div>
    </form>
  );
}

/**
 * UX3-02 (E-UX3-13): the plant's shift summaries still in draft, whatever their day — what Inicio's "Resúmenes en borrador" and
 * "Resúmenes por cerrar" count (the runs list, IN_PROGRESS with a DRAFT summary).
 */
function DraftSummaries({ plantId }: { plantId: string }) {
  const { companyId } = useSession();
  const { data } = useLoad(
    plantId ? () => query("/api/v1/companies/{companyId}/manufacturing/runs", { path: { companyId }, query: { plantId, status: "IN_PROGRESS", limit: 200 } }) : null,
    [companyId, plantId],
  );
  const drafts = (data?.items ?? []).filter((r) => r.summaryStatus === "DRAFT");
  if (drafts.length === 0) {
    return null;
  }
  return (
    <section className="card" id="resumenes-borrador" data-testid="draft-summaries">
      <h2 style={{ marginTop: 0 }}>Resúmenes en borrador (por cerrar)</h2>
      <ul className="plain-list">
        {drafts.map((r) => (
          <li key={r.runId}>
            <Link href={`/produccion/corrida/?id=${r.runId}`}>{r.runNo}</Link> · {formatDate(r.businessDate)} · {r.machineCode} · {r.shiftCode} · {r.itemCode}
          </li>
        ))}
      </ul>
    </section>
  );
}

function ProductionDay() {
  const { companyId, can } = useSession();
  const params = useSearchParams();
  const plants = usePlants();
  const { plant, setPlant } = useChosenPlant(plants.data);
  const plantId = plant?.plantId ?? "";
  const [businessDate, setBusinessDate] = useState(() => dayFromQuery(params.get("dia")) ?? todayInDominicanRepublic());
  const [starting, setStarting] = useState(false);
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
    return <LoadingIndicator error={plants.error} />;
  }
  if (plants.data.length === 0) {
    return (
      <>
        <h1>Producción del día</h1>
        <EmptyState title="No hay plantas con producción.">
          <p>Las máquinas y los turnos de una planta se definen en Máquinas y turnos.</p>
        </EmptyState>
      </>
    );
  }
  const tolerance = data ? toleranceText(data.usageTolerancePct) : null;
  return (
    <>
      <h1>Producción del día</h1>
      <StepsHelp
        testId="production-steps"
        steps={[
          "Inicie la corrida: máquina, turno, producto y fecha.",
          "Al terminar el turno, registre el resumen: tandas, unidades buenas, merma y el consumo real de cada material.",
          "El Gerente de planta cierra el resumen del turno: se descuentan los materiales y el lote entra a curado.",
        ]}
      />
      <div>
        <PlantSelect plants={plants.data} value={plantId} onChange={setPlant} />
        <Field label="Día">
          <input type="date" value={businessDate} onChange={(e) => setBusinessDate(e.target.value)} />
        </Field>
      </div>
      <DraftSummaries plantId={plantId} />
      {can("production_run:manage") ? (
        starting ? (
          <>
            <StartRun
              plantId={plantId}
              businessDate={businessDate}
              onDateChange={setBusinessDate}
              onDone={(runId) => {
                setStarted(runId);
                setStarting(false);
                reload();
              }}
            />
            <button type="button" onClick={() => setStarting(false)}>
              Cerrar el formulario
            </button>
          </>
        ) : (
          <button type="button" className="primary" onClick={() => setStarting(true)}>
            Iniciar una corrida
          </button>
        )
      ) : null}
      {started ? (
        <p className="notice" data-testid="run-started">
          Corrida iniciada. <Link href={`/produccion/corrida/?id=${started}`}>Abrir la corrida</Link>
        </p>
      ) : null}
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : (
        <>
          <h2>Totales del día</h2>
          <div className="cards" data-testid="day-totals">
            <div className="stat">
              <span>Corridas</span>
              <span className="value" data-testid="day-runs">
                {data.runs.length}
              </span>
            </div>
            <div className="stat">
              <span>Unidades buenas</span>
              <span className="value" data-testid="day-good-units">
                {formatQuantity(data.goodUnits)}
              </span>
            </div>
            <div className="stat">
              <span>Merma de mezcla</span>
              <span className="value" data-testid="day-mix-scrap">
                {formatQuantity(data.mixScrapUnits)}
              </span>
            </div>
            <div className="stat">
              <span>Merma en fresco</span>
              <span className="value" data-testid="day-fresh-scrap">
                {formatQuantity(data.freshScrapUnits)}
              </span>
            </div>
            <div className="stat">
              <span>Merma total</span>
              <span className="value" data-testid="day-scrap">
                {formatQuantity(data.scrapUnits)}
              </span>
            </div>
          </div>
          <p className="muted">Unidades de producto; solo cuentan los resúmenes registrados.</p>
          <h2>Corridas</h2>
          {data.runs.length === 0 ? (
            <EmptyState title="No hay corridas ese día.">
              <p>{can("production_run:manage") ? "Pulse «Iniciar una corrida» para empezar, o elija otro día." : "Elija otro día o planta."}</p>
            </EmptyState>
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Corrida</th>
                    <th>Siguiente paso</th>
                    <th>Máquina</th>
                    <th>Turno</th>
                    <th>Producto</th>
                    <th>Estado</th>
                    <th>Resumen</th>
                    <th className="num">Unidades buenas</th>
                    <th className="num">Merma de mezcla</th>
                    <th className="num">Merma en fresco</th>
                    <th>Lote</th>
                  </tr>
                </thead>
                <tbody>
                  {data.runs.map((r) => {
                    const step = runNextStep(r, can);
                    return (
                      <tr key={r.runId}>
                        <td className="mono">
                          <Link href={`/produccion/corrida/?id=${r.runId}`}>{r.runNo}</Link>
                        </td>
                        <td className="ux4b-next">
                          {step ? (
                            <Link className={step.primary ? "button primary" : "button"} href={step.href} data-testid={`next-step-${r.runNo}`}>
                              {step.label}
                            </Link>
                          ) : null}
                        </td>
                        <td className="mono">{r.machineCode}</td>
                        <td className="mono">{r.shiftCode}</td>
                        <td className="wrap">{r.itemCode}</td>
                        <td>
                          <ProductionBadge kind="run" status={r.status} />
                        </td>
                        <td>{r.summaryStatus ? <ProductionBadge kind="summary" status={r.summaryStatus} /> : <span className="muted">Sin resumen</span>}</td>
                        <td className="num">{formatQuantity(r.goodUnits)}</td>
                        <td className="num">{formatQuantity(r.mixScrapUnits)}</td>
                        <td className="num">{formatQuantity(r.freshScrapUnits)}</td>
                        <td>
                          {r.lotCode ? (
                            <>
                              <Link className="mono" href={lotHref(r.lotCode)}>
                                {r.lotCode}
                              </Link>{" "}
                              {r.lotStatus ? <ProductionBadge kind="lot" status={r.lotStatus} /> : null}
                            </>
                          ) : (
                            <span className="muted">Se crea al cerrar el resumen</span>
                          )}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
          <h2>Materiales: real contra teórico</h2>
          <p className="muted" data-testid="day-tolerance">
            {tolerance
              ? `Tolerancia de uso de la política de producción: ${tolerance} del teórico. La diferencia es real − teórico.`
              : "Sin política de producción en vigor ese día: no se marca la tolerancia. La diferencia es real − teórico."}
          </p>
          {data.materials.length === 0 ? (
            <EmptyState title="Sin consumos registrados ese día.">
              <p>El consumo real se registra con el resumen del turno de cada corrida.</p>
            </EmptyState>
          ) : (
            <div className="table-wrap">
              <table data-testid="day-materials">
                <thead>
                  <tr>
                    <th>Material</th>
                    <th className="num">Real</th>
                    <th className="num">Teórico</th>
                    <th className="num">Diferencia</th>
                    <th className="num">%</th>
                    <th>Tolerancia</th>
                  </tr>
                </thead>
                <tbody>
                  {data.materials.map((m) => {
                    const mark = varianceMark(m.outOfTolerance);
                    return (
                      <tr key={m.materialItemId} data-testid={`day-material-${m.materialCode}`}>
                        <td>{m.materialCode}</td>
                        <td className="num">
                          <Quantity value={m.qty} uom={m.baseUom} />
                        </td>
                        <td className="num">
                          <Quantity value={m.theoreticalQty} uom={m.baseUom} />
                        </td>
                        <td className="num" data-testid="variance">
                          {signedQuantity(m.difference)} {uomLabel(m.baseUom)}
                        </td>
                        <td className="num" data-testid="variance-pct">
                          {signedPercent(m.differencePct)}
                        </td>
                        <td>{mark ? <span className={`badge tone-${mark.tone}`}>{mark.label}</span> : <span className="muted">—</span>}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
      {data !== null && data.runs.some((r) => r.lotStatus === "CURING") ? (
        <p>
          <StatusBadge status="CURING" /> Los lotes en curado se liberan en <Link href="/produccion/lotes/">Curado y liberación</Link>.
        </p>
      ) : null}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <ProductionDay />
    </Suspense>
  );
}
