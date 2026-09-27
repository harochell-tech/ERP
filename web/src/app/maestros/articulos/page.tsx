"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
import { formatDate, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Item = Schemas["ItemView"];

// E-B03-15-4: raw materials — list, create (item:create), activate and unit conversions (item:activate).

const UOMS = ["kg", "t", "m3", "l", "un"] as const;
const CATEGORIES: Readonly<Record<string, string>> = {
  CEMENTO: "Cemento",
  AGREGADO: "Agregado",
  ADITIVO: "Aditivo",
  OTRA_MATERIA_PRIMA: "Otra materia prima",
};

function CreateItem({ onDone }: { onDone: () => void }) {
  const create = useCommand("create-raw-material", "/api/v1/companies/{companyId}/master-data/create-raw-material");
  const [code, setCode] = useState("");
  const [description, setDescription] = useState("");
  const [baseUom, setBaseUom] = useState<string>("kg");
  const [itemCategory, setItemCategory] = useState("CEMENTO");
  return (
    <form
      className="inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        if (await create.run({ code: code.trim(), description: description.trim(), baseUom, itemCategory })) {
          setCode("");
          setDescription("");
          onDone();
        }
      }}
    >
      <Field label="Código">
        <input value={code} onChange={(e) => setCode(e.target.value)} required />
      </Field>
      <Field label="Descripción">
        <input value={description} onChange={(e) => setDescription(e.target.value)} required />
      </Field>
      <Field label="Unidad base">
        <select value={baseUom} onChange={(e) => setBaseUom(e.target.value)}>
          {UOMS.map((u) => (
            <option key={u}>{u}</option>
          ))}
        </select>
      </Field>
      <Field label="Categoría">
        <select value={itemCategory} onChange={(e) => setItemCategory(e.target.value)}>
          {Object.entries(CATEGORIES).map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </select>
      </Field>
      <button type="submit" disabled={create.busy}>
        Crear materia prima
      </button>
      <ErrorBox error={create.error} />
    </form>
  );
}

function ItemRow({ item, onDone }: { item: Item; onDone: () => void }) {
  const { can } = useSession();
  const id = item.itemId;
  const activate = useCommand(`activate-item:${id}`, "/api/v1/companies/{companyId}/master-data/activate-item");
  const convert = useCommand(`uom-conversion:${id}`, "/api/v1/companies/{companyId}/master-data/define-uom-conversion");
  const [fromUom, setFromUom] = useState<string>(UOMS.find((u) => u !== item.baseUom) ?? "un");
  const [factor, setFactor] = useState("");
  const [effectiveFrom, setEffectiveFrom] = useState("");

  return (
    <tr>
      <td>{item.code}</td>
      <td>{item.description}</td>
      <td>{CATEGORIES[item.itemCategory] ?? item.itemCategory}</td>
      <td>{item.baseUom}</td>
      <td>
        {item.conversions.length === 0 ? (
          <span className="muted">—</span>
        ) : (
          <ul>
            {item.conversions.map((c) => (
              <li key={`${c.fromUom}:${c.effectiveFrom}`}>
                1 {c.fromUom} = {c.factor} {c.toUom} desde {formatDate(c.effectiveFrom)}
                {c.effectiveTo ? ` hasta ${formatDate(c.effectiveTo)}` : ""}
              </li>
            ))}
          </ul>
        )}
        {can("item:activate") ? (
          <form
            className="inline-form"
            onSubmit={async (e) => {
              e.preventDefault();
              if (await convert.run({ itemId: id, fromUom, factor: factor.trim(), effectiveFrom })) {
                setFactor("");
                onDone();
              }
            }}
          >
            1{" "}
            <select aria-label="Unidad de compra" value={fromUom} onChange={(e) => setFromUom(e.target.value)}>
              {UOMS.filter((u) => u !== item.baseUom).map((u) => (
                <option key={u}>{u}</option>
              ))}
            </select>{" "}
            = <input aria-label="Factor" inputMode="decimal" size={8} value={factor} onChange={(e) => setFactor(e.target.value)} required /> {item.baseUom}{" "}
            desde <input aria-label="Vigente desde" type="date" value={effectiveFrom} onChange={(e) => setEffectiveFrom(e.target.value)} required />
            <button type="submit" disabled={convert.busy}>
              Definir conversión
            </button>
          </form>
        ) : null}
        <ErrorBox error={convert.error} />
      </td>
      <td>{statusLabel(item.status)}</td>
      <td>
        {item.status !== "ACTIVE" && can("item:activate") ? (
          <button type="button" disabled={activate.busy} onClick={async () => (await activate.run({ itemId: id, expectedVersion: item.version })) && onDone()}>
            Activar
          </button>
        ) : null}
        <ErrorBox error={activate.error} />
      </td>
    </tr>
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
  return (
    <>
      <h1>Materias primas</h1>
      {can("item:create") ? <CreateItem onDone={reload} /> : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay materias primas.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Código</th>
              <th>Descripción</th>
              <th>Categoría</th>
              <th>Unidad base</th>
              <th>Conversiones</th>
              <th>Estado</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.items.map((i) => (
              <ItemRow key={`${i.itemId}:${i.version}:${i.conversions.length}`} item={i} onDone={reload} />
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
