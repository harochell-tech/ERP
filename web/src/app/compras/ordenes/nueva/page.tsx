"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, Loading, Money, NoPermission, useFieldErrors } from "@/components/ui";
import { formatDecimal, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { describeError } from "@/lib/errors";
import { todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { uomOptions } from "@/lib/units";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { previewQuery } from "@/lib/ux4a";
import { itemLabel, previewKey, uomLabel } from "@/lib/ux4a-compras";

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

type Preview = Schemas["PurchaseOrderPreview"];
interface PreviewState {
  key: string | null;
  preview: Preview | null;
  error: unknown;
}

/**
 * UX4-02 (C-09, E-UX4-3): the order as the server would price it — net per line, net total and the purchase ITBIS in force on the
 * order date — asked 400 ms after the last change of a complete form. Nothing is written; an error is shown, never blocks.
 */
function usePreview(companyId: string, values: Values): PreviewState {
  const key = previewKey({ plantId: values.plantId, partyId: values.partyId, orderDate: values.orderDate }, values.lines);
  const [state, setState] = useState<PreviewState>({ key: null, preview: null, error: null });
  useEffect(() => {
    if (key === null) {
      return;
    }
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      const body = JSON.parse(key) as Schemas["PurchaseOrderPreviewRequest"];
      previewQuery("/api/v1/companies/{companyId}/procurement/purchase-orders/preview", companyId, body, controller.signal)
        .then((preview) => setState({ key, preview, error: null }))
        .catch((error: unknown) => {
          if (!controller.signal.aborted) {
            setState({ key, preview: null, error });
          }
        });
    }, 400);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [companyId, key]);
  return state.key === key ? state : { key, preview: null, error: null };
}

/** C-09: the server's totals of the draft, or why they are not there yet. */
function PreviewTotals({ state }: { state: PreviewState }) {
  if (state.key === null) {
    return (
      <p className="muted" data-testid="po-preview">
        Complete la planta, el proveedor, la fecha y cada línea (artículo, cantidad y precio) para ver el neto, el ITBIS estimado y el total.
      </p>
    );
  }
  if (state.error) {
    return (
      <p className="muted" data-testid="po-preview">
        No se pudo calcular la vista previa: {describeError(state.error).message}
      </p>
    );
  }
  if (state.preview === null) {
    return (
      <p className="muted" data-testid="po-preview">
        Calculando totales…
      </p>
    );
  }
  const p = state.preview;
  return (
    <>
      <dl className="facts" data-testid="po-preview">
        <dt>Neto</dt>
        <dd>
          <Money value={p.netTotal} currency testId="po-preview-net-total" />
        </dd>
        <dt>ITBIS estimado</dt>
        <dd data-testid="po-preview-itbis">
          {p.itbisTotal === null ? (
            <span className="muted">No disponible: {p.itbisUnavailableReason ?? "no hay regla fiscal vigente para la fecha de la orden"}</span>
          ) : (
            <Money value={p.itbisTotal} currency />
          )}
        </dd>
        <dt>Total</dt>
        <dd>{p.total === null ? "—" : <Money value={p.total} currency testId="po-preview-total" />}</dd>
      </dl>
      <p className="muted">Estimado con las reglas fiscales vigentes en la fecha de la orden; el ITBIS definitivo se determina con la factura del proveedor.</p>
    </>
  );
}

export default function NewPurchaseOrder() {
  const { companyId, can, scope, plantName } = useSession();
  const router = useRouter();
  const create = useCommand<"/api/v1/companies/{companyId}/procurement/create-purchase-order", Values>(
    "create-po",
    "/api/v1/companies/{companyId}/procurement/create-purchase-order",
    (_, doc) => (doc ? `Orden de compra ${doc} creada en borrador.` : "Orden de compra creada en borrador."),
  );
  // C-11: "Guardar y enviar a aprobación" creates the order and then submits it (two commands).
  const send = useCommand("create-po-submit", "/api/v1/companies/{companyId}/procurement/submit-purchase-order");
  const [values, setValues] = useState<Values>(
    () => create.restored ?? { plantId: "", partyId: "", orderDate: todayInDominicanRepublic(), lines: [{ ...EMPTY_LINE }] },
  );
  const fe = useFieldErrors();
  const preview = usePreview(companyId, values);
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

  const submit = async (andSend: boolean) => {
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
    const response = await create.run(
      { plantId: values.plantId, partyId: values.partyId, orderDate: values.orderDate, lines },
      values,
      andSend ? (_, doc) => (doc ? `Orden de compra ${doc} guardada.` : "Orden de compra guardada.") : undefined,
    );
    if (!response) {
      return;
    }
    if (andSend) {
      const result = response.result as { version?: number; poNo?: string } | null;
      const doc = result?.poNo ? `Orden de compra ${result.poNo}` : "Orden de compra";
      // If the submission fails the order stays a draft: its detail says "Aún no enviada a aprobación" and offers the button.
      await send.run(
        { plantId: values.plantId, purchaseOrderId: response.resultRef, expectedVersion: result?.version ?? 1 },
        undefined,
        `${doc} guardada y enviada a aprobación.`,
      );
    }
    router.push(`/compras/orden/?id=${response.resultRef}`);
  };

  const lineError = (index: number, field: string) => fe.errors[`line-${index}-${field}`];

  return (
    <>
      <h1>Nueva orden de compra</h1>
      <p className="muted">El número de la orden (OC-año-000000) lo asigna el sistema al guardarla.</p>
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
            <th className="num">Neto (RD$)</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {values.lines.map((line, index) => {
            const item = data.items.find((i) => i.itemId === line.itemId);
            const uoms = item ? uomOptions(item.baseUom, item.conversions) : [];
            const id = (field: string) => `po-line-${index}-${field}`;
            const net = preview.preview?.lines[index]?.netAmount;
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
                        {itemLabel(i.code, i.description)}
                      </option>
                    ))}
                  </select>
                  <FieldMessage id={id("itemId")} error={lineError(index, "itemId")} />
                </td>
                <td>
                  <select aria-label={`Unidad ${index + 1}`} {...fieldAria(lineError(index, "uom"), id("uom"), true)} value={line.uom} onChange={(e) => setLine(index, { uom: e.target.value })}>
                    {uoms.map((u) => (
                      <option key={u} value={u}>
                        {uomLabel(u)}
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
                <td className="num" data-testid={`po-preview-net-${index + 1}`}>
                  {net ? formatDecimal(net) : "—"}
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
      <PreviewTotals state={preview} />
      <div className="actions form-actions">
        <button type="button" onClick={() => setValues({ ...values, lines: [...values.lines, { ...EMPTY_LINE }] })}>
          Agregar línea
        </button>
        {can("purchase_order:submit") ? (
          <>
            <button type="button" disabled={create.busy || send.busy} onClick={() => submit(false)}>
              Guardar borrador
            </button>
            <button type="button" className="primary" disabled={create.busy || send.busy} onClick={() => submit(true)}>
              Guardar y enviar a aprobación
            </button>
          </>
        ) : (
          <button type="button" className="primary" disabled={create.busy} onClick={() => submit(false)}>
            Guardar borrador
          </button>
        )}
      </div>
      <ErrorBox error={create.error ?? send.error} />
    </>
  );
}
