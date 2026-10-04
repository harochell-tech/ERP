"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { checkExpenseLines, ExpenseLinesEditor, ExpenseTotals, useExpenseMasters, useExpensePreview } from "@/components/ExpenseLines";
import { ErrorBox, Field, Loading, NoPermission, useFieldErrors } from "@/components/ui";
import { normalizeInput } from "@/lib/decimal";
import { EMPTY_EXPENSE_LINE, type ExpenseLine } from "@/lib/expenses";
import { todayInDominicanRepublic } from "@/lib/labels";
import { allSuppliers } from "@/lib/paging";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// GAS1-07 (E-GAS-07-3): a purchase order of expenses — services and supplies that never go through the warehouse — approved as any
// order; its invoices bill it and it closes once billed in full (E-GAS-05-4).

interface Values {
  plantId: string;
  partyId: string;
  orderDate: string;
  lines: ExpenseLine[];
}

export default function NewExpenseOrder() {
  const { companyId, can, scope, plantName } = useSession();
  const router = useRouter();
  const create = useCommand<"/api/v1/companies/{companyId}/procurement/create-expense-purchase-order", Values>(
    "create-expense-po",
    "/api/v1/companies/{companyId}/procurement/create-expense-purchase-order",
  );
  const submit = useCommand("create-expense-po-submit", "/api/v1/companies/{companyId}/procurement/submit-purchase-order");
  const [values, setValues] = useState<Values>(() => create.restored ?? { plantId: "", partyId: "", orderDate: todayInDominicanRepublic(), lines: [{ ...EMPTY_EXPENSE_LINE }] });
  const fe = useFieldErrors<string>();
  const allowed = can("purchase_order:create");
  const permission = scope("purchase_order:create");
  const base = useLoad(
    allowed
      ? async () => {
          const [suppliers, plants] = await Promise.all([
            allSuppliers(companyId, { status: "ACTIVE" }),
            query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId } }),
          ]);
          return { suppliers: suppliers.items, plants: plants.items.filter((p) => permission.companyWide || permission.plants.includes(p.plantId)) };
        }
      : null,
    [companyId, allowed],
  );
  const masters = useExpenseMasters(companyId, values.orderDate, allowed);
  const preview = useExpensePreview(companyId, "/api/v1/companies/{companyId}/procurement/expense-purchase-orders/preview", values.orderDate, values.lines);

  if (!allowed) {
    return <NoPermission />;
  }
  if (base.data === null || masters.data === null) {
    return <Loading error={base.error ?? masters.error} />;
  }
  const set = (change: Partial<Values>) => setValues((v) => ({ ...v, ...change }));
  const plantId = values.plantId || base.data.plants[0]?.plantId || "";

  const save = async (andSubmit: boolean) => {
    if (!fe.check({ partyId: !values.partyId && "Elija el proveedor.", plantId: !plantId && "Elija la planta.", ...checkExpenseLines(values.lines) })) {
      return;
    }
    const response = await create.run(
      {
        plantId,
        partyId: values.partyId,
        orderDate: values.orderDate,
        lines: values.lines.map((l) => ({
          description: l.description.trim(),
          expenseCategoryId: l.expenseCategoryId,
          taxTypeId: l.taxTypeId,
          quantity: normalizeInput(l.quantity),
          unitPrice: normalizeInput(l.unitPrice),
        })),
      },
      { ...values, plantId },
      (_r, doc) => (doc ? `Orden de gastos ${doc} creada en borrador.` : "Orden de gastos creada en borrador."),
    );
    if (!response) {
      return;
    }
    if (andSubmit) {
      await submit.run({ plantId, purchaseOrderId: response.resultRef, expectedVersion: 1 }, undefined, "Orden de gastos enviada a aprobación.");
    }
    router.push(`/compras/orden/?id=${response.resultRef}`);
  };

  return (
    <>
      <p className="actions">
        <Link href="/compras/ordenes/nueva/">Orden de inventario</Link> · <strong>Orden de gastos</strong>
      </p>
      <h1>Nueva orden de compra de gastos</h1>
      <p className="muted">Servicios y suministros que no entran a almacén: no se reciben; se cierran cuando sus facturas completan lo pedido.</p>
      <div>
        <Field label="Planta" required error={fe.errors.plantId}>
          <select aria-label="Planta de la orden" value={plantId} onChange={(e) => set({ plantId: e.target.value })}>
            {base.data.plants.map((p) => (
              <option key={p.plantId} value={p.plantId}>
                {plantName(p.plantId, p.code)}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Proveedor" required error={fe.errors.partyId}>
          <select aria-label="Proveedor" value={values.partyId} onChange={(e) => set({ partyId: e.target.value })}>
            <option value="">—</option>
            {base.data.suppliers.map((s) => (
              <option key={s.supplierId} value={s.supplierId}>
                {s.legalName}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Fecha" required>
          <input type="date" aria-label="Fecha de la orden" value={values.orderDate} onChange={(e) => set({ orderDate: e.target.value })} />
        </Field>
      </div>
      <ExpenseLinesEditor lines={values.lines} onChange={(lines) => set({ lines })} masters={masters.data} errors={fe.errors} preview={preview.preview} />
      <ExpenseTotals preview={preview.preview} problem={preview.problem} />
      <div className="actions form-actions">
        <button type="button" disabled={create.busy || submit.busy} onClick={() => void save(false)}>
          Guardar borrador
        </button>
        <button type="button" className="primary" disabled={create.busy || submit.busy} onClick={() => void save(true)}>
          Guardar y enviar
        </button>
      </div>
      <ErrorBox error={create.error ?? submit.error} />
    </>
  );
}
