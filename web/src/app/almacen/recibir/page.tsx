"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
import { formatQuantity, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

interface Values {
  locationId: string;
  occurredAt: string;
  weighTicketRef: string;
  lines: Record<string, { quantity: string; supplierLotNumber: string }>;
}

/** Local date-time for <input type="datetime-local"> (the browser's clock, in the plant's time zone). */
function localNow(): string {
  const now = new Date();
  now.setSeconds(0, 0);
  return new Date(now.getTime() - now.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
}

function Receive() {
  const { companyId, can, plantFor } = useSession();
  const router = useRouter();
  const poId = useSearchParams().get("oc") ?? "";
  const post = useCommand<"/api/v1/companies/{companyId}/procurement/post-goods-receipt", Values>(`post-gr:${poId}`, "/api/v1/companies/{companyId}/procurement/post-goods-receipt");
  const [values, setValues] = useState<Values>(() => post.restored ?? { locationId: "", occurredAt: localNow(), weighTicketRef: "", lines: {} });
  const [invalid, setInvalid] = useState<string | null>(null);
  const allowed = can("goods_receipt:post");

  const { data, error } = useLoad(
    allowed && poId
      ? async () => {
          const order = await query("/api/v1/companies/{companyId}/procurement/purchase-orders/{purchaseOrderId}", {
            path: { companyId, purchaseOrderId: poId },
            query: { plantId: plantFor("purchase_order:read") },
          });
          const plants = await query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId }, query: { plantId: plantFor("master_data:read") ?? order.plantId } });
          return { order, locations: plants.items.find((p) => p.plantId === order.plantId)?.locations ?? [] };
        }
      : null,
    [companyId, poId, allowed],
  );

  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const { order, locations } = data;

  const submit = async () => {
    const lines = order.lines
      .map((l) => ({ line: l, input: values.lines[l.poLineId] }))
      .filter((x) => x.input && normalizeInput(x.input.quantity) !== "")
      .map((x) => ({
        purchaseOrderLineId: x.line.poLineId,
        quantity: normalizeInput(x.input!.quantity),
        supplierLotNumber: x.input!.supplierLotNumber.trim() || null,
      }));
    if (!values.locationId || !values.occurredAt) {
      setInvalid("Seleccione la ubicación y la fecha y hora de la recepción.");
      return;
    }
    if (lines.length === 0 || lines.some((l) => !isPositiveDecimal(l.quantity, 6))) {
      setInvalid("Indique al menos una cantidad mayor que cero (hasta 6 decimales).");
      return;
    }
    setInvalid(null);
    const response = await post.run(
      {
        plantId: order.plantId,
        purchaseOrderId: order.purchaseOrderId,
        locationId: values.locationId,
        occurredAt: new Date(values.occurredAt).toISOString(),
        lines,
        weighTicketRef: values.weighTicketRef.trim() || null,
      },
      values,
    );
    if (response) {
      router.push(`/almacen/recepcion/?id=${response.resultRef}`);
    }
  };

  const setLine = (poLineId: string, change: Partial<{ quantity: string; supplierLotNumber: string }>) =>
    setValues((v) => ({ ...v, lines: { ...v.lines, [poLineId]: { quantity: "", supplierLotNumber: "", ...v.lines[poLineId], ...change } } }));

  return (
    <>
      <h1>Recibir material — orden {order.poNo}</h1>
      <p>
        Proveedor: {order.supplierName}. Planta: {order.plantCode}.
      </p>
      <div>
        <Field label="Ubicación">
          <select aria-label="Ubicación" value={values.locationId} onChange={(e) => setValues({ ...values, locationId: e.target.value })}>
            <option value="">—</option>
            {locations.map((l) => (
              <option key={l.locationId} value={l.locationId}>
                {l.code}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Fecha y hora (pesaje)">
          <input type="datetime-local" aria-label="Fecha y hora" value={values.occurredAt} onChange={(e) => setValues({ ...values, occurredAt: e.target.value })} />
        </Field>
        <Field label="Ticket de báscula">
          <input aria-label="Ticket de báscula" value={values.weighTicketRef} onChange={(e) => setValues({ ...values, weighTicketRef: e.target.value })} />
        </Field>
      </div>
      <table>
        <thead>
          <tr>
            <th>Artículo</th>
            <th>Unidad</th>
            <th className="num">Pedido</th>
            <th className="num">Ya recibido</th>
            <th className="num">Cantidad a recibir</th>
            <th>Lote del proveedor</th>
          </tr>
        </thead>
        <tbody>
          {order.lines.map((l) => (
            <tr key={l.poLineId}>
              <td>{l.itemCode}</td>
              <td>{l.uom}</td>
              <td className="num">{formatQuantity(l.qtyOrdered)}</td>
              <td className="num">{formatQuantity(l.qtyReceived)}</td>
              <td className="num">
                <input
                  aria-label={`Cantidad a recibir ${l.itemCode}`}
                  inputMode="decimal"
                  value={values.lines[l.poLineId]?.quantity ?? ""}
                  onChange={(e) => setLine(l.poLineId, { quantity: e.target.value })}
                />
              </td>
              <td>
                <input aria-label={`Lote ${l.itemCode}`} value={values.lines[l.poLineId]?.supplierLotNumber ?? ""} onChange={(e) => setLine(l.poLineId, { supplierLotNumber: e.target.value })} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="actions">
        <button type="button" disabled={post.busy} onClick={submit}>
          Registrar recepción
        </button>
      </div>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={post.error} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Receive />
    </Suspense>
  );
}
