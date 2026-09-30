"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { usePlants } from "@/components/Production";
import { ConfirmAction, ErrorBox, Field, FieldMessage, fieldAria, LineTable, Loading, NoPermission, ReasonAction, StatusBadge, useFieldErrors } from "@/components/ui";
import { formatQuantity, isDecimal, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { parseWholeNumber, stockLocations } from "@/lib/production";
import { uomOptions } from "@/lib/units";
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
  const existing = detail.summary?.status === "DRAFT" ? detail.summary : null;
  const record = useCommand(
    `record-shift-summary:${run.runId}`,
    "/api/v1/companies/{companyId}/manufacturing/record-shift-summary",
    `Resumen del turno de la corrida ${run.runNo} ${existing ? "reemplazado" : "registrado"}; falta contabilizarlo.`,
  );
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
  const fe = useFieldErrors();
  // UX3-02 (E-UX3-11): each material's unit is its base unit or one with a conversion into it (as the purchase order form offers).
  const { companyId, plantFor } = useSession();
  const items = useLoad(
    () => query("/api/v1/companies/{companyId}/master-data/items", { path: { companyId }, query: { plantId: plantFor("master_data:read"), status: "ACTIVE", limit: 200 } }),
    [companyId],
  );
  const conversionsOf = (materialItemId: string) => items.data?.items.find((i) => i.itemId === materialItemId)?.conversions ?? [];
  const setLine = (materialItemId: string, patch: Partial<ConsumptionDraft>) =>
    setConsumption({ ...consumption, [materialItemId]: { ...(consumption[materialItemId] ?? { locationId: "", quantity: "", uom: "" }), ...patch } });
  const scrapInvalid = (value: string) => value !== "" && (!isDecimal(value, 6) || value.startsWith("-"));
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const batches = parseWholeNumber(form.batches);
        const goodUnits = normalizeInput(form.goodUnits);
        const mixScrapUnits = normalizeInput(form.mixScrapUnits) || "0";
        const freshScrapUnits = normalizeInput(form.freshScrapUnits) || "0";
        const body = lines.map((l) => {
          const c = consumption[l.materialItemId] ?? { locationId: "", quantity: "", uom: l.baseUom };
          return { materialItemId: l.materialItemId, locationId: c.locationId, quantity: normalizeInput(c.quantity), uom: c.uom.trim() };
        });
        const found: Record<string, string | false> = {
          batches: (batches === null || batches <= 0) && "Indique las tandas (un número entero mayor que cero).",
          goodUnits: !isPositiveDecimal(goodUnits, 6) && "Unidades buenas mayores que cero (hasta 6 decimales).",
          mixScrapUnits: scrapInvalid(mixScrapUnits) && "Cero o más (hasta 6 decimales).",
          freshScrapUnits: scrapInvalid(freshScrapUnits) && "Cero o más (hasta 6 decimales).",
        };
        for (const c of body) {
          found[`location-${c.materialItemId}`] = !c.locationId && "Elija la ubicación.";
          found[`quantity-${c.materialItemId}`] = !isPositiveDecimal(c.quantity, 6) && "Cantidad consumida mayor que cero.";
          found[`uom-${c.materialItemId}`] = c.uom === "" && "Indique la unidad.";
        }
        if (!fe.check(found) || batches === null) {
          return;
        }
        if (await record.run({ plantId: run.plantId, runId: run.runId, batches, goodUnits, mixScrapUnits, freshScrapUnits, consumption: body })) {
          onDone();
        }
      }}
    >
      <h2>{existing ? "Reemplazar resumen del turno" : "Registrar resumen del turno"}</h2>
      <Field label="Tandas" required error={fe.errors.batches}>
        <input inputMode="numeric" value={form.batches} onChange={(e) => setForm({ ...form, batches: e.target.value })} />
      </Field>
      <Field label="Unidades buenas" required error={fe.errors.goodUnits}>
        <input inputMode="decimal" value={form.goodUnits} onChange={(e) => setForm({ ...form, goodUnits: e.target.value })} />
      </Field>
      <Field label="Scrap de mezcla (unidades)" error={fe.errors.mixScrapUnits}>
        <input inputMode="decimal" value={form.mixScrapUnits} onChange={(e) => setForm({ ...form, mixScrapUnits: e.target.value })} />
      </Field>
      <Field label="Scrap fresco (unidades)" error={fe.errors.freshScrapUnits}>
        <input inputMode="decimal" value={form.freshScrapUnits} onChange={(e) => setForm({ ...form, freshScrapUnits: e.target.value })} />
      </Field>
      <h3>Consumo real</h3>
      {locations.length === 0 ? <p className="warning">No hay ubicaciones de existencias disponibles para la planta.</p> : null}
      <LineTable>
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
            const locationError = fe.errors[`location-${l.materialItemId}`];
            const quantityError = fe.errors[`quantity-${l.materialItemId}`];
            const uomError = fe.errors[`uom-${l.materialItemId}`];
            return (
              <tr key={l.materialItemId}>
                <td>
                  {l.materialCode} — {l.materialName}
                </td>
                <td className="num">
                  {formatQuantity(l.qtyPerBatch)} {l.baseUom}
                </td>
                <td>
                  <select
                    aria-label={`Ubicación ${l.materialCode}`}
                    value={c.locationId}
                    onChange={(e) => setLine(l.materialItemId, { locationId: e.target.value })}
                    {...fieldAria(locationError, `location-${l.materialItemId}-message`, true)}
                  >
                    <option value="">—</option>
                    {locations.map((loc) => (
                      <option key={loc.locationId} value={loc.locationId}>
                        {loc.code}
                      </option>
                    ))}
                  </select>
                  <FieldMessage id={`location-${l.materialItemId}-message`} error={locationError} />
                </td>
                <td>
                  <input
                    aria-label={`Consumo ${l.materialCode}`}
                    inputMode="decimal"
                    value={c.quantity}
                    onChange={(e) => setLine(l.materialItemId, { quantity: e.target.value })}
                    {...fieldAria(quantityError, `quantity-${l.materialItemId}-message`, true)}
                  />
                  <FieldMessage id={`quantity-${l.materialItemId}-message`} error={quantityError} />
                </td>
                <td>
                  <select
                    aria-label={`Unidad ${l.materialCode}`}
                    value={c.uom}
                    onChange={(e) => setLine(l.materialItemId, { uom: e.target.value })}
                    {...fieldAria(uomError, `uom-${l.materialItemId}-message`, true)}
                  >
                    {uomOptions(l.baseUom, conversionsOf(l.materialItemId), c.uom).map((u) => (
                      <option key={u} value={u}>
                        {u}
                      </option>
                    ))}
                  </select>
                  <FieldMessage id={`uom-${l.materialItemId}-message`} error={uomError} />
                </td>
              </tr>
            );
          })}
        </tbody>
      </LineTable>
      <ErrorBox error={record.error} />
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={record.busy}>
          {existing ? "Reemplazar resumen" : "Registrar resumen"}
        </button>
      </div>
    </form>
  );
}

