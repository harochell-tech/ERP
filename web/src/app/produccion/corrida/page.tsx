"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ProductionBadge, Quantity, usePlants } from "@/components/Production";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, FieldMessage, fieldAria, LineTable, NoPermission, ReasonAction, useFieldErrors } from "@/components/ui";
import { formatQuantity, isDecimal, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import {
  curingRemainingText,
  dayHref,
  lotHref,
  quantityWithUnit,
  signedPercent,
  signedQuantity,
  toleranceText,
  uomLabel,
  varianceMark,
} from "@/lib/ux4bProduction";
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
// UX4-03: the run's own recipe version (E-UX4-9), a header of facts (P-25), merma and "Cerrar resumen del turno" (E-UX4-14), the
// consumption location chosen explicitly (P-23), per-batch and theoretical figures with units (P-22/P-05), the variance with sign,
// % and tolerance mark (P-16), the lot's remaining curing hours and link (P-26/P-17).

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
    `Resumen del turno de la corrida ${run.runNo} ${existing ? "reemplazado" : "registrado"}; falta que el Gerente de planta lo cierre.`,
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
            // P-23: the location is chosen explicitly (the recorded one when replacing a draft), never the first silently.
            locationId: (recorded && locations.find((loc) => loc.code === recorded.locationCode)?.locationId) ?? "",
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
      <Field label="Tandas" required error={fe.errors.batches} hint="Mezclas completas de la receta en el turno.">
        <input inputMode="numeric" value={form.batches} onChange={(e) => setForm({ ...form, batches: e.target.value })} />
      </Field>
      <Field label="Unidades buenas" required error={fe.errors.goodUnits} hint="Unidades que pasan a curado.">
        <input inputMode="decimal" value={form.goodUnits} onChange={(e) => setForm({ ...form, goodUnits: e.target.value })} />
      </Field>
      <Field label="Merma de mezcla (unidades)" error={fe.errors.mixScrapUnits} hint="Unidades perdidas por mezcla defectuosa.">
        <input inputMode="decimal" value={form.mixScrapUnits} onChange={(e) => setForm({ ...form, mixScrapUnits: e.target.value })} />
      </Field>
      <Field label="Merma en fresco (unidades)" error={fe.errors.freshScrapUnits} hint="Unidades rotas o defectuosas al salir de la máquina.">
        <input inputMode="decimal" value={form.freshScrapUnits} onChange={(e) => setForm({ ...form, freshScrapUnits: e.target.value })} />
      </Field>
      <h3>Consumo real</h3>
      <p className="muted">
        Indique de qué ubicación salió cada material y cuánto se usó. El teórico (por tanda × tandas) lo calcula el sistema al registrar el
        resumen.
      </p>
      {locations.length === 0 ? <p className="warning">No hay ubicaciones de existencias disponibles para la planta.</p> : null}
      <LineTable>
        <thead>
          <tr>
            <th>Material</th>
            <th className="num">Receta por tanda</th>
            <th>Ubicación</th>
            <th>Cantidad consumida</th>
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
                <td>{l.materialCode === l.materialName ? l.materialCode : `${l.materialCode} — ${l.materialName}`}</td>
                <td className="num">{quantityWithUnit(l.qtyPerBatch, l.baseUom)}</td>
                <td>
                  <select
                    aria-label={`Ubicación ${l.materialCode}`}
                    value={c.locationId}
                    onChange={(e) => setLine(l.materialItemId, { locationId: e.target.value })}
                    {...fieldAria(locationError, `location-${l.materialItemId}-message`, true)}
                  >
                    <option value="">Seleccione…</option>
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
                        {uomLabel(u)}
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
    return `Resumen del turno de la corrida ${run.runNo} cerrado${lotCode ? `; lote ${lotCode} en curado` : ""}.`;
  });
  const reverse = useCommand(
    `reverse-shift-summary:${run.runId}`,
    "/api/v1/companies/{companyId}/manufacturing/reverse-shift-summary",
    `Resumen del turno de la corrida ${run.runNo} revertido; la corrida vuelve a estar en proceso.`,
  );
  const cancel = useCommand(`cancel-production-run:${run.runId}`, "/api/v1/companies/{companyId}/manufacturing/cancel-production-run", `Corrida ${run.runNo} cancelada.`);
  const busy = post.busy || reverse.busy || cancel.busy;
  const after = (response: unknown) => response && onDone();
  return (
    <div className="actions">
      {summary?.status === "DRAFT" && can("shift_summary:post") ? (
        <ConfirmAction
          label="Cerrar resumen del turno"
          title={`¿Cerrar el resumen del turno de la corrida ${run.runNo}?`}
          consequence="Se descuentan los materiales del inventario, se registra el costo en contabilidad y el lote entra a curado; la corrida queda completada. Solo se deshace revirtiendo el resumen."
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
          // E-UX4-9: the run keeps the recipe version it started with, even after a newer one is approved.
          const recipe = await query("/api/v1/companies/{companyId}/manufacturing/recipes/{recipeVersionId}", {
            path: { companyId, recipeVersionId: detail.recipeVersionId },
          });
          return { detail, lines: recipe.lines, recipe: recipe.recipe };
        }
      : null,
    [companyId, id],
  );
  if (!can("production:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const { detail, lines, recipe } = data;
  const run = detail.run;
  const summary = detail.summary;
  const lot = detail.lot;
  const locations = stockLocations(plants.data?.find((p) => p.plantId === run.plantId)?.locations ?? []);
  const canRecord = run.status === "IN_PROGRESS" && summary?.status !== "POSTED" && can("shift_summary:record");
  const tolerance = toleranceText(detail.usageTolerancePct);
  const remaining = lot ? curingRemainingText(lot.status, lot.curingHoursRemaining) : null;
  const batchesText = summary ? `${summary.batches} ${summary.batches === 1 ? "tanda" : "tandas"}` : "";
  return (
    <>
      <p>
        <Link href={dayHref(run.businessDate)}>← Producción del día</Link>
      </p>
      <h1>Corrida {run.runNo}</h1>
      <p>
        <ProductionBadge kind="run" status={run.status} testId="run-status" />
      </p>
      <dl className="ux4b-facts card" data-testid="run-header">
        <div>
          <dt>Producto</dt>
          <dd>{recipe.itemCode === recipe.itemName ? recipe.itemCode : `${recipe.itemCode} — ${recipe.itemName}`}</dd>
        </div>
        <div>
          <dt>Fecha de producción</dt>
          <dd>
            <Link href={dayHref(run.businessDate)}>{formatDate(run.businessDate)}</Link>
          </dd>
        </div>
        <div>
          <dt>Planta</dt>
          <dd>{plantName(run.plantId, run.plantCode)}</dd>
        </div>
        <div>
          <dt>Máquina</dt>
          <dd className="mono">{run.machineCode}</dd>
        </div>
        <div>
          <dt>Turno</dt>
          <dd className="mono">{run.shiftCode}</dd>
        </div>
        <div>
          <dt>Receta</dt>
          <dd>
            <Link href={`/produccion/receta/?id=${detail.recipeVersionId}`} data-testid="run-recipe">
              Versión {detail.recipeVersion}
            </Link>{" "}
            · {formatQuantity(recipe.unitsPerBatch)} unidades por tanda
          </dd>
        </div>
      </dl>

      <h2>Resumen del turno</h2>
      {summary === null ? (
        <p className="muted" data-testid="summary-status">
          Sin resumen
        </p>
      ) : (
        <>
          <p>
            <ProductionBadge kind="summary" status={summary.status} testId="summary-status" /> · {batchesText} · unidades buenas{" "}
            <span className="mono" data-testid="summary-good-units">
              {formatQuantity(summary.goodUnits)}
            </span>{" "}
            · merma de mezcla <span className="mono">{formatQuantity(summary.mixScrapUnits)}</span> · merma en fresco{" "}
            <span className="mono">{formatQuantity(summary.freshScrapUnits)}</span>
          </p>
          {detail.consumption.length > 0 ? (
            <>
              <p className="muted" data-testid="run-tolerance">
                {tolerance
                  ? `Diferencia = real − teórico; tolerancia de uso ${tolerance} del teórico.`
                  : "Diferencia = real − teórico; sin política de producción en vigor para esta fecha, no se marca la tolerancia."}
              </p>
              <div className="table-wrap">
                <table data-testid="run-consumption">
                  <thead>
                    <tr>
                      <th>Material</th>
                      <th>Ubicación</th>
                      <th className="num">Receta por tanda</th>
                      <th className="num">Teórico ({batchesText})</th>
                      <th className="num">Real</th>
                      <th className="num">Diferencia</th>
                      <th className="num">%</th>
                      <th>Tolerancia</th>
                    </tr>
                  </thead>
                  <tbody>
                    {detail.consumption.map((c) => {
                      const mark = varianceMark(c.outOfTolerance);
                      return (
                        <tr key={c.materialItemId} data-testid={`consumption-${c.materialCode}`}>
                          <td>{c.materialCode}</td>
                          <td>{c.locationCode}</td>
                          <td className="num">
                            <Quantity value={c.qtyPerBatch} uom={c.baseUom} />
                          </td>
                          <td className="num">
                            <Quantity value={c.theoreticalQty} uom={c.baseUom} />
                          </td>
                          <td className="num">
                            <Quantity value={c.qty} uom={c.baseUom} />
                            {c.enteredUom !== c.baseUom ? <div className="muted">anotado: {quantityWithUnit(c.enteredQty, c.enteredUom)}</div> : null}
                          </td>
                          <td className="num" data-testid="variance">
                            {signedQuantity(c.difference)} {uomLabel(c.baseUom)}
                          </td>
                          <td className="num" data-testid="variance-pct">
                            {signedPercent(c.differencePct)}
                          </td>
                          <td>{mark ? <span className={`badge tone-${mark.tone}`}>{mark.label}</span> : <span className="muted">—</span>}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            </>
          ) : null}
        </>
      )}
      <Actions detail={detail} onDone={reload} />
      {canRecord ? (
        lines.length === 0 ? (
          <p className="warning">La receta de la corrida no tiene materiales.</p>
        ) : (
          <RecordSummary key={`${summary?.summaryId ?? "new"}:${summary?.version ?? 0}:${plants.data ? "p" : "-"}`} detail={detail} lines={lines} locations={locations} onDone={reload} />
        )
      ) : null}

      <h2>Lote</h2>
      {lot === null ? (
        <p className="muted">El lote se crea al cerrar el resumen del turno.</p>
      ) : (
        <>
          <p>
            <Link className="mono" href={lotHref(lot.lotCode)} data-testid="lot-code">
              {lot.lotCode}
            </Link>{" "}
            <ProductionBadge kind="lot" status={lot.status} testId="lot-status" /> · <span data-testid="lot-racks">{lot.racks}</span> racks · en curado desde{" "}
            {formatDateTime(lot.curingFrom)} · se puede liberar desde {formatDateTime(lot.releasableAt)}
          </p>
          {remaining ? <p data-testid="lot-curing-remaining">{remaining}</p> : null}
        </>
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
