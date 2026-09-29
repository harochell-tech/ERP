"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { itemLabel, PlantSelect, useChosenPlant, useItems, usePlants } from "@/components/Production";
import { ErrorBox, Field, Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatQuantity, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { parseWholeNumber } from "@/lib/production";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// MFG1-07 (E-MFG1-07-4): the recipes of a plant — one per product and machine — prepared by the Supervisor de producción and
// approved by the Gerente de planta (four eyes). Quantities stay decimal strings.

interface LineDraft {
  materialItemId: string;
  qtyPerBatch: string;
}

const EMPTY_FORM = { itemId: "", machineId: "", unitsPerBatch: "", unitsPerCycle: "", unitsPerRack: "", minCuringHours: "", maxCuringHours: "" };

function PrepareRecipe({ plantId, onDone }: { plantId: string; onDone: (recipeVersionId: string) => void }) {
  const { companyId } = useSession();
  const prepare = useCommand("prepare-recipe", "/api/v1/companies/{companyId}/manufacturing/prepare-recipe");
  const items = useItems();
  const machines = useLoad(
    () => query("/api/v1/companies/{companyId}/manufacturing/machines", { path: { companyId }, query: { plantId, status: "ACTIVE" } }),
    [companyId, plantId],
  );
  const [form, setForm] = useState(EMPTY_FORM);
  const [lines, setLines] = useState<LineDraft[]>([{ materialItemId: "", qtyPerBatch: "" }]);
  const [invalid, setInvalid] = useState<string | null>(null);
  if (items.data === null || machines.data === null) {
    return <Loading error={items.error ?? machines.error} />;
  }
  const goods = items.data.filter((i) => i.itemType === "FINISHED_GOOD" && i.status === "ACTIVE");
  const materials = items.data.filter((i) => i.itemType === "RAW_MATERIAL" && i.status === "ACTIVE");
  const setLine = (index: number, patch: Partial<LineDraft>) => setLines(lines.map((l, i) => (i === index ? { ...l, ...patch } : l)));
  return (
    <form
      className="card"
      onSubmit={async (e) => {
        e.preventDefault();
        const units = [form.unitsPerBatch, form.unitsPerCycle, form.unitsPerRack].map(normalizeInput);
        const minCuringHours = parseWholeNumber(form.minCuringHours);
        const maxCuringHours = parseWholeNumber(form.maxCuringHours);
        const body = lines.map((l) => ({ materialItemId: l.materialItemId, qtyPerBatch: normalizeInput(l.qtyPerBatch) }));
        if (!form.itemId || !form.machineId) {
          setInvalid("Elija el producto y la máquina.");
          return;
        }
        if (!units.every((u) => isPositiveDecimal(u, 6))) {
          setInvalid("Las unidades por tanda, por ciclo y por rack son mayores que cero (hasta 6 decimales).");
          return;
        }
        if (minCuringHours === null || maxCuringHours === null || maxCuringHours <= minCuringHours) {
          setInvalid("Indique las horas de curado en números enteros; el máximo debe ser mayor que el mínimo.");
          return;
        }
        if (body.length === 0 || body.some((l) => !l.materialItemId || !isPositiveDecimal(l.qtyPerBatch, 6))) {
          setInvalid("Cada línea necesita una materia prima y una cantidad por tanda mayor que cero (hasta 6 decimales).");
          return;
        }
        setInvalid(null);
        const response = await prepare.run({
          plantId,
          itemId: form.itemId,
          machineId: form.machineId,
          unitsPerBatch: units[0] ?? "",
          unitsPerCycle: units[1] ?? "",
          unitsPerRack: units[2] ?? "",
          minCuringHours,
          maxCuringHours,
          lines: body,
        });
        if (response) {
          setForm(EMPTY_FORM);
          setLines([{ materialItemId: "", qtyPerBatch: "" }]);
          onDone(response.resultRef);
        }
      }}
    >
      <h2>Preparar receta</h2>
      <div>
        <Field label="Producto">
          <select aria-label="Producto" value={form.itemId} onChange={(e) => setForm({ ...form, itemId: e.target.value })}>
            <option value="">—</option>
            {goods.map((g) => (
              <option key={g.itemId} value={g.itemId}>
                {itemLabel(g)}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Máquina">
          <select aria-label="Máquina" value={form.machineId} onChange={(e) => setForm({ ...form, machineId: e.target.value })}>
            <option value="">—</option>
            {machines.data.items.map((m) => (
              <option key={m.machineId} value={m.machineId}>
                {m.code} — {m.name}
              </option>
            ))}
          </select>
        </Field>
      </div>
      <div>
        <Field label="Unidades por tanda">
          <input inputMode="decimal" value={form.unitsPerBatch} onChange={(e) => setForm({ ...form, unitsPerBatch: e.target.value })} />
        </Field>
        <Field label="Unidades por ciclo">
          <input inputMode="decimal" value={form.unitsPerCycle} onChange={(e) => setForm({ ...form, unitsPerCycle: e.target.value })} />
        </Field>
        <Field label="Unidades por rack">
          <input inputMode="decimal" value={form.unitsPerRack} onChange={(e) => setForm({ ...form, unitsPerRack: e.target.value })} />
        </Field>
        <Field label="Curado mínimo (horas)">
          <input inputMode="numeric" value={form.minCuringHours} onChange={(e) => setForm({ ...form, minCuringHours: e.target.value })} />
        </Field>
        <Field label="Curado máximo (horas)">
          <input inputMode="numeric" value={form.maxCuringHours} onChange={(e) => setForm({ ...form, maxCuringHours: e.target.value })} />
        </Field>
      </div>
      <h3>Materiales por tanda</h3>
      {lines.map((line, index) => (
        <div key={index}>
          <Field label={`Material ${index + 1}`}>
            <select aria-label={`Material ${index + 1}`} value={line.materialItemId} onChange={(e) => setLine(index, { materialItemId: e.target.value })}>
              <option value="">—</option>
              {materials.map((m) => (
                <option key={m.itemId} value={m.itemId}>
                  {itemLabel(m)}
                </option>
              ))}
            </select>
          </Field>
          <Field label={`Cantidad por tanda ${index + 1}`}>
            <input inputMode="decimal" value={line.qtyPerBatch} onChange={(e) => setLine(index, { qtyPerBatch: e.target.value })} />
          </Field>
          {lines.length > 1 ? (
            <button type="button" onClick={() => setLines(lines.filter((_, i) => i !== index))}>
              Quitar
            </button>
          ) : null}
        </div>
      ))}
      <div className="actions">
        <button type="button" onClick={() => setLines([...lines, { materialItemId: "", qtyPerBatch: "" }])}>
          Agregar material
        </button>
        <button type="submit" disabled={prepare.busy}>
          Preparar receta
        </button>
      </div>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={prepare.error} />
    </form>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const plants = usePlants();
  const { plant, setPlant } = useChosenPlant(plants.data);
  const plantId = plant?.plantId ?? "";
  const [status, setStatus] = useState("");
  const [prepared, setPrepared] = useState<string | null>(null);
  const { data, error, reload } = useLoad(
    can("production:read") && plantId
      ? () => query("/api/v1/companies/{companyId}/manufacturing/recipes", { path: { companyId }, query: { plantId, status } })
      : null,
    [companyId, plantId, status],
  );
  if (!can("production:read")) {
    return <NoPermission />;
  }
  if (plants.data === null) {
    return <Loading error={plants.error} />;
  }
  return (
    <>
      <h1>Recetas</h1>
      {plants.data.length === 0 ? <p className="muted">No hay plantas disponibles.</p> : <PlantSelect plants={plants.data} value={plantId} onChange={setPlant} />}
      <Field label="Estado">
        <select aria-label="Estado" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">Todos</option>
          <option value="DRAFT">Borrador</option>
          <option value="ACTIVE">Activo</option>
          <option value="SUPERSEDED">Reemplazada</option>
        </select>
      </Field>
      {can("recipe:prepare") && plantId ? (
        <PrepareRecipe
          plantId={plantId}
          onDone={(id) => {
            setPrepared(id);
            reload();
          }}
        />
      ) : null}
      {prepared ? (
        <p className="notice" data-testid="recipe-prepared">
          Receta preparada. <Link href={`/produccion/receta/?id=${prepared}`}>Ver la receta</Link>
        </p>
      ) : null}
      {data === null ? (
        plantId ? <Loading error={error} /> : null
      ) : data.items.length === 0 ? (
        <p className="muted">No hay recetas.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Producto</th>
              <th>Máquina</th>
              <th className="num">Versión</th>
              <th>Vigente desde</th>
              <th className="num">Unidades por tanda</th>
              <th className="num">Por rack</th>
              <th>Curado (h)</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((r) => (
              <tr key={`${r.recipeVersionId}:${r.status}`}>
                <td>
                  <Link href={`/produccion/receta/?id=${r.recipeVersionId}`}>
                    {r.itemCode} — {r.itemName}
                  </Link>
                </td>
                <td className="mono">{r.machineCode}</td>
                <td className="num">{r.version}</td>
                <td>{formatDate(r.effectiveFrom)}</td>
                <td className="num">{formatQuantity(r.unitsPerBatch)}</td>
                <td className="num">{formatQuantity(r.unitsPerRack)}</td>
                <td>
                  {r.minCuringHours} – {r.maxCuringHours}
                </td>
                <td>
                  <StatusBadge status={r.status} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
