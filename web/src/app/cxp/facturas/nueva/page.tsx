"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, Loading, NoPermission, useFieldErrors } from "@/components/ui";
import { formatDecimal, formatQuantity, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { addDays, formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

interface Values {
  partyId: string;
  fiscalNumber: string;
  docDate: string;
  dueDate: string;
  purchaseOrderId: string;
  lines: Record<string, { quantity: string; unitPrice: string }>;
}

/** T-06: registers a DRAFT invoice against the lines of one received purchase order. Nothing is computed on screen (E-PR18b-4). */
export default function NewInvoice() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const register = useCommand<"/api/v1/companies/{companyId}/procurement/register-supplier-invoice", Values>(
    "register-si",
    "/api/v1/companies/{companyId}/procurement/register-supplier-invoice",
  );
  const [values, setValues] = useState<Values>(
    () => register.restored ?? { partyId: "", fiscalNumber: "", docDate: todayInDominicanRepublic(), dueDate: "", purchaseOrderId: "", lines: {} },
  );
  const fe = useFieldErrors();
  const allowed = can("supplier_invoice:register");

  const suppliers = useLoad(
    allowed ? () => query("/api/v1/companies/{companyId}/master-data/suppliers", { path: { companyId }, query: { status: "ACTIVE", limit: 200 } }) : null,
    [companyId, allowed],
  );
  const orders = useLoad(
    allowed && values.partyId
      ? async () => {
          const list = await query("/api/v1/companies/{companyId}/procurement/purchase-orders", { path: { companyId }, query: { supplierId: values.partyId, limit: 200 } });
          return list.items.filter((po) => po.status === "RECEIVED" || po.status === "PARTIALLY_RECEIVED" || po.status === "CLOSED");
        }
      : null,
    [companyId, values.partyId],
  );
  const order = useLoad(
    values.purchaseOrderId
      ? () => query("/api/v1/companies/{companyId}/procurement/purchase-orders/{purchaseOrderId}", { path: { companyId, purchaseOrderId: values.purchaseOrderId } })
      : null,
    [companyId, values.purchaseOrderId],
  );

  if (!allowed) {
    return <NoPermission />;
  }
  if (suppliers.data === null) {
    return <Loading error={suppliers.error} />;
  }
  // E-VS3-02-10: the supplier's payment term proposes the due date; the user keeps the last word.
  const termsDays = suppliers.data.items.find((s) => s.supplierId === values.partyId)?.paymentTermsDays ?? null;

  const setLine = (poLineId: string, change: Partial<{ quantity: string; unitPrice: string }>) =>
    setValues((v) => ({ ...v, lines: { ...v.lines, [poLineId]: { quantity: "", unitPrice: "", ...v.lines[poLineId], ...change } } }));

  const submit = async () => {
    const po = order.data;
    const lines = (po?.lines ?? [])
      .map((l) => ({ l, input: values.lines[l.poLineId] }))
      .filter((x) => x.input && normalizeInput(x.input.quantity) !== "")
      .map((x) => ({
        purchaseOrderLineId: x.l.poLineId,
        quantity: normalizeInput(x.input!.quantity),
        unitPrice: normalizeInput(x.input!.unitPrice || x.l.unitPrice),
      }));
    const found: Record<string, string | false> = {
      partyId: !values.partyId && "Elija el proveedor.",
      fiscalNumber: !values.fiscalNumber.trim() && "Indique el NCF de la factura.",
      docDate: !values.docDate && "Indique la fecha de la factura.",
      dueDate: !values.dueDate && "Indique el vencimiento.",
      purchaseOrderId: !values.purchaseOrderId && "Elija la orden de compra.",
      lines: !!values.purchaseOrderId && lines.length === 0 && "Indique la cantidad facturada de al menos una línea.",
    };
    for (const l of lines) {
      found[`line-${l.purchaseOrderLineId}-quantity`] = !isPositiveDecimal(l.quantity, 6) && "Cantidad mayor que cero (hasta 6 decimales).";
      found[`line-${l.purchaseOrderLineId}-unitPrice`] = !isPositiveDecimal(l.unitPrice, 6) && "Precio mayor que cero (hasta 6 decimales).";
    }
    if (!fe.check(found)) {
      return;
    }
    const fiscalNumber = values.fiscalNumber.trim();
    const response = await register.run(
      { partyId: values.partyId, supplierFiscalNumber: fiscalNumber, docDate: values.docDate, dueDate: values.dueDate, lines },
      values,
      `Factura de proveedor ${fiscalNumber} registrada en borrador.`,
    );
    if (response) {
      router.push(`/cxp/factura/?id=${response.resultRef}`);
    }
  };

  return (
    <>
      <h1>Registrar factura de proveedor</h1>
      <div>
        <Field label="Proveedor" required error={fe.errors.partyId}>
          <select aria-label="Proveedor" value={values.partyId} onChange={(e) => setValues({ ...values, partyId: e.target.value, purchaseOrderId: "", lines: {} })}>
            <option value="">—</option>
            {suppliers.data.items.map((s) => (
              <option key={s.supplierId} value={s.supplierId}>
                {s.legalName}
              </option>
            ))}
          </select>
        </Field>
        <Field label="NCF" required error={fe.errors.fiscalNumber}>
          <input aria-label="NCF" placeholder="B0100000001" value={values.fiscalNumber} onChange={(e) => setValues({ ...values, fiscalNumber: e.target.value })} />
        </Field>
        <Field label="Fecha" required error={fe.errors.docDate}>
          <input type="date" aria-label="Fecha de la factura" value={values.docDate} onChange={(e) => setValues({ ...values, docDate: e.target.value })} />
        </Field>
        <Field label="Vencimiento" required error={fe.errors.dueDate}>
          <input type="date" aria-label="Vencimiento" value={values.dueDate} onChange={(e) => setValues({ ...values, dueDate: e.target.value })} />
        </Field>
        {termsDays !== null && values.docDate ? (
          <span className="muted" data-testid="due-date-proposal">
            Plazo del proveedor: {termsDays} días →{" "}
            <button type="button" className="link" onClick={() => setValues({ ...values, dueDate: addDays(values.docDate, termsDays) })}>
              usar {formatDate(addDays(values.docDate, termsDays))}
            </button>
          </span>
        ) : null}
        <Field label="Orden de compra" required error={fe.errors.purchaseOrderId}>
          <select aria-label="Orden de compra" value={values.purchaseOrderId} onChange={(e) => setValues({ ...values, purchaseOrderId: e.target.value, lines: {} })}>
            <option value="">—</option>
            {(orders.data ?? []).map((po) => (
              <option key={po.purchaseOrderId} value={po.purchaseOrderId}>
                {po.poNo}
              </option>
            ))}
          </select>
        </Field>
      </div>
      {order.data ? (
        <LineTable>
          <thead>
            <tr>
              <th>Artículo</th>
              <th className="num">Recibido</th>
              <th className="num">Ya facturado</th>
              <th className="num">Precio de la orden (RD$)</th>
              <th className="num">Cantidad facturada</th>
              <th className="num">Precio facturado (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {order.data.lines.map((l) => {
              const qtyKey = `line-${l.poLineId}-quantity`;
              const priceKey = `line-${l.poLineId}-unitPrice`;
              return (
                <tr key={l.poLineId}>
                  <td>{l.itemCode}</td>
                  <td className="num">{formatQuantity(l.qtyReceived)}</td>
                  <td className="num">{formatQuantity(l.qtyInvoiced)}</td>
                  <td className="num">{formatDecimal(l.unitPrice)}</td>
                  <td className="num">
                    <input
                      aria-label={`Cantidad facturada ${l.itemCode}`}
                      {...fieldAria(fe.errors[qtyKey], `si-${qtyKey}`)}
                      inputMode="decimal"
                      value={values.lines[l.poLineId]?.quantity ?? ""}
                      onChange={(e) => setLine(l.poLineId, { quantity: e.target.value })}
                    />
                    <FieldMessage id={`si-${qtyKey}`} error={fe.errors[qtyKey]} />
                  </td>
                  <td className="num">
                    <input
                      aria-label={`Precio facturado ${l.itemCode}`}
                      {...fieldAria(fe.errors[priceKey], `si-${priceKey}`)}
                      inputMode="decimal"
                      placeholder={formatDecimal(l.unitPrice)}
                      value={values.lines[l.poLineId]?.unitPrice ?? ""}
                      onChange={(e) => setLine(l.poLineId, { unitPrice: e.target.value })}
                    />
                    <FieldMessage id={`si-${priceKey}`} error={fe.errors[priceKey]} />
                  </td>
                </tr>
              );
            })}
          </tbody>
        </LineTable>
      ) : null}
      {fe.errors.lines ? <div className="error">{fe.errors.lines}</div> : null}
      <div className="actions form-actions">
        <button type="button" className="primary" disabled={register.busy} onClick={submit}>
          Registrar factura
        </button>
      </div>
      <ErrorBox error={register.error ?? orders.error ?? order.error} />
    </>
  );
}
