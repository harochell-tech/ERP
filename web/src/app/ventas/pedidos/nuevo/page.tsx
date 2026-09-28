"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, Money, NoPermission } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { DELIVERY_TERMS } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-10-4): a DRAFT order — customer, plant, delivery term, site, lines of products of the price list in force. Prices
// and totals are the server's (shown on the order after saving); the screen never multiplies.

interface Line {
  itemId: string;
  uom: string;
  quantity: string;
}

interface Values {
  partyId: string;
  plantId: string;
  deliveryTermCode: string;
  siteAddress: string;
  requestedDate: string;
  customerPoRef: string;
  lines: Line[];
}

const EMPTY_LINE: Line = { itemId: "", uom: "", quantity: "" };

function OrderForm() {
  const { companyId, can, scope } = useSession();
  const router = useRouter();
  const editId = useSearchParams().get("id");
  const formId = editId ? `edit-order:${editId}` : "create-order";
  const create = useCommand<"/api/v1/companies/{companyId}/sales/create-sales-order", Values>(formId, "/api/v1/companies/{companyId}/sales/create-sales-order");
  const update = useCommand<"/api/v1/companies/{companyId}/sales/update-sales-order-draft", Values>(formId, "/api/v1/companies/{companyId}/sales/update-sales-order-draft");
  const [values, setValues] = useState<Values | null>(() => create.restored ?? update.restored ?? null);
  const [invalid, setInvalid] = useState<string | null>(null);
  const allowed = can("sales_order:create");
  const permission = scope("sales_order:create");

  const { data, error } = useLoad(
    allowed
      ? async () => {
          const [customers, plants, lists, order] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/customers", { path: { companyId }, query: { status: "ACTIVE", limit: 200 } }),
            query("/api/v1/companies/{companyId}/sales/plants", { path: { companyId } }),
            query("/api/v1/companies/{companyId}/sales/price-lists", { path: { companyId } }),
            editId ? query("/api/v1/companies/{companyId}/sales/orders/{salesOrderId}", { path: { companyId, salesOrderId: editId } }) : Promise.resolve(null),
          ]);
          const active = lists.items.find((l) => l.status === "ACTIVE");
          const prices = active
            ? (await query("/api/v1/companies/{companyId}/sales/price-lists/{priceListVersionId}", { path: { companyId, priceListVersionId: active.priceListVersionId } })).lines
            : [];
          return {
            customers: customers.items,
            plants: plants.items.filter((p) => permission.companyWide || permission.plants.includes(p.plantId)),
            prices,
            order,
          };
        }
      : null,
    [companyId, allowed, editId],
  );

  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const order = data.order;
  const current: Values =
    values ??
    (order
      ? {
          partyId: order.header.partyId,
          plantId: order.plantId,
          deliveryTermCode: order.header.deliveryTermCode,
          siteAddress: order.siteAddress ?? "",
          requestedDate: order.requestedDate ?? "",
          customerPoRef: order.customerPoRef ?? "",
          lines: order.lines.map((l) => ({ itemId: l.itemId, uom: l.uom, quantity: l.qtyOrdered })),
        }
      : { partyId: "", plantId: data.plants[0]?.plantId ?? "", deliveryTermCode: "PICKUP_AT_PLANT", siteAddress: "", requestedDate: "", customerPoRef: "", lines: [{ ...EMPTY_LINE }] });
  const set = (change: Partial<Values>) => setValues({ ...current, ...change });
  const setLine = (index: number, change: Partial<Line>) => set({ lines: current.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) });

  if (order && order.header.status !== "DRAFT") {
    return <p className="muted">Solo se edita un pedido en borrador.</p>;
  }
  if (data.prices.length === 0) {
    return <p className="muted">No hay lista de precios vigente: el Controller la prepara y el Aprobador de políticas la aprueba (Maestros › Lista de precios).</p>;
  }

  const submit = async () => {
    const lines = current.lines.map((l) => ({ ...l, quantity: normalizeInput(l.quantity) }));
    if (!current.partyId || !current.plantId) {
      setInvalid("Elija cliente y planta.");
      return;
    }
    if (current.deliveryTermCode === "DELIVERED_OWN_TRANSPORT" && current.siteAddress.trim() === "") {
      setInvalid("Una entrega en obra necesita la dirección de la obra.");
      return;
    }
    if (lines.some((l) => !l.itemId || !isPositiveDecimal(l.quantity, 6))) {
      setInvalid("Cada línea necesita producto y cantidad mayor que cero.");
      return;
    }
    setInvalid(null);
    const header = {
      plantId: current.plantId,
      deliveryTermCode: current.deliveryTermCode,
      siteAddress: current.siteAddress.trim() === "" ? null : current.siteAddress.trim(),
      requestedDate: current.requestedDate === "" ? null : current.requestedDate,
      customerPoRef: current.customerPoRef.trim() === "" ? null : current.customerPoRef.trim(),
      lines,
    };
    const response = order
      ? await update.run({ salesOrderId: order.header.salesOrderId, expectedVersion: order.header.version, ...header }, current)
      : await create.run({ partyId: current.partyId, ...header }, current);
    if (response) {
      router.push(`/ventas/pedido/?id=${order ? order.header.salesOrderId : response.resultRef}`);
    }
  };

  return (
    <>
      <h1>{order ? `Editar pedido ${order.header.orderNo}` : "Nuevo pedido de venta"}</h1>
      <div>
        <Field label="Cliente">
          <select aria-label="Cliente" value={current.partyId} disabled={order !== null} onChange={(e) => set({ partyId: e.target.value })}>
            <option value="">—</option>
            {data.customers.map((c) => (
              <option key={c.partyId} value={c.partyId}>
                {c.legalName} ({c.rnc})
              </option>
            ))}
          </select>
        </Field>
        <Field label="Planta">
          <select aria-label="Planta del pedido" value={current.plantId} onChange={(e) => set({ plantId: e.target.value })}>
            {data.plants.map((p) => (
              <option key={p.plantId} value={p.plantId}>
                {p.code}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Entrega">
          <select aria-label="Término de entrega" value={current.deliveryTermCode} onChange={(e) => set({ deliveryTermCode: e.target.value })}>
            {Object.entries(DELIVERY_TERMS).map(([code, label]) => (
              <option key={code} value={code}>
                {label}
              </option>
            ))}
          </select>
        </Field>
        {current.deliveryTermCode === "DELIVERED_OWN_TRANSPORT" ? (
          <Field label="Dirección de la obra">
            <input aria-label="Dirección de la obra" value={current.siteAddress} onChange={(e) => set({ siteAddress: e.target.value })} />
          </Field>
        ) : null}
        <Field label="Fecha solicitada (opcional)">
          <input type="date" value={current.requestedDate} onChange={(e) => set({ requestedDate: e.target.value })} />
        </Field>
        <Field label="Orden de compra del cliente (opcional)">
          <input value={current.customerPoRef} onChange={(e) => set({ customerPoRef: e.target.value })} />
        </Field>
      </div>
      <table>
        <thead>
          <tr>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Precio de lista</th>
            <th className="num">Cantidad</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {current.lines.map((line, index) => {
            const price = data.prices.find((p) => p.itemId === line.itemId && p.uom === line.uom);
            return (
              <tr key={index}>
                <td>
                  <select
                    aria-label={`Producto ${index + 1}`}
                    value={line.itemId && line.uom ? `${line.itemId}|${line.uom}` : ""}
                    onChange={(e) => {
                      const [itemId = "", uom = ""] = e.target.value.split("|");
                      setLine(index, { itemId, uom });
                    }}
                  >
                    <option value="">—</option>
                    {data.prices.map((p) => (
                      <option key={`${p.itemId}|${p.uom}`} value={`${p.itemId}|${p.uom}`}>
                        {p.itemCode} — {p.itemDescription} ({p.uom})
                      </option>
                    ))}
                  </select>
                </td>
                <td>{line.uom}</td>
                <td className="num">
                  <Money value={price?.unitPrice} />
                </td>
                <td className="num">
                  <input aria-label={`Cantidad ${index + 1}`} inputMode="decimal" value={line.quantity} onChange={(e) => setLine(index, { quantity: e.target.value })} />
                </td>
                <td>
                  {current.lines.length > 1 ? (
                    <button type="button" onClick={() => set({ lines: current.lines.filter((_, i) => i !== index) })}>
                      Quitar
                    </button>
                  ) : null}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <p className="muted">El total del pedido lo calcula el sistema al guardar, con los precios de la lista vigente.</p>
      <div className="actions">
        <button type="button" onClick={() => set({ lines: [...current.lines, { ...EMPTY_LINE }] })}>
          Agregar línea
        </button>
        <button type="button" className="primary" disabled={create.busy || update.busy} onClick={submit}>
          {order ? "Guardar borrador" : "Crear pedido"}
        </button>
      </div>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={create.error ?? update.error} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <OrderForm />
    </Suspense>
  );
}
