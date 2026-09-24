"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
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
  const { companyId, can, scope } = useSession();
  const router = useRouter();
  const create = useCommand<"/api/v1/companies/{companyId}/procurement/create-purchase-order", Values>("create-po", "/api/v1/companies/{companyId}/procurement/create-purchase-order");
  const [values, setValues] = useState<Values>(
    () => create.restored ?? { plantId: "", partyId: "", orderDate: todayInDominicanRepublic(), lines: [{ ...EMPTY_LINE }] },
  );
  const [invalid, setInvalid] = useState<string | null>(null);
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
    if (!values.plantId || !values.partyId || !values.orderDate) {
      setInvalid("Seleccione planta, proveedor y fecha.");
      return;
    }
    if (lines.some((l) => !l.itemId || !l.uom || !isPositiveDecimal(l.quantity, SCALE) || !isPositiveDecimal(l.unitPrice, SCALE))) {
      setInvalid("Cada línea necesita artículo, unidad, cantidad y precio mayores que cero (hasta 6 decimales).");
      return;
    }
    setInvalid(null);
    const response = await create.run({ plantId: values.plantId, partyId: values.partyId, orderDate: values.orderDate, lines }, values);
    if (response) {
      router.push(`/compras/orden/?id=${response.resultRef}`);
    }
  };

  return (
    <>
      <h1>Nueva orden de compra</h1>
      <div>
        <Field label="Planta">
          <select aria-label="Planta de la orden" value={values.plantId} onChange={(e) => setValues({ ...values, plantId: e.target.value })}>
            <option value="">—</option>
            {data.plants.map((p) => (
              <option key={p.plantId} value={p.plantId}>
                {p.code}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Proveedor">
          <select aria-label="Proveedor" value={values.partyId} onChange={(e) => setValues({ ...values, partyId: e.target.value })}>
            <option value="">—</option>
            {data.suppliers.map((s) => (
              <option key={s.supplierId} value={s.supplierId}>
                {s.legalName}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Fecha">
          <input type="date" aria-label="Fecha de la orden" value={values.orderDate} onChange={(e) => setValues({ ...values, orderDate: e.target.value })} />
        </Field>
      </div>
      <table>
        <thead>
          <tr>
            <th>Artículo</th>
            <th>Unidad</th>
            <th className="num">Cantidad</th>
            <th className="num">Precio unitario</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {values.lines.map((line, index) => {
            const item = data.items.find((i) => i.itemId === line.itemId);
            const uoms = item ? [item.baseUom, ...item.conversions.filter((c) => c.toUom === item.baseUom).map((c) => c.fromUom)] : [];
            return (
              <tr key={index}>
                <td>
                  <select aria-label={`Artículo ${index + 1}`} value={line.itemId} onChange={(e) => setLine(index, { itemId: e.target.value, uom: data.items.find((i) => i.itemId === e.target.value)?.baseUom ?? "" })}>
                    <option value="">—</option>
                    {data.items.map((i) => (
                      <option key={i.itemId} value={i.itemId}>
                        {i.code} — {i.description}
                      </option>
                    ))}
                  </select>
                </td>
                <td>
                  <select aria-label={`Unidad ${index + 1}`} value={line.uom} onChange={(e) => setLine(index, { uom: e.target.value })}>
                    {[...new Set(uoms)].map((u) => (
                      <option key={u} value={u}>
                        {u}
                      </option>
                    ))}
                  </select>
                </td>
                <td className="num">
                  <input aria-label={`Cantidad ${index + 1}`} inputMode="decimal" value={line.quantity} onChange={(e) => setLine(index, { quantity: e.target.value })} />
                </td>
                <td className="num">
                  <input aria-label={`Precio ${index + 1}`} inputMode="decimal" value={line.unitPrice} onChange={(e) => setLine(index, { unitPrice: e.target.value })} />
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
      </table>
      <div className="actions">
        <button type="button" onClick={() => setValues({ ...values, lines: [...values.lines, { ...EMPTY_LINE }] })}>
          Agregar línea
        </button>
        <button type="button" disabled={create.busy} onClick={submit}>
          Crear orden
        </button>
      </div>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={create.error} />
    </>
  );
}
