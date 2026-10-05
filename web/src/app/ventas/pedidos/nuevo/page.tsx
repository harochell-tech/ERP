"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { CreditPreviewCard, PreviewTotals, useSalesPreview } from "@/components/SalesUx4";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, Money, NoPermission, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { DELIVERY_TERMS } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { allCustomers } from "@/lib/paging";

// VS3-10a (E-VS3-10-4): a DRAFT order — customer, plant, delivery term, site, lines of products of the price list in force. Prices
// and totals are the server's; the screen never multiplies.
// UX4-03 (V-11, E-UX4-3/4): while typing, the server prices the draft (POST preview): net per line, net total, estimated ITBIS and
// total, and the customer's credit with that net.

interface Line {
  itemId: string;
  uom: string;
  quantity: string;
}

/** FIS1b-07 (E-FIS1b-2, E-FIS1b-01-1): NONE, or the exemption in process with proformas that collect WITH or WITHOUT ITBIS. */
type Exemption = "NONE" | "WITH_ITBIS" | "WITHOUT_ITBIS";

interface Values {
  partyId: string;
  plantId: string;
  deliveryTermCode: string;
  siteAddress: string;
  requestedDate: string;
  customerPoRef: string;
  lines: Line[];
  exemption: Exemption;
}

const EMPTY_LINE: Line = { itemId: "", uom: "", quantity: "" };