function Actions({ detail, onDone }: { detail: Detail; onDone: () => void }) {
  const { can } = useSession();
  const run = detail.run;
  const summary = detail.summary;
  const post = useCommand(`post-shift-summary:${run.runId}`, "/api/v1/companies/{companyId}/manufacturing/post-shift-summary", (response) => {
    const lotCode = (response.result as { lotCode?: string } | null)?.lotCode;
    return `Resumen de la corrida ${run.runNo} contabilizado${lotCode ? `; lote ${lotCode} en curado` : ""}.`;
  });
  const reverse = useCommand(
    `reverse-shift-summary:${run.runId}`,
    "/api/v1/companies/{companyId}/manufacturing/reverse-shift-summary",
    `Resumen de la corrida ${run.runNo} revertido; la corrida vuelve a estar en proceso.`,
  );
  const cancel = useCommand(`cancel-production-run:${run.runId}`, "/api/v1/companies/{companyId}/manufacturing/cancel-production-run", `Corrida ${run.runNo} cancelada.`);
  const busy = post.busy || reverse.busy || cancel.busy;
  const after = (response: unknown) => response && onDone();
  return (
    <div className="actions">
      {summary?.status === "DRAFT" && can("shift_summary:post") ? (
        <ConfirmAction
          label="Contabilizar resumen"
          title={`¿Contabilizar el resumen de la corrida ${run.runNo}?`}
          consequence="Se consumen los materiales del inventario, se contabilizan los asientos y el lote entra a curado; la corrida queda completada. Solo se deshace revirtiendo el resumen."
          className="primary"
          busy={busy}
          onConfirm={async () => after(await post.run({ plantId: run.plantId, runId: run.runId, expectedVersion: summary.version }))}
        />
      ) : null}
      {summary?.status === "POSTED" && can("shift_summary:post") ? (
        <ReasonAction
          label="Revertir resumen"
          consequence="Se reversan los asientos y los consumos, el lote se anula y la corrida vuelve a estar en proceso. Solo es posible si el lote no se ha movido."
          stepUp
          busy={busy}
          onConfirm={async (reason) => after(await reverse.run({ plantId: run.plantId, runId: run.runId, expectedVersion: summary.version, reason }))}
        />
      ) : null}
      {run.status === "IN_PROGRESS" && summary === null && can("production_run:manage") ? (
        <ReasonAction
          label="Cancelar corrida"
          consequence="La corrida queda cancelada y no se podrá registrar producción en ella."
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
  const { companyId, can, plantName } = useSession();
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
        <StatusBadge status={run.status} testId="run-status" /> · planta {plantName(run.plantId, run.plantCode)} · máquina {run.machineCode} · turno {run.shiftCode} · {formatDate(run.businessDate)} ·{" "}
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
            <div className="table-wrap"><table>
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
            </table></div>
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
