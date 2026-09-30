"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { FINISHED_GOOD_CATEGORIES } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-02-12): finished goods — create (item:create) and activate (item:activate, Controller).

function CreateFinishedGood({ onDone }: { onDone: () => void }) {
  const create = useCommand("create-finished-good", "/api/v1/companies/{companyId}/master-data/create-finished-good");
  const [form, setForm] = useState({ code: "", description: "", baseUom: "un", itemCategory: "BLOQUE" });
  const fe = useFieldErrors<"code" | "description" | "baseUom">();
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [key]: e.target.value });
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (
          !fe.check({
            code: form.code.trim() === "" && "Indique el código del producto.",
            description: form.description.trim() === "" && "Indique la descripción.",
            baseUom: form.baseUom.trim() === "" && "Indique la unidad base.",
          })
        ) {
          return;
        }
        if (await create.run({ code: form.code.trim(), description: form.description.trim(), baseUom: form.baseUom.trim(), itemCategory: form.itemCategory }, undefined, `Producto ${form.code.trim()} creado en borrador.`)) {
          setForm({ ...form, code: "", description: "" });
          onDone();
        }
      }}
    >
      <Field label="Código" required error={fe.errors.code}>
        <input value={form.code} onChange={set("code")} />
      </Field>
      <Field label="Descripción" required error={fe.errors.description}>
        <input value={form.description} onChange={set("description")} />
      </Field>
      <Field label="Unidad base" required error={fe.errors.baseUom}>
        <input value={form.baseUom} onChange={set("baseUom")} size={6} />
      </Field>
      <Field label="Categoría" required>
        <select value={form.itemCategory} onChange={set("itemCategory")}>
          {Object.entries(FINISHED_GOOD_CATEGORIES).map(([code, label]) => (
            <option key={code} value={code}>
              {label}
            </option>
          ))}
        </select>
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={create.busy}>
          Crear producto
        </button>
      </div>
      <ErrorBox error={create.error} />
    </form>
  );
}

function Activate({ itemId, code, version, onDone }: { itemId: string; code: string; version: number; onDone: () => void }) {
  const activate = useCommand(`activate-item:${itemId}`, "/api/v1/companies/{companyId}/master-data/activate-item", `Producto ${code} activado.`);
  return (
    <>
      <button type="button" disabled={activate.busy} onClick={async () => (await activate.run({ itemId, expectedVersion: version })) && onDone()}>
        Activar
      </button>
      <ErrorBox error={activate.error} />
    </>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const { data, error, reload } = useLoad(
    can("master_data:read") ? () => query("/api/v1/companies/{companyId}/master-data/items", { path: { companyId }, query: { limit: 200 } }) : null,
    [companyId],
  );
  if (!can("master_data:read")) {
    return <NoPermission />;
  }
  const goods = data?.items.filter((i) => i.itemType === "FINISHED_GOOD") ?? [];
  return (
    <>
      <h1>Productos terminados</h1>
      {can("item:create") ? <CreateFinishedGood onDone={reload} /> : null}
      {data === null ? (
        <Loading error={error} />
      ) : goods.length === 0 ? (
        <p className="muted">No hay productos terminados.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Código</th>
              <th>Descripción</th>
              <th>Categoría</th>
              <th>Unidad</th>
              <th>Estado</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {goods.map((i) => (
              <tr key={`${i.itemId}:${i.version}`}>
                <td className="mono">{i.code}</td>
                <td className="wrap">{i.description}</td>
                <td>{FINISHED_GOOD_CATEGORIES[i.itemCategory] ?? i.itemCategory}</td>
                <td>{i.baseUom}</td>
                <td>
                  <StatusBadge status={i.status} />
                </td>
                <td className="actions">{i.status !== "ACTIVE" && can("item:activate") ? <Activate itemId={i.itemId} code={i.code} version={i.version} onDone={reload} /> : null}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
