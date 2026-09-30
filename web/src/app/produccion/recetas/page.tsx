"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { itemLabel, PlantSelect, useChosenPlant, useItems, usePlants } from "@/components/Production";
import { ErrorBox, Field, Loading, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
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
  const fe = useFieldErrors();
  if (items.data === null || machines.data === null) {
    return <Loading error={items.error ?? machines.error} />;
  }
  const goods = items.data.filter((i) => i.itemType === "FINISHED_GOOD" && i.status === "ACTIVE");
  const materials = items.data.filter((i) => i.itemType === "RAW_MATERIAL" && i.status === "ACTIVE");
  const setLine = (index: number, patch: Partial<LineDraft>) => setLines(lines.map((l, i) => (i === index ? { ...l, ...patch } : l)));
  const unitsError = "Mayor que cero (hasta 6 decimales).";
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const units = [form.unitsPerBatch, form.unitsPerCycle, form.unitsPerRack].map(normalizeInput);
        const minCuringHours = parseWholeNumber(form.minCuringHours);
        const maxCuringHours = parseWholeNumber(form.maxCuringHours);
        const body = lines.map((l) => ({ materialItemId: l.materialItemId, qtyPerBatch: normalizeInput(l.qtyPerBatch) }));
        const found: Record<string, string | false> = {
          itemId: !form.itemId && "Elija el producto.",
          machineId: !form.machineId && "Elija la máquina.",
          unitsPerBatch: !isPositiveDecimal(units[0] ?? "", 6) && unitsError,
          unitsPerCycle: !isPositiveDecimal(units[1] ?? "", 6) && unitsError,
          unitsPerRack: !isPositiveDecimal(units[2] ?? "", 6) && unitsError,
          minCuringHours: minCuringHours === null && "Horas en número entero (0 o más).",
          maxCuringHours:
            maxCuringHours === null
              ? "Horas en número entero."
              : minCuringHours !== null && maxCuringHours <= minCuringHours && "El máximo debe ser mayor que el mínimo.",
        };
        body.forEach((l, index) => {
          found[`line-${index}-material`] = !l.materialItemId && "Elija la materia prima.";
          found[`line-${index}-quantity`] = !isPositiveDecimal(l.qtyPerBatch, 6) && "Cantidad por tanda mayor que cero (hasta 6 decimales).";
        });
        if (!fe.check(found) || minCuringHours === null || maxCuringHours === null) {
          return;
        }
        const machine = machines.data?.items.find((m) => m.machineId === form.machineId);
        const good = goods.find((g) => g.itemId === form.itemId);
        const response = await prepare.run(
          {
            plantId,
            itemId: form.itemId,
            machineId: form.machineId,
            unitsPerBatch: units[0] ?? "",
            unitsPerCycle: units[1] ?? "",
            unitsPerRack: units[2] ?? "",
            minCuringHours,
            maxCuringHours,
            lines: body,
          },
          undefined,
          `Receta de ${good?.code ?? "producto"} en ${machine?.code ?? "la máquina"} preparada; falta que el Gerente de planta la apruebe.`,
        );
        if (response) {
          setForm(EMPTY_FORM);
          setLines([{ materialItemId: "", qtyPerBatch: "" }]);
          onDone(response.resultRef);
        }
      }}
    >
      <h2>Preparar receta</h2>
      <div>
        <Field label="Producto" required error={fe.errors.itemId}>
          <select aria-label="Producto" value={form.itemId} onChange={(e) => setForm({ ...form, itemId: e.target.value })}>
            <option value="">—</option>
            {goods.map((g) => (
              <option key={g.itemId} value={g.itemId}>
                {itemLabel(g)}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Máquina" required error={fe.errors.machineId}>
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
        <Field label="Unidades por tanda" required error={fe.errors.unitsPerBatch}>
          <input inputMode="decimal" value={form.unitsPerBatch} onChange={(e) => setForm({ ...form, unitsPerBatch: e.target.value })} />
        </Field>
        <Field label="Unidades por ciclo" required error={fe.errors.unitsPerCycle}>
          <input inputMode="decimal" value={form.unitsPerCycle} onChange={(e) => setForm({ ...form, unitsPerCycle: e.target.value })} />
        </Field>
        <Field label="Unidades por rack" required error={fe.errors.unitsPerRack}>
          <input inputMode="decimal" value={form.unitsPerRack} onChange={(e) => setForm({ ...form, unitsPerRack: e.target.value })} />
        </Field>
        <Field label="Curado mínimo (horas)" required error={fe.errors.minCuringHours}>
          <input inputMode="numeric" value={form.minCuringHours} onChange={(e) => setForm({ ...form, minCuringHours: e.target.value })} />
        </Field>
        <Field label="Curado máximo (horas)" required error={fe.errors.maxCuringHours}>
          <input inputMode="numeric" value={form.maxCuringHours} onChange={(e) => setForm({ ...form, maxCuringHours: e.target.value })} />
        </Field>
      </div>
      <h3>Materiales por tanda</h3>
      {lines.map((line, index) => (
        <div key={index}>
          <Field label={`Material ${index + 1}`} required error={fe.errors[`line-${index}-material`]}>
            <select aria-label={`Material ${index + 1}`} value={line.materialItemId} onChange={(e) => setLine(index, { materialItemId: e.target.value })}>
              <option value="">—</option>
              {materials.map((m) => (
                <option key={m.itemId} value={m.itemId}>
                  {itemLabel(m)}
                </option>
              ))}
            </select>
          </Field>
          <Field label={`Cantidad por tanda ${index + 1}`} required error={fe.errors[`line-${index}-quantity`]}>
            <input inputMode="decimal" value={line.qtyPerBatch} onChange={(e) => setLine(index, { qtyPerBatch: e.target.value })} />
          </Field>
          {lines.length > 1 ? (
            <button type="button" onClick={() => setLines(lines.filter((_, i) => i !== index))}>
              Quitar
            </button>
          ) : null}
        </div>
      ))}
      <ErrorBox error={prepare.error} />
      <div className="actions form-actions">
        <button type="button" onClick={() => setLines([...lines, { materialItemId: "", qtyPerBatch: "" }])}>
          Agregar material
        </button>
        <button type="submit" className="primary" disabled={prepare.busy}>
          Preparar receta
        </button>
      </div>
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
        <div className="table-wrap"><table>
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
        </table></div>
      )}
    </>
  );
}
