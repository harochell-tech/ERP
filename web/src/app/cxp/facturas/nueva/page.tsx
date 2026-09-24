"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
import { formatDecimal, formatQuantity, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { todayInDominicanRepublic } from "@/lib/labels";
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
  const register = useCommand<"/api/v1/companies/{companyId}/procurement/register-supplier-invoice", Values>("register-si", "/api/v1/companies/{companyId}/procurement/register-supplier-invoice");
  const [values, setValues] = useState<Values>(
    () => register.restored ?? { partyId: "", fiscalNumber: "", docDate: todayInDominicanRepublic(), dueDate: "", purchaseOrderId: "", lines: {} },
  );
  const [invalid, setInvalid] = useState<string | null>(null);
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
    if (!values.partyId || !values.fiscalNumber.trim() || !values.docDate || !values.dueDate) {
      setInvalid("Indique proveedor, NCF, fecha y vencimiento.");
      return;
    }
    if (lines.length === 0 || lines.some((l) => !isPositiveDecimal(l.quantity, 6) || !isPositiveDecimal(l.unitPrice, 6))) {
      setInvalid("Indique al menos una línea con cantidad y precio mayores que cero (hasta 6 decimales).");
      return;
    }
    setInvalid(null);
    const response = await register.run(
      { partyId: values.partyId, supplierFiscalNumber: values.fiscalNumber.trim(), docDate: values.docDate, dueDate: values.dueDate, lines },
      values,
    );
    if (response) {
      router.push(`/cxp/factura/?id=${response.resultRef}`);
    }
  };

  return (
    <>
      <h1>Registrar factura de proveedor</h1>
      <div>
        <Field label="Proveedor">
          <select aria-label="Proveedor" value={values.partyId} onChange={(e) => setValues({ ...values, partyId: e.target.value, purchaseOrderId: "", lines: {} })}>
            <option value="">—</option>
            {suppliers.data.items.map((s) => (
              <option key={s.supplierId} value={s.supplierId}>
                {s.legalName}
              </option>
            ))}
          </select>
        </Field>
        <Field label="NCF">
          <input aria-label="NCF" placeholder="B0100000001" value={values.fiscalNumber} onChange={(e) => setValues({ ...values, fiscalNumber: e.target.value })} />
        </Field>
        <Field label="Fecha">
          <input type="date" aria-label="Fecha de la factura" value={values.docDate} onChange={(e) => setValues({ ...values, docDate: e.target.value })} />
        </Field>
        <Field label="Vencimiento">
          <input type="date" aria-label="Vencimiento" value={values.dueDate} onChange={(e) => setValues({ ...values, dueDate: e.target.value })} />
        </Field>
        <Field label="Orden de compra">
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
        <table>
          <thead>
            <tr>
              <th>Artículo</th>
              <th className="num">Recibido</th>
              <th className="num">Ya facturado</th>
              <th className="num">Precio de la orden</th>
              <th className="num">Cantidad facturada</th>
              <th className="num">Precio facturado</th>
            </tr>
          </thead>
          <tbody>
            {order.data.lines.map((l) => (
              <tr key={l.poLineId}>
                <td>{l.itemCode}</td>
                <td className="num">{formatQuantity(l.qtyReceived)}</td>
                <td className="num">{formatQuantity(l.qtyInvoiced)}</td>
                <td className="num">{formatDecimal(l.unitPrice)}</td>
                <td className="num">
                  <input
                    aria-label={`Cantidad facturada ${l.itemCode}`}
                    inputMode="decimal"
                    value={values.lines[l.poLineId]?.quantity ?? ""}
                    onChange={(e) => setLine(l.poLineId, { quantity: e.target.value })}
                  />
                </td>
                <td className="num">
                  <input
                    aria-label={`Precio facturado ${l.itemCode}`}
                    inputMode="decimal"
                    placeholder={l.unitPrice}
                    value={values.lines[l.poLineId]?.unitPrice ?? ""}
                    onChange={(e) => setLine(l.poLineId, { unitPrice: e.target.value })}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
      <div className="actions">
        <button type="button" disabled={register.busy} onClick={submit}>
          Registrar factura
        </button>
      </div>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={register.error ?? orders.error ?? order.error} />
    </>
  );
}
