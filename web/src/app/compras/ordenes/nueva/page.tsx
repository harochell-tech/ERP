"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, Loading, NoPermission, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

interface Line {
  itemId: string;
  uom: string;
  quantity: string;
  unitPrice: string;
}

interface Values {
  plantId: string;
  partyId: string;
  orderDate: string;
  lines: Line[];
}

const EMPTY_LINE: Line = { itemId: "", uom: "", quantity: "", unitPrice: "" };
// PO line columns: quantity numeric(18,6), unit price numeric(19,6).
const SCALE = 6;

export default function NewPurchaseOrder() {
  const { companyId, can, scope, plantName } = useSession();
  const router = useRouter();
  const create = useCommand<"/api/v1/companies/{companyId}/procurement/create-purchase-order", Values>(
    "create-po",
    "/api/v1/companies/{companyId}/procurement/create-purchase-order",
    (_, doc) => (doc ? `Orden de compra ${doc} creada en borrador.` : "Orden de compra creada en borrador."),
  );
  const [values, setValues] = useState<Values>(
    () => create.restored ?? { plantId: "", partyId: "", orderDate: todayInDominicanRepublic(), lines: [{ ...EMPTY_LINE }] },
  );
  const fe = useFieldErrors();
  const permission = scope("purchase_order:create");
  const allowed = can("purchase_order:create");

  const { data, error } = useLoad(
    allowed
      ? async () => {
          const plantFilter = permission.companyWide ? undefined : permission.plants[0];
          const [plants, suppliers, items] = await Promise.all([
            query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId }, query: { plantId: plantFilter } }),
            query("/api/v1/companies/{companyId}/master-data/suppliers", { path: { companyId }, query: { status: "ACTIVE", plantId: plantFilter, limit: 200 } }),
            query("/api/v1/companies/{companyId}/master-data/items", { path: { companyId }, query: { status: "ACTIVE", plantId: plantFilter, limit: 200 } }),
          ]);
          return {
            plants: plants.items.filter((p) => permission.companyWide || permission.plants.includes(p.plantId)),
            suppliers: suppliers.items,
            items: items.items,
          };
        }
      : null,
    [companyId, allowed],
  );

  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }

  const setLine = (index: number, change: Partial<Line>) =>
    setValues((v) => ({ ...v, lines: v.lines.map((line, i) => (i === index ? { ...line, ...change } : line)) }));

  const submit = async () => {
    const lines = values.lines.map((l) => ({ ...l, quantity: normalizeInput(l.quantity), unitPrice: normalizeInput(l.unitPrice) }));
    const found: Record<string, string | false> = {
      plantId: !values.plantId && "Elija la planta.",
      partyId: !values.partyId && "Elija el proveedor.",
      orderDate: !values.orderDate && "Indique la fecha de la orden.",
    };
    lines.forEach((l, i) => {
      found[`line-${i}-itemId`] = !l.itemId && "Elija el artículo.";
      found[`line-${i}-uom`] = !!l.itemId && !l.uom && "Elija la unidad.";
      found[`line-${i}-quantity`] = !isPositiveDecimal(l.quantity, SCALE) && "Cantidad mayor que cero (hasta 6 decimales).";
      found[`line-${i}-unitPrice`] = !isPositiveDecimal(l.unitPrice, SCALE) && "Precio mayor que cero (hasta 6 decimales).";
    });
    if (!fe.check(found)) {
      return;
    }
    const response = await create.run({ plantId: values.plantId, partyId: values.partyId, orderDate: values.orderDate, lines }, values);
    if (response) {
      router.push(`/compras/orden/?id=${response.resultRef}`);
    }
  };

  const lineError = (index: number, field: string) => fe.errors[`line-${index}-${field}`];

  return (
    <>
      <h1>Nueva orden de compra</h1>
      <div>
        <Field label="Planta" required error={fe.errors.plantId}>
          <select aria-label="Planta de la orden" value={values.plantId} onChange={(e) => setValues({ ...values, plantId: e.target.value })}>
            <option value="">—</option>
            {data.plants.map((p) => (
              <option key={p.plantId} value={p.plantId}>
                {plantName(p.plantId, p.code)}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Proveedor" required error={fe.errors.partyId}>
          <select aria-label="Proveedor" value={values.partyId} onChange={(e) => setValues({ ...values, partyId: e.target.value })}>
            <option value="">—</option>
            {data.suppliers.map((s) => (
              <option key={s.supplierId} value={s.supplierId}>
                {s.legalName}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Fecha" required error={fe.errors.orderDate}>
          <input type="date" aria-label="Fecha de la orden" value={values.orderDate} onChange={(e) => setValues({ ...values, orderDate: e.target.value })} />
        </Field>
      </div>
      <LineTable>
        <thead>
          <tr>
            <th>Artículo</th>
            <th>Unidad</th>
            <th className="num">Cantidad</th>
            <th className="num">Precio unitario (RD$)</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {values.lines.map((line, index) => {
            const item = data.items.find((i) => i.itemId === line.itemId);
            const uoms = item ? [item.baseUom, ...item.conversions.filter((c) => c.toUom === item.baseUom).map((c) => c.fromUom)] : [];
            const id = (field: string) => `po-line-${index}-${field}`;
            return (
              <tr key={index}>
                <td>
                  <select
                    aria-label={`Artículo ${index + 1}`}
                    {...fieldAria(lineError(index, "itemId"), id("itemId"), true)}
                    value={line.itemId}
                    onChange={(e) => setLine(index, { itemId: e.target.value, uom: data.items.find((i) => i.itemId === e.target.value)?.baseUom ?? "" })}
                  >
                    <option value="">—</option>
                    {data.items.map((i) => (
                      <option key={i.itemId} value={i.itemId}>
                        {i.code} — {i.description}
                      </option>
                    ))}
                  </select>
                  <FieldMessage id={id("itemId")} error={lineError(index, "itemId")} />
                </td>
                <td>
                  <select aria-label={`Unidad ${index + 1}`} {...fieldAria(lineError(index, "uom"), id("uom"), true)} value={line.uom} onChange={(e) => setLine(index, { uom: e.target.value })}>
                    {[...new Set(uoms)].map((u) => (
                      <option key={u} value={u}>
                        {u}
                      </option>
                    ))}
                  </select>
                  <FieldMessage id={id("uom")} error={lineError(index, "uom")} />
                </td>
                <td className="num">
                  <input
                    aria-label={`Cantidad ${index + 1}`}
                    {...fieldAria(lineError(index, "quantity"), id("quantity"), true)}
                    inputMode="decimal"
                    value={line.quantity}
                    onChange={(e) => setLine(index, { quantity: e.target.value })}
                  />
                  <FieldMessage id={id("quantity")} error={lineError(index, "quantity")} />
                </td>
                <td className="num">
                  <input
                    aria-label={`Precio ${index + 1}`}
                    {...fieldAria(lineError(index, "unitPrice"), id("unitPrice"), true)}
                    inputMode="decimal"
                    value={line.unitPrice}
                    onChange={(e) => setLine(index, { unitPrice: e.target.value })}
                  />
                  <FieldMessage id={id("unitPrice")} error={lineError(index, "unitPrice")} />
                </td>
                <td>
                  {values.lines.length > 1 ? (
                    <button type="button" onClick={() => setValues({ ...values, lines: values.lines.filter((_, i) => i !== index) })}>
                      Quitar
                    </button>
                  ) : null}
                </td>
              </tr>
            );
          })}
        </tbody>
      </LineTable>
      <div className="actions form-actions">
        <button type="button" onClick={() => setValues({ ...values, lines: [...values.lines, { ...EMPTY_LINE }] })}>
          Agregar línea
        </button>
        <button type="button" className="primary" disabled={create.busy} onClick={submit}>
          Crear orden
        </button>
      </div>
      <ErrorBox error={create.error} />
    </>
  );
}
