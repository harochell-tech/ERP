"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { matchesSearch } from "@/lib/ux4b";
import { useUomCatalogue } from "@/components/Units";
import { FINISHED_GOOD_CATEGORIES } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-02-12): finished goods — create (item:create) and activate (item:activate, Controller).

function CreateFinishedGood({ onDone }: { onDone: () => void }) {
  const create = useCommand("create-finished-good", "/api/v1/companies/{companyId}/master-data/create-finished-good");
  const [form, setForm] = useState({ code: "", description: "", baseUom: "un", itemCategory: "BLOQUE" });
  const fe = useFieldErrors<"code" | "description" | "baseUom">();
  const uoms = useUomCatalogue(); // UX3-02 (E-UX3-11)
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
        <select aria-label="Unidad base" value={form.baseUom} onChange={set("baseUom")}>
          {uoms.length === 0 ? <option value={form.baseUom}>{form.baseUom}</option> : null}
          {uoms.map((u) => (
            <option key={u.value} value={u.value}>
              {u.label}
            </option>
          ))}
        </select>
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

/**
 * PRS-05 (E-PRS-05-3, E-PRS-01-7): the freight item «Transporte de blocks» — one per company, created here while it does not exist and
 * activated like any item; it is what the invoice's freight lines show.
 */
function FreightItem({ item, onDone }: { item: Schemas["ItemView"] | undefined; onDone: () => void }) {
  const { can } = useSession();
  const create = useCommand("create-freight-item", "/api/v1/companies/{companyId}/master-data/create-freight-item");
  if (item === undefined && !can("item:create")) {
    return null;
  }
  return (
    <section data-testid="freight-item">
      <h2>Artículo de flete</h2>
      {item ? (
        <p>
          {item.description} ({item.code}) · <StatusBadge status={item.status} />
          {item.status !== "ACTIVE" && can("item:activate") ? <Activate itemId={item.itemId} code={item.code} version={item.version} onDone={onDone} /> : null}
        </p>
      ) : (
        <>
          <p className="muted">El flete de las entregas con nuestro camión se factura con este artículo de servicio, exento de ITBIS.</p>
          <button
            type="button"
            disabled={create.busy}
            onClick={async () => (await create.run({ description: "Transporte de blocks" }, undefined, "Artículo de flete creado; falta activarlo.")) && onDone()}
          >
            Crear artículo de flete
          </button>
        </>
      )}
      <ErrorBox error={create.error} />
    </section>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const { data, error, reload } = useLoad(
    can("master_data:read") ? () => query("/api/v1/companies/{companyId}/master-data/items", { path: { companyId }, query: { limit: 200 } }) : null,
    [companyId],
  );
  const [search, setSearch] = useState("");
  const [category, setCategory] = useState("");
  if (!can("master_data:read")) {
    return <NoPermission />;
  }
  const goods = data?.items.filter((i) => i.itemType === "FINISHED_GOOD") ?? [];
  // UX4-03 (V-40): search by code or description and filter by category.
  const shown = goods.filter((i) => (category === "" || i.itemCategory === category) && matchesSearch(search, i.code, i.description));
  return (
    <>
      <h1>Productos terminados</h1>
      {can("item:create") ? <CreateFinishedGood onDone={reload} /> : null}
      {data ? <FreightItem item={data.items.find((i) => i.itemType === "SERVICE")} onDone={reload} /> : null}
      <div className="inline-form" role="search">
        <Field label="Buscar producto">
          <input type="search" placeholder="Código o descripción" value={search} onChange={(e) => setSearch(e.target.value)} />
        </Field>
        <Field label="Mostrar categoría">
          <select value={category} onChange={(e) => setCategory(e.target.value)}>
            <option value="">Todas</option>
            {Object.entries(FINISHED_GOOD_CATEGORIES).map(([code, label]) => (
              <option key={code} value={code}>
                {label}
              </option>
            ))}
          </select>
        </Field>
      </div>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : goods.length === 0 ? (
        <EmptyState title="Aún no hay productos terminados.">
          <p>{can("item:create") ? "Cree el primero con el formulario de arriba; queda en borrador hasta que el Controller lo active." : "Quien tenga el permiso de crear artículos los registra aquí."}</p>
        </EmptyState>
      ) : shown.length === 0 ? (
        <EmptyState title="Ningún producto coincide con la búsqueda.">
          <p>Pruebe con otra palabra o elija «Todas» las categorías.</p>
        </EmptyState>
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
            {shown.map((i) => (
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
