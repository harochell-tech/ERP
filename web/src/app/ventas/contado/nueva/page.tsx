"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { MoneyText, PreviewTotals, useSalesPreview } from "@/components/SalesUx4";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, Money, NoPermission, useFieldErrors } from "@/components/ui";
import { BUYER_ID_KINDS, buyerIdError, normalizeBuyerId } from "@/lib/cashSales";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { DELIVERY_TERMS } from "@/lib/labels";
import { useZones, ZoneField } from "@/components/ZoneField";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// CF1-05 (E-CF1-05-2, E-CF1-05-6): step 1 of a cash sale — the products at the price list in force and, optional below the fiscal
// rule's amount, who buys. Prices, ITBIS and totals are the server's (POST preview); the screen never multiplies.

interface Line {
  itemId: string;
  uom: string;
  quantity: string;
}

interface Values {
  plantId: string;
  deliveryTermCode: string;
  siteAddress: string;
  requestedDate: string;
  lines: Line[];
  buyerName: string;
  buyerPhone: string;
  buyerIdKind: string;
  buyerId: string;
  deliveryZoneId?: string;
}

const EMPTY_LINE: Line = { itemId: "", uom: "", quantity: "" };

function CashSaleForm() {
  const { companyId, can, scope, plantName } = useSession();
  const router = useRouter();
  const editId = useSearchParams().get("id");
  const formId = editId ? `edit-cash-sale:${editId}` : "create-cash-sale";
  const create = useCommand<"/api/v1/companies/{companyId}/sales/create-cash-sale", Values>(formId, "/api/v1/companies/{companyId}/sales/create-cash-sale", (_, doc) =>
    doc ? `Venta de contado ${doc} creada en borrador.` : "Venta de contado creada en borrador.",
  );
  const update = useCommand<"/api/v1/companies/{companyId}/sales/update-cash-sale-draft", Values>(formId, "/api/v1/companies/{companyId}/sales/update-cash-sale-draft", "Borrador de la venta guardado.");
  const [values, setValues] = useState<Values | null>(() => create.restored ?? update.restored ?? null);
  const fe = useFieldErrors<string>();
  const allowed = can("cash_sale:create");
  const permission = scope("cash_sale:create");
  const { zones } = useZones();

  const { data, error } = useLoad(
    allowed
      ? async () => {
          const [plants, lists, setup, order] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/plants", { path: { companyId } }),
            query("/api/v1/companies/{companyId}/sales/price-lists", { path: { companyId } }),
            query("/api/v1/companies/{companyId}/sales/cash-sale-setup", { path: { companyId } }),
            editId ? query("/api/v1/companies/{companyId}/sales/orders/{salesOrderId}", { path: { companyId, salesOrderId: editId } }) : Promise.resolve(null),
          ]);
          // PRS-05: the products offered are GENERAL's (every product is there); the price of each line is the server's preview.
          const active = lists.items.find((l) => l.status === "ACTIVE" && l.priceListCode === "GENERAL") ?? lists.items.find((l) => l.status === "ACTIVE");
          const prices = active
            ? (await query("/api/v1/companies/{companyId}/sales/price-lists/{priceListVersionId}", { path: { companyId, priceListVersionId: active.priceListVersionId } })).lines
            : [];
          return { plants: plants.items.filter((p) => permission.companyWide || permission.plants.includes(p.plantId)), prices, setup, order };
        }
      : null,
    [companyId, allowed, editId],
  );
  const previewSource = values ?? (data?.order ? { plantId: data.order.plantId, lines: data.order.lines.map((l) => ({ itemId: l.itemId, uom: l.uom, quantity: l.qtyOrdered })) } : null);
  // E-PRS-04-9: a cash sale's freight comes from GENERAL.
  const ownTruck = (values?.deliveryTermCode ?? data?.order?.header.deliveryTermCode) === "DELIVERED_OWN_TRANSPORT";
  const preview = useSalesPreview("cash", previewSource?.plantId ?? "", previewSource?.lines ?? [], "", ownTruck ? (values?.deliveryZoneId ?? data?.order?.deliveryZoneId ?? "") : "");

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
          plantId: order.plantId,
          deliveryTermCode: order.header.deliveryTermCode,
          siteAddress: order.siteAddress ?? "",
          requestedDate: order.requestedDate ?? "",
          deliveryZoneId: order.deliveryZoneId ?? "",
          lines: order.lines.map((l) => ({ itemId: l.itemId, uom: l.uom, quantity: l.qtyOrdered })),
          buyerName: order.cashSale?.buyerName ?? "",
          buyerPhone: order.cashSale?.buyerPhone ?? "",
          buyerIdKind: order.cashSale?.buyerIdKind ?? "",
          buyerId: order.cashSale?.buyerId ?? "",
        }
      : {
          plantId: data.plants[0]?.plantId ?? "",
          deliveryTermCode: "PICKUP_AT_PLANT",
          siteAddress: "",
          requestedDate: "",
          lines: [{ ...EMPTY_LINE }],
          buyerName: "",
          buyerPhone: "",
          buyerIdKind: "",
          buyerId: "",
        });
  const set = (change: Partial<Values>) => setValues({ ...current, ...change });
  const setLine = (index: number, change: Partial<Line>) => set({ lines: current.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) });

  if (order && (!order.header.cashSale || order.header.status !== "DRAFT")) {
    return <p className="muted">Solo se edita una venta de contado en borrador.</p>;
  }
  if (data.prices.length === 0) {
    return (
      <p className="muted">
        Todavía no hay una lista de precios aprobada, así que no se puede vender. El Controller la prepara en Maestros › Lista de precios y otra
        persona autorizada la aprueba.
      </p>
    );
  }

  const submit = async () => {
    const lines = current.lines.map((l) => ({ ...l, quantity: normalizeInput(l.quantity) }));
    const found: Record<string, string | false> = {
      plantId: !current.plantId && "Elija la planta.",
      siteAddress: current.deliveryTermCode === "DELIVERED_OWN_TRANSPORT" && current.siteAddress.trim() === "" && "Una entrega en obra necesita la dirección de la obra.",
      deliveryZoneId: current.deliveryTermCode === "DELIVERED_OWN_TRANSPORT" && zones.length > 0 && !current.deliveryZoneId && "Elija la zona de entrega.",
      buyerId: buyerIdError(current.buyerIdKind, current.buyerId) ?? false,
    };
    lines.forEach((l, i) => {
      found[`line-${i}-item`] = !l.itemId && "Elija el producto.";
      found[`line-${i}-quantity`] = !isPositiveDecimal(l.quantity, 6) && "Indique una cantidad mayor que cero (hasta 6 decimales).";
    });
    if (!fe.check(found)) {
      return;
    }
    const optional = (v: string) => (v.trim() === "" ? null : v.trim());
    const body = {
      plantId: current.plantId,
      deliveryTermCode: current.deliveryTermCode,
      siteAddress: optional(current.siteAddress),
      requestedDate: current.requestedDate === "" ? null : current.requestedDate,
      lines,
      deliveryZoneId: current.deliveryTermCode === "DELIVERED_OWN_TRANSPORT" && current.deliveryZoneId ? current.deliveryZoneId : null,
      buyerName: optional(current.buyerName),
      buyerPhone: optional(current.buyerPhone),
      buyerIdKind: current.buyerIdKind === "" ? null : current.buyerIdKind,
      buyerId: current.buyerIdKind === "" ? null : normalizeBuyerId(current.buyerIdKind, current.buyerId),
    };
    const response = order ? await update.run({ salesOrderId: order.header.salesOrderId, expectedVersion: order.header.version, ...body }, current) : await create.run(body, current);
    if (response) {
      router.push(`/ventas/venta-contado/?id=${order ? order.header.salesOrderId : response.resultRef}`);
    }
  };

  return (
    <>
      <p>
        <Link href="/ventas/contado/">← Ventas de contado</Link>
      </p>
      <h1>{order ? `Editar venta de contado ${order.header.orderNo}` : "Nueva venta de contado"}</h1>
      {data.setup.buyerIdRequiredFrom === null ? (
        <p className="notice" role="alert" data-testid="threshold-missing">
          Todavía no se puede vender a consumidor final: falta activar la regla fiscal con el monto desde el cual hay que identificar al comprador
          (Fiscal › Reglas fiscales). Puede guardar el borrador, pero no enviarlo a pago.
        </p>
      ) : (
        <p className="notice" data-testid="threshold-notice">
          Desde <MoneyText value={data.setup.buyerIdRequiredFrom} testId="threshold-amount" /> (total con ITBIS) la identificación del comprador es obligatoria.
        </p>
      )}
      <h2>1. Productos</h2>
      <div>
        <Field label="Planta" required error={fe.errors.plantId}>
          <select aria-label="Planta de la venta" value={current.plantId} onChange={(e) => set({ plantId: e.target.value })}>
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
        {current.deliveryTermCode === "DELIVERED_OWN_TRANSPORT" ? (
          <ZoneField value={current.deliveryZoneId ?? ""} onChange={(deliveryZoneId) => set({ deliveryZoneId })} error={fe.errors.deliveryZoneId} zones={zones} />
        ) : null}
        <Field label="Fecha solicitada (opcional)">
          <input type="date" value={current.requestedDate} onChange={(e) => set({ requestedDate: e.target.value })} />
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
                    {...fieldAria(fe.errors[`line-${index}-item`], `cash-line-${index}-item`, true)}
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
                  <FieldMessage id={`cash-line-${index}-item`} error={fe.errors[`line-${index}-item`]} />
                </td>
                <td>{line.uom}</td>
                <td className="num">
                  <Money value={priced && priced.itemId === line.itemId ? priced.listPrice : price?.unitPrice} />
                </td>
                <td className="num">
                  <input
                    aria-label={`Cantidad ${index + 1}`}
                    {...fieldAria(fe.errors[`line-${index}-quantity`], `cash-line-${index}-quantity`, true)}
                    inputMode="decimal"
                    value={line.quantity}
                    onChange={(e) => setLine(index, { quantity: e.target.value })}
                  />
                  <FieldMessage id={`cash-line-${index}-quantity`} error={fe.errors[`line-${index}-quantity`]} />
                </td>
                <td className="num">
                  {priced && priced.itemId === line.itemId ? <Money value={priced.netAmount} testId={`preview-line-net:${index + 1}`} /> : <span className="muted">—</span>}
                  {priced && priced.itemId === line.itemId && priced.freightAmount ? (
                    <div className="muted" data-testid={`preview-line-freight:${index + 1}`}>
                      + flete <Money value={priced.freightAmount} />
                    </div>
                  ) : null}
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
      <div className="actions">
        <button type="button" onClick={() => set({ lines: [...current.lines, { ...EMPTY_LINE }] })}>
          Agregar línea
        </button>
      </div>
      <PreviewTotals {...preview} note="El total a pagar queda fijo, con el ITBIS del día, al enviar la venta a pago." />
      <h2>Comprador</h2>
      <div>
        <Field label="Nombre (opcional)">
          <input aria-label="Nombre del comprador" maxLength={150} value={current.buyerName} onChange={(e) => set({ buyerName: e.target.value })} />
        </Field>
        <Field label="Teléfono (opcional)">
          <input aria-label="Teléfono del comprador" maxLength={30} inputMode="tel" value={current.buyerPhone} onChange={(e) => set({ buyerPhone: e.target.value })} />
        </Field>
        <Field label="Tipo de identificación">
          <select aria-label="Tipo de identificación" value={current.buyerIdKind} onChange={(e) => set({ buyerIdKind: e.target.value, buyerId: e.target.value === "" ? "" : current.buyerId })}>
            <option value="">Sin identificación</option>
            {Object.entries(BUYER_ID_KINDS).map(([code, label]) => (
              <option key={code} value={code}>
                {label}
              </option>
            ))}
          </select>
        </Field>
        {current.buyerIdKind !== "" ? (
          <Field label="Número de identificación" required error={fe.errors.buyerId}>
            <input aria-label="Número de identificación" value={current.buyerId} onChange={(e) => set({ buyerId: e.target.value })} />
          </Field>
        ) : null}
      </div>
      <div className="actions form-actions">
        <button type="button" className="primary" disabled={create.busy || update.busy} onClick={submit}>
          {order ? "Guardar borrador" : "Crear venta"}
        </button>
      </div>
      <ErrorBox error={create.error ?? update.error} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <CashSaleForm />
    </Suspense>
  );
}
