"use client";

import { useId, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, FieldMessage, fieldAria, NoPermission, useFieldErrors } from "@/components/ui";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { matchesSearch } from "@/lib/ux4b";
import { useUomCatalogue } from "@/components/Units";
import { formatQuantity, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Item = Schemas["ItemView"];

// E-B03-15-4: raw materials — list, create (item:create), activate and unit conversions (item:activate).
// UX3-02 (E-UX3-11): the units come from the md.uom catalogue (GET /master-data/uoms), not from a list in the code.
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
  const uoms = useUomCatalogue();
  const fe = useFieldErrors<"code" | "description">();
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (!fe.check({ code: code.trim() === "" && "Indique el código de la materia prima.", description: description.trim() === "" && "Indique la descripción." })) {
          return;
        }
        if (await create.run({ code: code.trim(), description: description.trim(), baseUom, itemCategory }, undefined, `Materia prima ${code.trim()} creada en borrador.`)) {
          setCode("");
          setDescription("");
          onDone();
        }
      }}
    >
      <Field label="Código" required error={fe.errors.code}>
        <input value={code} onChange={(e) => setCode(e.target.value)} />
      </Field>
      <Field label="Descripción" required error={fe.errors.description}>
        <input value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
      <Field label="Unidad base" required>
        <select aria-label="Unidad base" value={baseUom} onChange={(e) => setBaseUom(e.target.value)}>
          {uoms.length === 0 ? <option value={baseUom}>{baseUom}</option> : null}
          {uoms.map((u) => (
            <option key={u.value} value={u.value}>
              {u.label}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Categoría" required>
        <select value={itemCategory} onChange={(e) => setItemCategory(e.target.value)}>
          {Object.entries(CATEGORIES).map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </select>
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={create.busy}>
          Crear materia prima
        </button>
      </div>
      <ErrorBox error={create.error} />
    </form>
  );
}

function ItemRow({ item, onDone }: { item: Item; onDone: () => void }) {
  const { can } = useSession();
  const id = item.itemId;
  const activate = useCommand(`activate-item:${id}`, "/api/v1/companies/{companyId}/master-data/activate-item", `Materia prima ${item.code} activada.`);
  const convert = useCommand(`uom-conversion:${id}`, "/api/v1/companies/{companyId}/master-data/define-uom-conversion");
  const uoms = useUomCatalogue().filter((u) => u.value !== item.baseUom);
  const [chosenUom, setFromUom] = useState<string>("");
  const fromUom = chosenUom || (uoms[0]?.value ?? "");
  const [factor, setFactor] = useState("");
  const [effectiveFrom, setEffectiveFrom] = useState("");
  const fe = useFieldErrors<"factor" | "effectiveFrom">();
  const messageId = useId();

  return (
    <tr>
      <td>{item.code}</td>
      <td className="wrap">{item.description}</td>
      <td>{CATEGORIES[item.itemCategory] ?? item.itemCategory}</td>
      <td>{item.baseUom}</td>
      <td>
        {item.conversions.length === 0 ? (
          <span className="muted">—</span>
        ) : (
          <ul>
            {item.conversions.map((c) => (
              <li key={`${c.fromUom}:${c.effectiveFrom}`} data-testid={`conversion:${item.code}:${c.fromUom}`}>
                {/* UX4-03 (C-32): the factor without trailing zeros, as a sentence. */}
                1 {c.fromUom} equivale a <strong>{formatQuantity(c.factor)} {c.toUom}</strong>
                <span className="muted">
                  {" "}
                  · desde {formatDate(c.effectiveFrom)}
                  {c.effectiveTo ? ` hasta ${formatDate(c.effectiveTo)}` : ""}
                </span>
              </li>
            ))}
          </ul>
        )}
        {can("item:activate") ? (
          <form
            className="inline-form"
            noValidate
            onSubmit={async (e) => {
              e.preventDefault();
              const trimmed = normalizeInput(factor);
              if (
                !fe.check({
                  factor: !isPositiveDecimal(trimmed, 8) && "Indique un factor mayor que cero (hasta 8 decimales).",
                  effectiveFrom: effectiveFrom === "" && "Indique desde qué fecha rige.",
                })
              ) {
                return;
              }
              if (await convert.run({ itemId: id, fromUom, factor: trimmed, effectiveFrom }, undefined, `Conversión de ${fromUom} a ${item.baseUom} definida para ${item.code}.`)) {
                setFactor("");
                onDone();
              }
            }}
          >
            1{" "}
            <select aria-label="Unidad de compra" value={fromUom} onChange={(e) => setFromUom(e.target.value)}>
              {uoms.map((u) => (
                <option key={u.value} value={u.value}>
                  {u.label}
                </option>
              ))}
            </select>{" "}
            ={" "}
            <input
              aria-label="Factor"
              inputMode="decimal"
              size={8}
              value={factor}
              onChange={(e) => setFactor(e.target.value)}
              {...fieldAria(fe.errors.factor, `${messageId}-factor`, true)}
            />{" "}
            {item.baseUom} desde{" "}
            <input
              aria-label="Vigente desde"
              type="date"
              value={effectiveFrom}
              onChange={(e) => setEffectiveFrom(e.target.value)}
              {...fieldAria(fe.errors.effectiveFrom, `${messageId}-from`, true)}
            />
            <button type="submit" disabled={convert.busy}>
              Definir conversión
            </button>
            <FieldMessage id={`${messageId}-factor`} error={fe.errors.factor} />
            <FieldMessage id={`${messageId}-from`} error={fe.errors.effectiveFrom} />
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
  const [search, setSearch] = useState("");
  const [category, setCategory] = useState("");
  const [creating, setCreating] = useState(false);

  if (!can("master_data:read")) {
    return <NoPermission />;
  }
  // UX4-03 (C-32): this screen is the raw materials; finished goods have their own (Maestros › Productos terminados).
  const materials = data?.items.filter((i) => i.itemType === "RAW_MATERIAL") ?? [];
  const shown = materials.filter((i) => (category === "" || i.itemCategory === category) && matchesSearch(search, i.code, i.description));
  return (
    <>
      <h1>Materias primas</h1>
      <p className="muted">Cemento, agregados, aditivos y demás materiales que se compran y se consumen en producción. Los bloques y demás productos están en Productos terminados.</p>
      {can("item:create") ? (
        creating ? (
          <CreateItem
            onDone={() => {
              setCreating(false);
              reload();
            }}
          />
        ) : (
          <div className="actions">
            <button type="button" className="primary" onClick={() => setCreating(true)}>
              Nueva materia prima
            </button>
          </div>
        )
      ) : null}
      <div className="inline-form" role="search">
        <Field label="Buscar">
          <input type="search" placeholder="Código o descripción" value={search} onChange={(e) => setSearch(e.target.value)} />
        </Field>
        <Field label="Mostrar categoría">
          <select value={category} onChange={(e) => setCategory(e.target.value)}>
            <option value="">Todas</option>
            {Object.entries(CATEGORIES).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
        </Field>
      </div>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : materials.length === 0 ? (
        <EmptyState title="Aún no hay materias primas.">
          <p>{can("item:create") ? "Cree la primera con «Nueva materia prima»; quedará en borrador hasta que se active." : "Quien tenga el permiso de crear artículos las registra aquí."}</p>
        </EmptyState>
      ) : shown.length === 0 ? (
        <EmptyState title="Ninguna materia prima coincide con la búsqueda.">
          <p>Pruebe con otra palabra o elija «Todas» las categorías.</p>
        </EmptyState>
      ) : (
        <div className="table-wrap"><table>
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
            {shown.map((i) => (
              <ItemRow key={`${i.itemId}:${i.version}:${i.conversions.length}`} item={i} onDone={reload} />
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
