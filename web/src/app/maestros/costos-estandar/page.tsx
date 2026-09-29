"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { isDecimal, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-02-7): standard cost per finished good and valuation area — prepared by the Controller, approved by the Aprobador
// de políticas (step-up). The products come from master data, the areas from the plants.

function PrepareCost({ onDone }: { onDone: () => void }) {
  const { companyId, can } = useSession();
  const prepare = useCommand("prepare-standard-cost", "/api/v1/companies/{companyId}/sales/prepare-standard-cost");
  const [form, setForm] = useState({ itemId: "", valuationAreaId: "", unitCost: "" });
  const [invalid, setInvalid] = useState<string | null>(null);
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
    return <Loading error={error} />;
  }
  return (
    <form
      className="inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const unitCost = normalizeInput(form.unitCost);
        if (!form.itemId || !form.valuationAreaId || !isPositiveDecimal(unitCost, 4)) {
          setInvalid("Elija producto y área, y un costo mayor que cero (hasta 4 decimales).");
          return;
        }
        setInvalid(null);
        if (await prepare.run({ itemId: form.itemId, valuationAreaId: form.valuationAreaId, unitCost })) {
          setForm({ ...form, unitCost: "" });
          onDone();
        }
      }}
    >
      <Field label="Producto">
        <select aria-label="Producto" value={form.itemId} onChange={(e) => setForm({ ...form, itemId: e.target.value })}>
          <option value="">—</option>
          {data.goods.map((g) => (
            <option key={g.itemId} value={g.itemId}>
              {g.code} — {g.description}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Área (planta)">
        <select aria-label="Área (planta)" value={form.valuationAreaId} onChange={(e) => setForm({ ...form, valuationAreaId: e.target.value })}>
          <option value="">—</option>
          {data.plants.map((p) => (
            <option key={p.plantId} value={p.valuationAreaId}>
              {p.code}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Costo unitario">
        <input inputMode="decimal" value={form.unitCost} onChange={(e) => setForm({ ...form, unitCost: e.target.value })} />
      </Field>
      <button type="submit" disabled={prepare.busy}>
        Preparar costo
      </button>
      {invalid ? <div className="error">{invalid}</div> : null}
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
  const [invalid, setInvalid] = useState<string | null>(null);
  const [result, setResult] = useState<FromRecipeResult | null>(null);
  if (recipes.data === null) {
    return <Loading error={recipes.error} />;
  }
  const lines = recipe.data && recipe.data.recipe.recipeVersionId === recipeVersionId ? recipe.data.lines : [];
  return (
    <form
      className="card"
      onSubmit={async (e) => {
        e.preventDefault();
        const materialPrices = lines.map((l) => ({ materialItemId: l.materialItemId, stdPrice: normalizeInput(prices[l.materialItemId] ?? "") }));
        const conversion = normalizeInput(conversionCost);
        if (!recipeVersionId || lines.length === 0) {
          setInvalid("Elija una receta activa.");
          return;
        }
        if (materialPrices.some((p) => !isPositiveDecimal(p.stdPrice, 4)) || !isDecimal(conversion, 4) || conversion.startsWith("-")) {
          setInvalid("Cada material necesita un precio estándar mayor que cero y el costo de conversión es cero o más (hasta 4 decimales).");
          return;
        }
        setInvalid(null);
        const response = await prepare.run({ recipeVersionId, materialPrices, conversionCost: conversion });
        if (response) {
          setResult(response.result as FromRecipeResult);
          setPrices({});
          setConversionCost("");
          onDone();
        }
      }}
    >
      <h2>Preparar desde receta</h2>
      <Field label="Receta activa">
        <select
          aria-label="Receta activa"
          value={recipeVersionId}
          onChange={(e) => {
            setRecipeVersionId(e.target.value);
            setPrices({});
            setResult(null);
          }}
        >
          <option value="">—</option>
          {recipes.data.items.map((r) => (
            <option key={r.recipeVersionId} value={r.recipeVersionId}>
              {r.itemCode} en {r.machineCode} (v{r.version})
            </option>
          ))}
        </select>
      </Field>
      {recipeVersionId && recipe.data === null ? <Loading error={recipe.error} /> : null}
      {lines.map((l) => (
        <Field key={l.materialItemId} label={`Precio estándar ${l.materialCode} (por ${l.baseUom})`}>
          <input inputMode="decimal" value={prices[l.materialItemId] ?? ""} onChange={(e) => setPrices({ ...prices, [l.materialItemId]: e.target.value })} />
        </Field>
      ))}
      <Field label="Costo de conversión por unidad">
        <input inputMode="decimal" value={conversionCost} onChange={(e) => setConversionCost(e.target.value)} />
      </Field>
      <button type="submit" disabled={prepare.busy}>
        Preparar desde receta
      </button>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={prepare.error} />
      {result ? (
        <p className="notice" data-testid="recipe-cost-result">
          Costo unitario <Money value={result.unitCost} testId="recipe-unit-cost" /> = materiales <Money value={result.materialCost} /> + conversión{" "}
          <Money value={result.conversionCost} />
        </p>
      ) : null}
    </form>
  );
}

function Approve({ costVersionId, onDone }: { costVersionId: string; onDone: () => void }) {
  const approve = useCommand(`approve-standard-cost:${costVersionId}`, "/api/v1/companies/{companyId}/sales/approve-standard-cost");
  return (
    <>
      <button type="button" disabled={approve.busy} onClick={async () => (await approve.run({ costVersionId })) && onDone()}>
        Aprobar
      </button>
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
      {can("standard_cost:prepare") ? <PrepareCost onDone={reload} /> : null}
      {can("standard_cost:prepare") && can("production:read") ? <PrepareFromRecipe onDone={reload} /> : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay costos estándar.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Producto</th>
              <th>Área</th>
              <th className="num">Versión</th>
              <th>Vigente desde</th>
              <th className="num">Costo unitario</th>
              <th>Estado</th>
              <th>Preparó</th>
              <th>Aprobó</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.items.map((c) => (
              <tr key={`${c.costVersionId}:${c.status}`}>
                <td>
                  {c.itemCode} — {c.itemDescription}
                </td>
                <td>{c.valuationAreaCode}</td>
                <td className="num">{c.version}</td>
                <td>{formatDate(c.effectiveFrom)}</td>
                <td className="num">
                  <Money value={c.unitCost} />
                </td>
                <td>
                  <StatusBadge status={c.status} />
                </td>
                <td>{c.preparedBy ?? "—"}</td>
                <td>{c.approvedBy ?? "—"}</td>
                <td className="actions">{c.status === "DRAFT" && can("standard_cost:approve") ? <Approve costVersionId={c.costVersionId} onDone={reload} /> : null}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
