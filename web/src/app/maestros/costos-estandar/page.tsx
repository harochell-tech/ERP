"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { codeAndName } from "@/lib/ux4b";
import { isDecimal, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-02-7): standard cost per finished good and valuation area — prepared by the Controller, approved by the Aprobador
// de políticas (step-up). The products come from master data, the areas from the plants.

function PrepareCost({ onDone }: { onDone: () => void }) {
  const { companyId, can, plantName } = useSession();
  const prepare = useCommand("prepare-standard-cost", "/api/v1/companies/{companyId}/sales/prepare-standard-cost");
  const [form, setForm] = useState({ itemId: "", valuationAreaId: "", unitCost: "" });
  const fe = useFieldErrors<"itemId" | "valuationAreaId" | "unitCost">();
  const { data, error } = useLoad(
    async () => {
      const [plants, items] = await Promise.all([
        query("/api/v1/companies/{companyId}/sales/plants", { path: { companyId } }),
        can("master_data:read") ? query("/api/v1/companies/{companyId}/master-data/items", { path: { companyId }, query: { limit: 200 } }) : Promise.resolve({ items: [] }),
      ]);
      return { plants: plants.items, goods: items.items.filter((i) => i.itemType === "FINISHED_GOOD") };
    },
    [companyId],
  );
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const unitCost = normalizeInput(form.unitCost);
        if (
          !fe.check({
            itemId: !form.itemId && "Elija el producto.",
            valuationAreaId: !form.valuationAreaId && "Elija la planta.",
            unitCost: !isPositiveDecimal(unitCost, 4) && "Indique un costo mayor que cero (hasta 4 decimales).",
          })
        ) {
          return;
        }
        const code = data.goods.find((g) => g.itemId === form.itemId)?.code ?? "";
        if (await prepare.run({ itemId: form.itemId, valuationAreaId: form.valuationAreaId, unitCost }, undefined, `Costo estándar de ${code} preparado; falta su aprobación.`)) {
          setForm({ ...form, unitCost: "" });
          onDone();
        }
      }}
    >
      <h2>Opción 1 · Escribir el costo unitario</h2>
      <p className="muted">Para un producto sin receta activa, o cuando el costo viene de otro cálculo: escriba cuánto cuesta una unidad.</p>
      <Field label="Producto" required error={fe.errors.itemId}>
        <select aria-label="Producto" value={form.itemId} onChange={(e) => setForm({ ...form, itemId: e.target.value })}>
          <option value="">Seleccione…</option>
          {data.goods.map((g) => (
            <option key={g.itemId} value={g.itemId}>
              {codeAndName(g.code, g.description)}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Planta" required error={fe.errors.valuationAreaId} hint="El costo vale para el inventario del producto en esa planta.">
        <select aria-label="Planta" value={form.valuationAreaId} onChange={(e) => setForm({ ...form, valuationAreaId: e.target.value })}>
          <option value="">Seleccione…</option>
          {data.plants.map((p) => (
            <option key={p.plantId} value={p.valuationAreaId}>
              {plantName(p.plantId, p.code)}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Costo unitario (RD$)" required error={fe.errors.unitCost}>
        <input inputMode="decimal" value={form.unitCost} onChange={(e) => setForm({ ...form, unitCost: e.target.value })} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={prepare.busy}>
          Preparar costo
        </button>
      </div>
      <ErrorBox error={prepare.error} />
    </form>
  );
}

interface FromRecipeResult {
  unitCost: string;
  materialCost: string;
  conversionCost: string;
}

/** MFG1-07 (E-MFG1-07-4): the standard of a product from its ACTIVE recipe — a standard price per material plus the conversion cost; the
 * API computes the unit cost (materials per unit × price + conversion). */
function PrepareFromRecipe({ onDone }: { onDone: () => void }) {
  const { companyId, plantFor } = useSession();
  const prepare = useCommand("prepare-standard-cost-from-recipe", "/api/v1/companies/{companyId}/sales/prepare-standard-cost-from-recipe");
  const recipes = useLoad(
    () => query("/api/v1/companies/{companyId}/manufacturing/recipes", { path: { companyId }, query: { plantId: plantFor("production:read"), status: "ACTIVE" } }),
    [companyId],
  );
  const [recipeVersionId, setRecipeVersionId] = useState("");
  const recipe = useLoad(
    recipeVersionId
      ? () => query("/api/v1/companies/{companyId}/manufacturing/recipes/{recipeVersionId}", { path: { companyId, recipeVersionId } })
      : null,
    [companyId, recipeVersionId],
  );
  const [prices, setPrices] = useState<Record<string, string>>({});
  const [conversionCost, setConversionCost] = useState("");
  const fe = useFieldErrors();
  const [result, setResult] = useState<FromRecipeResult | null>(null);
  if (recipes.data === null) {
    return <LoadingIndicator error={recipes.error} />;
  }
  const lines = recipe.data && recipe.data.recipe.recipeVersionId === recipeVersionId ? recipe.data.lines : [];
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const materialPrices = lines.map((l) => ({ materialItemId: l.materialItemId, stdPrice: normalizeInput(prices[l.materialItemId] ?? "") }));
        const conversion = normalizeInput(conversionCost);
        const found: Record<string, string | false> = {
          recipe: (!recipeVersionId || lines.length === 0) && "Elija una receta activa.",
          conversion: (!isDecimal(conversion, 4) || conversion.startsWith("-")) && "El costo de conversión es cero o más (hasta 4 decimales).",
        };
        for (const p of materialPrices) {
          found[`price-${p.materialItemId}`] = !isPositiveDecimal(p.stdPrice, 4) && "Indique un precio estándar mayor que cero (hasta 4 decimales).";
        }
        if (!fe.check(found)) {
          return;
        }
        const itemCode = recipes.data?.items.find((r) => r.recipeVersionId === recipeVersionId)?.itemCode ?? "";
        const response = await prepare.run({ recipeVersionId, materialPrices, conversionCost: conversion }, undefined, `Costo estándar de ${itemCode} preparado desde la receta; falta su aprobación.`);
        if (response) {
          setResult(response.result as FromRecipeResult);
          setPrices({});
          setConversionCost("");
          onDone();
        }
      }}
    >
      <h2>Opción 2 · Calcular desde la receta</h2>
      <p className="muted">
        Para un producto con receta activa: escriba el precio estándar de cada material y el costo de conversión (mano de obra, energía, equipos) por unidad;
        el sistema calcula el costo unitario con las cantidades de la receta.
      </p>
      <Field label="Receta activa" required error={fe.errors.recipe}>
        <select
          aria-label="Receta activa"
          value={recipeVersionId}
          onChange={(e) => {
            setRecipeVersionId(e.target.value);
            setPrices({});
            setResult(null);
          }}
        >
          <option value="">Seleccione…</option>
          {recipes.data.items.map((r) => (
            <option key={r.recipeVersionId} value={r.recipeVersionId}>
              {r.itemCode} en {r.machineCode} (v{r.version})
            </option>
          ))}
        </select>
      </Field>
      {recipeVersionId && recipe.data === null ? <LoadingIndicator error={recipe.error} /> : null}
      {lines.map((l) => (
        <Field key={l.materialItemId} label={`Precio estándar ${l.materialCode} (por ${l.baseUom})`} required error={fe.errors[`price-${l.materialItemId}`]}>
          <input inputMode="decimal" value={prices[l.materialItemId] ?? ""} onChange={(e) => setPrices({ ...prices, [l.materialItemId]: e.target.value })} />
        </Field>
      ))}
      <Field label="Costo de conversión por unidad" required error={fe.errors.conversion} hint="En RD$ por unidad producida; puede ser 0.">
        <input inputMode="decimal" value={conversionCost} onChange={(e) => setConversionCost(e.target.value)} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={prepare.busy}>
          Preparar desde receta
        </button>
      </div>
      <ErrorBox error={prepare.error} />
      {result ? (
        <p className="notice" data-testid="recipe-cost-result">
          Costo unitario <Money value={result.unitCost} testId="recipe-unit-cost" currency /> = materiales <Money value={result.materialCost} /> + conversión{" "}
          <Money value={result.conversionCost} />
        </p>
      ) : null}
    </form>
  );
}

function Approve({ costVersionId, label, onDone }: { costVersionId: string; label: string; onDone: () => void }) {
  const approve = useCommand(`approve-standard-cost:${costVersionId}`, "/api/v1/companies/{companyId}/sales/approve-standard-cost", `Costo estándar de ${label} aprobado.`);
  return (
    <>
      <ConfirmAction
        label="Aprobar"
        className="primary"
        stepUp
        busy={approve.busy}
        title={`¿Aprobar el costo estándar de ${label}?`}
        consequence="El costo pasa a regir hoy y reemplaza al vigente; el inventario del producto en esa área se revalúa al nuevo costo. No se puede deshacer."
        onConfirm={async () => (await approve.run({ costVersionId })) && onDone()}
      />
      <ErrorBox error={approve.error} />
    </>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const { data, error, reload } = useLoad(
    can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/standard-costs", { path: { companyId } }) : null,
    [companyId],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Costos estándar</h1>
      <p className="muted">
        El costo estándar es lo que vale una unidad de cada producto terminado en cada planta: con él se valora el inventario y el costo de lo vendido. Se
        prepara de una de las dos formas de abajo y queda en borrador hasta que el Aprobador de políticas lo apruebe.
      </p>
      {can("standard_cost:prepare") ? <PrepareCost onDone={reload} /> : null}
      {can("standard_cost:prepare") && can("production:read") ? <PrepareFromRecipe onDone={reload} /> : null}
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : data.items.length === 0 ? (
        <EmptyState title="Aún no hay costos estándar.">
          <p>{can("standard_cost:prepare") ? "Prepare el primero con una de las dos opciones de arriba." : "Los prepara el Controller y los aprueba el Aprobador de políticas."}</p>
        </EmptyState>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Producto</th>
              <th>Área de valuación</th>
              <th className="num">Versión</th>
              <th>Vigente desde</th>
              <th className="num">Costo unitario (RD$)</th>
              <th>Estado</th>
              <th>Preparó</th>
              <th>Aprobó</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.items.map((c) => (
              <tr key={`${c.costVersionId}:${c.status}`}>
                <td>{codeAndName(c.itemCode, c.itemDescription)}</td>
                <td>{c.valuationAreaCode}</td>
                <td className="num">{c.version}</td>
                <td>{formatDate(c.effectiveFrom)}</td>
                <td className="num">
                  <Money value={c.unitCost} />
                </td>
                <td>
                  <StatusBadge status={c.status} />
                </td>
                <td className="wrap">{c.preparedBy ?? "—"}</td>
                <td className="wrap">{c.approvedBy ?? "—"}</td>
                <td className="actions">{c.status === "DRAFT" && can("standard_cost:approve") ? <Approve costVersionId={c.costVersionId} label={c.itemCode} onDone={reload} /> : null}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