function OrderForm() {
  const { companyId, can, scope, plantName } = useSession();
  const router = useRouter();
  const editId = useSearchParams().get("id");
  const formId = editId ? `edit-order:${editId}` : "create-order";
  const create = useCommand<"/api/v1/companies/{companyId}/sales/create-sales-order", Values>(formId, "/api/v1/companies/{companyId}/sales/create-sales-order", (_, doc) => (doc ? `Pedido ${doc} creado en borrador.` : "Pedido creado en borrador."));
  const update = useCommand<"/api/v1/companies/{companyId}/sales/update-sales-order-draft", Values>(formId, "/api/v1/companies/{companyId}/sales/update-sales-order-draft", "Borrador del pedido guardado.");
  const [values, setValues] = useState<Values | null>(() => create.restored ?? update.restored ?? null);
  const fe = useFieldErrors<string>();
  const allowed = can("sales_order:create");
  const permission = scope("sales_order:create");

  const { data, error } = useLoad(
    allowed
      ? async () => {
          const [customers, plants, lists, order] = await Promise.all([
            allCustomers(companyId, { status: "ACTIVE" }),
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
  const previewSource = values ?? (data?.order ? { plantId: data.order.plantId, lines: data.order.lines.map((l) => ({ itemId: l.itemId, uom: l.uom, quantity: l.qtyOrdered })) } : null);
  const preview = useSalesPreview("order", previewSource?.plantId ?? "", previewSource?.lines ?? [], values?.partyId ?? data?.order?.header.partyId ?? "");

  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
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
          exemption: !order.exemptionPending ? "NONE" : order.proformaCollectsItbis ? "WITH_ITBIS" : "WITHOUT_ITBIS",
        }
      : { partyId: "", plantId: data.plants[0]?.plantId ?? "", deliveryTermCode: "PICKUP_AT_PLANT", siteAddress: "", requestedDate: "", customerPoRef: "", lines: [{ ...EMPTY_LINE }], exemption: "NONE" });
  const set = (change: Partial<Values>) => setValues({ ...current, ...change });
  const setLine = (index: number, change: Partial<Line>) => set({ lines: current.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) });

  if (order && order.header.status !== "DRAFT") {
    return <p className="muted">Solo se edita un pedido en borrador.</p>;
  }
  if (data.prices.length === 0) {
    return (
      <p className="muted">
        Todavía no hay una lista de precios aprobada, así que no se pueden tomar pedidos. El Controller la prepara en Maestros › Lista de precios y
        otra persona autorizada la aprueba.
      </p>
    );
  }

  const submit = async () => {
    const lines = current.lines.map((l) => ({ ...l, quantity: normalizeInput(l.quantity) }));
    const found: Record<string, string | false> = {
      partyId: !current.partyId && "Elija el cliente.",
      plantId: !current.plantId && "Elija la planta.",
      siteAddress: current.deliveryTermCode === "DELIVERED_OWN_TRANSPORT" && current.siteAddress.trim() === "" && "Una entrega en obra necesita la dirección de la obra.",
    };
    lines.forEach((l, i) => {
      found[`line-${i}-item`] = !l.itemId && "Elija el producto.";
      found[`line-${i}-quantity`] = !isPositiveDecimal(l.quantity, 6) && "Indique una cantidad mayor que cero (hasta 6 decimales).";
    });
    if (!fe.check(found)) {
      return;
    }
    const header = {
      plantId: current.plantId,
      deliveryTermCode: current.deliveryTermCode,
      siteAddress: current.siteAddress.trim() === "" ? null : current.siteAddress.trim(),
      requestedDate: current.requestedDate === "" ? null : current.requestedDate,
      customerPoRef: current.customerPoRef.trim() === "" ? null : current.customerPoRef.trim(),
      lines,
      exemptionPending: current.exemption !== "NONE",
      proformaCollectsItbis: current.exemption === "NONE" ? null : current.exemption === "WITH_ITBIS",
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
        <Field label="Cliente" required error={fe.errors.partyId}>
          <select aria-label="Cliente" value={current.partyId} disabled={order !== null} onChange={(e) => set({ partyId: e.target.value })}>
            <option value="">Seleccione…</option>
            {data.customers.map((c) => (
              <option key={c.partyId} value={c.partyId}>
                {c.legalName} ({c.rnc})
              </option>
            ))}
          </select>
        </Field>
        <Field label="Planta" required error={fe.errors.plantId}>
          <select aria-label="Planta del pedido" value={current.plantId} onChange={(e) => set({ plantId: e.target.value })}>
            {data.plants.map((p) => (
              <option key={p.plantId} value={p.plantId}>
                {plantName(p.plantId, p.code)}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Entrega" required>
          <select aria-label="Término de entrega" value={current.deliveryTermCode} onChange={(e) => set({ deliveryTermCode: e.target.value })}>
            {Object.entries(DELIVERY_TERMS).map(([code, label]) => (
              <option key={code} value={code}>
                {label}
              </option>
            ))}
          </select>
        </Field>
        {current.deliveryTermCode === "DELIVERED_OWN_TRANSPORT" ? (
          <Field label="Dirección de la obra" required error={fe.errors.siteAddress}>
            <input aria-label="Dirección de la obra" value={current.siteAddress} onChange={(e) => set({ siteAddress: e.target.value })} />
          </Field>
        ) : null}
        <Field label="Fecha solicitada (opcional)">
          <input type="date" value={current.requestedDate} onChange={(e) => set({ requestedDate: e.target.value })} />
        </Field>
        <Field label="Orden de compra del cliente (opcional)">
          <input value={current.customerPoRef} onChange={(e) => set({ customerPoRef: e.target.value })} />
        </Field>
        <Field
          label="Exención de ITBIS (CONFOTUR)"
          hint={current.exemption === "NONE" ? undefined : "Cada entrega generará su proforma, que se cobra antes de la factura fiscal. No cambia después de enviar el pedido a crédito."}
        >
          <select aria-label="Exención de ITBIS" value={current.exemption} onChange={(e) => set({ exemption: e.target.value as Exemption })}>
            <option value="NONE">No aplica: se factura al entregar</option>
            <option value="WITH_ITBIS">Exención en trámite: las proformas se cobran con ITBIS</option>
            <option value="WITHOUT_ITBIS">Exención en trámite: las proformas se cobran sin ITBIS</option>
          </select>
        </Field>
      </div>
      <LineTable>
        <thead>
          <tr>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Precio de lista (RD$)</th>
            <th className="num">Cantidad</th>
            <th className="num">Neto (RD$)</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {current.lines.map((line, index) => {
            const price = data.prices.find((p) => p.itemId === line.itemId && p.uom === line.uom);
            const priced = preview.preview?.lines[index];
            return (
              <tr key={index}>
                <td>
                  <select
                    aria-label={`Producto ${index + 1}`}
                    {...fieldAria(fe.errors[`line-${index}-item`], `order-line-${index}-item`, true)}
                    value={line.itemId && line.uom ? `${line.itemId}|${line.uom}` : ""}
                    onChange={(e) => {
                      const [itemId = "", uom = ""] = e.target.value.split("|");
                      setLine(index, { itemId, uom });
                    }}
                  >
                    <option value="">Seleccione…</option>
                    {data.prices.map((p) => (
                      <option key={`${p.itemId}|${p.uom}`} value={`${p.itemId}|${p.uom}`}>
                        {p.itemCode} — {p.itemDescription} ({p.uom})
                      </option>
                    ))}
                  </select>
                  <FieldMessage id={`order-line-${index}-item`} error={fe.errors[`line-${index}-item`]} />
                </td>
                <td>{line.uom}</td>
                <td className="num">
                  <Money value={price?.unitPrice} />
                </td>
                <td className="num">
                  <input
                    aria-label={`Cantidad ${index + 1}`}
                    {...fieldAria(fe.errors[`line-${index}-quantity`], `order-line-${index}-quantity`, true)}
                    inputMode="decimal"
                    value={line.quantity}
                    onChange={(e) => setLine(index, { quantity: e.target.value })}
                  />
                  <FieldMessage id={`order-line-${index}-quantity`} error={fe.errors[`line-${index}-quantity`]} />
                </td>
                <td className="num">
                  {priced && priced.itemId === line.itemId ? <Money value={priced.netAmount} testId={`preview-line-net:${index + 1}`} /> : <span className="muted">—</span>}
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
      </LineTable>
      <PreviewTotals {...preview} />
      {preview.preview && current.partyId ? (
        <CreditPreviewCard partyId={current.partyId} amount={preview.preview.netTotal} intro="Crédito del cliente con este pedido (se evalúa de nuevo al enviarlo a crédito):" />
      ) : null}
      <div className="actions form-actions">
        <button type="button" onClick={() => set({ lines: [...current.lines, { ...EMPTY_LINE }] })}>
          Agregar línea
        </button>
        <button type="button" className="primary" disabled={create.busy || update.busy} onClick={submit}>
          {order ? "Guardar borrador" : "Crear pedido"}
        </button>
      </div>
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
