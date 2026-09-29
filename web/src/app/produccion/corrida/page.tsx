"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { usePlants } from "@/components/Production";
import { ErrorBox, Field, Loading, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatQuantity, isDecimal, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { parseWholeNumber, stockLocations } from "@/lib/production";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Detail = Schemas["ProductionRunDetail"];
type RecipeLine = Schemas["RecipeLineView"];
type Location = Schemas["LocationView"];

// MFG1-07 (E-MFG1-07-2): a production run — the Supervisor de producción records (or replaces) the shift summary with the real
// consumption of each recipe material, the Gerente de planta posts it (P-08 / P-10: the lot goes into curing) or reverses it, and a
// run without a summary can be cancelled. Every quantity is a decimal string; the theory and differences come from the API.

interface ConsumptionDraft {
  locationId: string;
  quantity: string;
  uom: string;
}

function RecordSummary({ detail, lines, locations, onDone }: { detail: Detail; lines: RecipeLine[]; locations: Location[]; onDone: () => void }) {
  const run = detail.run;
  const record = useCommand(`record-shift-summary:${run.runId}`, "/api/v1/companies/{companyId}/manufacturing/record-shift-summary");
  const existing = detail.summary?.status === "DRAFT" ? detail.summary : null;
  const [form, setForm] = useState(() => ({
    batches: existing ? String(existing.batches) : "",
    goodUnits: existing?.goodUnits ?? "",
    mixScrapUnits: existing?.mixScrapUnits ?? "0",
    freshScrapUnits: existing?.freshScrapUnits ?? "0",
  }));
  const [consumption, setConsumption] = useState<Record<string, ConsumptionDraft>>(() =>
    Object.fromEntries(
      lines.map((l) => {
        const recorded = existing ? detail.consumption.find((c) => c.materialItemId === l.materialItemId) : undefined;
        return [
          l.materialItemId,
          {
            locationId: (recorded && locations.find((loc) => loc.code === recorded.locationCode)?.locationId) ?? locations[0]?.locationId ?? "",
            quantity: recorded?.enteredQty ?? "",
            uom: recorded?.enteredUom ?? l.baseUom,
          },
        ];
      }),
    ),
  );
  const [invalid, setInvalid] = useState<string | null>(null);
  const setLine = (materialItemId: string, patch: Partial<ConsumptionDraft>) =>
    setConsumption({ ...consumption, [materialItemId]: { ...(consumption[materialItemId] ?? { locationId: "", quantity: "", uom: "" }), ...patch } });
  return (
    <form
      className="card"
      onSubmit={async (e) => {
        e.preventDefault();
        const batches = parseWholeNumber(form.batches);
        const goodUnits = normalizeInput(form.goodUnits);
        const mixScrapUnits = normalizeInput(form.mixScrapUnits) || "0";
        const freshScrapUnits = normalizeInput(form.freshScrapUnits) || "0";
        if (batches === null || batches <= 0) {
          setInvalid("Indique las tandas (un número entero mayor que cero).");
          return;
        }
        if (!isPositiveDecimal(goodUnits, 6) || !isDecimal(mixScrapUnits, 6) || !isDecimal(freshScrapUnits, 6) || mixScrapUnits.startsWith("-") || freshScrapUnits.startsWith("-")) {
          setInvalid("Las unidades buenas son mayores que cero; el scrap es cero o más (hasta 6 decimales).");
          return;
        }
        const body = lines.map((l) => {
          const c = consumption[l.materialItemId] ?? { locationId: "", quantity: "", uom: l.baseUom };
          return { materialItemId: l.materialItemId, locationId: c.locationId, quantity: normalizeInput(c.quantity), uom: c.uom.trim() };
        });
        if (body.some((c) => !c.locationId || !isPositiveDecimal(c.quantity, 6) || c.uom === "")) {
          setInvalid("Indique la ubicación, la cantidad consumida (mayor que cero) y la unidad de cada material.");
          return;
        }
        setInvalid(null);
        if (await record.run({ plantId: run.plantId, runId: run.runId, batches, goodUnits, mixScrapUnits, freshScrapUnits, consumption: body })) {
          onDone();
        }
      }}
    >
      <h2>{existing ? "Reemplazar resumen del turno" : "Registrar resumen del turno"}</h2>
      <Field label="Tandas">
        <input inputMode="numeric" value={form.batches} onChange={(e) => setForm({ ...form, batches: e.target.value })} />
      </Field>
      <Field label="Unidades buenas">
        <input inputMode="decimal" value={form.goodUnits} onChange={(e) => setForm({ ...form, goodUnits: e.target.value })} />
      </Field>
      <Field label="Scrap de mezcla (unidades)">
        <input inputMode="decimal" value={form.mixScrapUnits} onChange={(e) => setForm({ ...form, mixScrapUnits: e.target.value })} />
      </Field>
      <Field label="Scrap fresco (unidades)">
        <input inputMode="decimal" value={form.freshScrapUnits} onChange={(e) => setForm({ ...form, freshScrapUnits: e.target.value })} />
      </Field>
      <h3>Consumo real</h3>
      {locations.length === 0 ? <p className="warning">No hay ubicaciones de existencias disponibles para la planta.</p> : null}
      <table>
        <thead>
          <tr>
            <th>Material</th>
            <th className="num">Por tanda</th>
            <th>Ubicación</th>
            <th>Cantidad</th>
            <th>Unidad</th>
          </tr>
        </thead>
        <tbody>
          {lines.map((l) => {
            const c = consumption[l.materialItemId] ?? { locationId: "", quantity: "", uom: l.baseUom };
            return (
              <tr key={l.materialItemId}>
                <td>
                  {l.materialCode} — {l.materialName}
                </td>
                <td className="num">
                  {formatQuantity(l.qtyPerBatch)} {l.baseUom}
                </td>
                <td>
                  <select aria-label={`Ubicación ${l.materialCode}`} value={c.locationId} onChange={(e) => setLine(l.materialItemId, { locationId: e.target.value })}>
                    <option value="">—</option>
                    {locations.map((loc) => (
                      <option key={loc.locationId} value={loc.locationId}>
                        {loc.code}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <input aria-label={`Consumo ${l.materialCode}`} inputMode="decimal" value={c.quantity} onChange={(e) => setLine(l.materialItemId, { quantity: e.target.value })} />
                </td>
                <td>
                  <input aria-label={`Unidad ${l.materialCode}`} value={c.uom} onChange={(e) => setLine(l.materialItemId, { uom: e.target.value })} size={6} />
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <div className="actions">
        <button type="submit" disabled={record.busy}>
          {existing ? "Reemplazar resumen" : "Registrar resumen"}
        </button>
      </div>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={record.error} />
    </form>
  );
}

function Actions({ detail, onDone }: { detail: Detail; onDone: () => void }) {
  const { can } = useSession();
  const run = detail.run;
  const summary = detail.summary;
  const post = useCommand(`post-shift-summary:${run.runId}`, "/api/v1/companies/{companyId}/manufacturing/post-shift-summary");
  const reverse = useCommand(`reverse-shift-summary:${run.runId}`, "/api/v1/companies/{companyId}/manufacturing/reverse-shift-summary");
  const cancel = useCommand(`cancel-production-run:${run.runId}`, "/api/v1/companies/{companyId}/manufacturing/cancel-production-run");
  const busy = post.busy || reverse.busy || cancel.busy;
  const after = (response: unknown) => response && onDone();
  return (
    <div className="actions">
      {summary?.status === "DRAFT" && can("shift_summary:post") ? (
        <button type="button" disabled={busy} onClick={async () => after(await post.run({ plantId: run.plantId, runId: run.runId, expectedVersion: summary.version }))}>
          Contabilizar resumen
        </button>
      ) : null}
      {summary?.status === "POSTED" && can("shift_summary:post") ? (
        <ReasonAction
          label="Revertir resumen"
          busy={busy}
          onConfirm={async (reason) => after(await reverse.run({ plantId: run.plantId, runId: run.runId, expectedVersion: summary.version, reason }))}
        />
      ) : null}
      {run.status === "IN_PROGRESS" && summary === null && can("production_run:manage") ? (
        <ReasonAction
          label="Cancelar corrida"
          busy={busy}
          onConfirm={async (reason) => after(await cancel.run({ plantId: run.plantId, runId: run.runId, expectedVersion: run.version, reason }))}
        />
      ) : null}
      <ErrorBox error={post.error ?? reverse.error ?? cancel.error} />
    </div>
  );
}

function RunDetail() {
  const id = useSearchParams().get("id") ?? "";
  const { companyId, can } = useSession();
  const plants = usePlants();
  const { data, error, reload } = useLoad(
    can("production:read") && id
      ? async () => {
          const detail = await query("/api/v1/companies/{companyId}/manufacturing/runs/{runId}", { path: { companyId, runId: id } });
          // The run's recipe: the active one of its product on its machine (the run was started from it).
          const recipes = await query("/api/v1/companies/{companyId}/manufacturing/recipes", {
            path: { companyId },
            query: { plantId: detail.run.plantId, itemId: detail.run.itemId, status: "ACTIVE" },
          });
          const recipe = recipes.items.find((r) => r.machineCode === detail.run.machineCode);
          const lines = recipe
            ? (await query("/api/v1/companies/{companyId}/manufacturing/recipes/{recipeVersionId}", { path: { companyId, recipeVersionId: recipe.recipeVersionId } })).lines
            : [];
          return { detail, lines };
        }
      : null,
    [companyId, id],
  );
  if (!can("production:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const { detail, lines } = data;
  const run = detail.run;
  const summary = detail.summary;
  const lot = detail.lot;
  const locations = stockLocations(plants.data?.find((p) => p.plantId === run.plantId)?.locations ?? []);
  const canRecord = run.status === "IN_PROGRESS" && summary?.status !== "POSTED" && can("shift_summary:record");
  return (
    <>
      <p>
        <Link href="/produccion/dia/">← Producción del día</Link>
      </p>
      <h1>Corrida {run.runNo}</h1>
      <p>
        <StatusBadge status={run.status} testId="run-status" /> · planta {run.plantCode} · máquina {run.machineCode} · turno {run.shiftCode} · {formatDate(run.businessDate)} ·{" "}
        {run.itemCode}
      </p>

      <h2>Resumen del turno</h2>
      {summary === null ? (
        <p className="muted" data-testid="summary-status">
          Sin resumen
        </p>
      ) : (
        <>
          <p>
            <StatusBadge status={summary.status} testId="summary-status" /> · {summary.batches} tandas · unidades buenas{" "}
            <span className="mono" data-testid="summary-good-units">
              {formatQuantity(summary.goodUnits)}
            </span>{" "}
            · scrap de mezcla {formatQuantity(summary.mixScrapUnits)} · scrap fresco {formatQuantity(summary.freshScrapUnits)}
          </p>
          {detail.consumption.length > 0 ? (
            <table>
              <thead>
                <tr>
                  <th>Material</th>
                  <th>Ubicación</th>
                  <th className="num">Registrado</th>
                  <th className="num">Real (unidad base)</th>
                  <th className="num">Teórico</th>
                  <th className="num">Diferencia</th>
                </tr>
              </thead>
              <tbody>
                {detail.consumption.map((c) => (
                  <tr key={c.materialItemId}>
                    <td>{c.materialCode}</td>
                    <td>{c.locationCode}</td>
                    <td className="num">
                      {formatQuantity(c.enteredQty)} {c.enteredUom}
                    </td>
                    <td className="num">
                      {formatQuantity(c.qty)} {c.baseUom}
                    </td>
                    <td className="num">{formatQuantity(c.theoreticalQty)}</td>
                    <td className="num">{formatQuantity(c.difference)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : null}
        </>
      )}
      <Actions detail={detail} onDone={reload} />
      {canRecord ? (
        lines.length === 0 ? (
          <p className="warning">No se encontró la receta activa del producto en esta máquina.</p>
        ) : (
          <RecordSummary key={`${summary?.summaryId ?? "new"}:${summary?.version ?? 0}:${plants.data ? "p" : "-"}`} detail={detail} lines={lines} locations={locations} onDone={reload} />
        )
      ) : null}

      <h2>Lote</h2>
      {lot === null ? (
        <p className="muted">El lote se crea al contabilizar el resumen.</p>
      ) : (
        <p>
          <span className="mono" data-testid="lot-code">
            {lot.lotCode}
          </span>{" "}
          <StatusBadge status={lot.status} testId="lot-status" /> · <span data-testid="lot-racks">{lot.racks}</span> racks · en curado desde {formatDateTime(lot.curingFrom)} ·
          liberable desde {formatDateTime(lot.releasableAt)} · <Link href="/produccion/lotes/">Curado y liberación</Link>
        </p>
      )}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <RunDetail />
    </Suspense>
  );
}
