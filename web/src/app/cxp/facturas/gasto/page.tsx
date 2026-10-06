"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { checkExpenseLines, ExpenseLinesEditor, ExpenseTotals, useExpenseMasters, useExpensePreview } from "@/components/ExpenseLines";
import { ErrorBox, Field, Loading, NoPermission, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput, shiftDecimalPoint } from "@/lib/decimal";
import { EMPTY_EXPENSE_LINE, type ExpenseLine } from "@/lib/expenses";
import { addDays, todayInDominicanRepublic } from "@/lib/labels";
import { allSuppliers } from "@/lib/paging";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// GAS1-07 (E-GAS-07-2): an expense invoice — electricity, telephone, repairs — with or without an expense order. Each line says
// what was bought, its category and its tax type; the server prices it while it is typed. «Registrar y cotejar» registers it and
// matches it at once: below the approval amount (or within the order) it is ready to post, otherwise the Controller approves it.
// USD1-07a (E-USD1-07-3): a foreign supplier's invoice is in USD — its own number, no tax type, the day's rate and pesos from the server.

interface Values {
  partyId: string;
  plantId: string;
  fiscalNumber: string;
  docDate: string;
  dueDate: string;
  purchaseOrderId: string;
  printedTotal: string;
  lines: ExpenseLine[];
}

export default function NewExpenseInvoice() {
  const { companyId, can, plantName } = useSession();
  const router = useRouter();
  const register = useCommand<"/api/v1/companies/{companyId}/procurement/register-expense-invoice", Values>("register-expense-invoice", "/api/v1/companies/{companyId}/procurement/register-expense-invoice");
  const match = useCommand("register-expense-invoice-match", "/api/v1/companies/{companyId}/procurement/match-supplier-invoice");
  const [values, setValues] = useState<Values>(
    () =>
      register.restored ?? {
        partyId: "",
        plantId: "",
        fiscalNumber: "",
        docDate: todayInDominicanRepublic(),
        dueDate: "",
        purchaseOrderId: "",
        printedTotal: "",
        lines: [{ ...EMPTY_EXPENSE_LINE }],
      },
  );
  const fe = useFieldErrors<string>();
  const allowed = can("supplier_invoice:register");
  const base = useLoad(
    allowed
      ? async () => {
          const [suppliers, plants] = await Promise.all([
            allSuppliers(companyId, { status: "ACTIVE" }),
            query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId } }),
          ]);
          return { suppliers: suppliers.items, plants: plants.items };
        }
      : null,
    [companyId, allowed],
  );
  const masters = useExpenseMasters(companyId, values.docDate, allowed);
  const orders = useLoad(
    allowed && values.partyId
      ? async () =>
          (await query("/api/v1/companies/{companyId}/procurement/purchase-orders", { path: { companyId }, query: { supplierId: values.partyId, status: "APPROVED", limit: 200 } })).items.filter(
            (o) => o.docClass === "EXPENSE",
          )
      : null,
    [companyId, values.partyId],
  );
  const foreign = base.data?.suppliers.find((s) => s.supplierId === values.partyId)?.partyKind === "FOREIGN";
  const preview = useExpensePreview(companyId, "/api/v1/companies/{companyId}/procurement/expense-invoices/preview", values.docDate, values.lines, foreign ? "USD" : "DOP");

  if (!allowed) {
    return <NoPermission />;
  }
  if (base.data === null || masters.data === null) {
    return <Loading error={base.error ?? masters.error} />;
  }
  const set = (change: Partial<Values>) => setValues((v) => ({ ...v, ...change }));
  const termsDays = base.data.suppliers.find((s) => s.supplierId === values.partyId)?.paymentTermsDays ?? null;

  const chooseOrder = async (purchaseOrderId: string) => {
    if (!purchaseOrderId) {
      set({ purchaseOrderId: "", lines: [{ ...EMPTY_EXPENSE_LINE }] });
      return;
    }
    // E-GAS-05-2: the order's lines, each with what it still has to bill; category and tax type are the order's.
    const order = await query("/api/v1/companies/{companyId}/procurement/purchase-orders/{purchaseOrderId}", { path: { companyId, purchaseOrderId } });
    set({
      purchaseOrderId,
      plantId: order.plantId,
      lines: order.lines.map((l) => ({
        description: l.description ?? "",
        expenseCategoryId: l.expenseCategoryId ?? "",
        taxTypeId: l.taxTypeId ?? "",
        quantity: shiftDecimalPoint(l.openQuantity, 0) ?? l.openQuantity, // "10.000000" reads "10"
        unitPrice: shiftDecimalPoint(l.unitPrice, 0) ?? l.unitPrice,
        purchaseOrderLineId: l.poLineId,
      })),
    });
  };

  const submit = async () => {
    const printedTotal = normalizeInput(values.printedTotal);
    const found: Record<string, string | false> = {
      partyId: !values.partyId && "Elija el proveedor.",
      plantId: !values.plantId && "Elija la planta.",
      fiscalNumber: !values.fiscalNumber.trim() && (foreign ? "Indique el número de la factura del proveedor." : "Indique el NCF de la factura."),
      docDate: !values.docDate && "Indique la fecha de la factura.",
      dueDate: !values.dueDate && "Indique el vencimiento.",
      printedTotal: printedTotal !== "" && !isPositiveDecimal(printedTotal, 2) && "El total impreso debe ser mayor que cero, con hasta 2 decimales.",
      ...checkExpenseLines(values.lines, foreign),
    };
    if (!fe.check(found)) {
      return;
    }
    const fiscalNumber = values.fiscalNumber.trim();
    const response = await register.run(
      {
        partyId: values.partyId,
        supplierFiscalNumber: fiscalNumber,
        docDate: values.docDate,
        dueDate: values.dueDate,
        plantId: values.plantId,
        lines: values.lines.map((l) => ({
          description: l.description.trim(),
          expenseCategoryId: l.expenseCategoryId,
          taxTypeId: foreign ? null : l.taxTypeId,
          quantity: normalizeInput(l.quantity),
          unitPrice: normalizeInput(l.unitPrice),
          purchaseOrderLineId: l.purchaseOrderLineId ?? null,
        })),
        printedTotal: printedTotal || null,
        purchaseOrderId: values.purchaseOrderId || null,
      },
      values,
      `Factura de gastos ${fiscalNumber} registrada.`,
    );
    if (!response) {
      return;
    }
    await match.run({ supplierInvoiceId: response.resultRef, expectedVersion: 1 }, undefined, `Factura de gastos ${fiscalNumber} cotejada.`);
    router.push(`/cxp/factura/?id=${response.resultRef}`);
  };

  return (
    <>
      <p className="actions">
        <Link href="/cxp/facturas/nueva/">Factura de inventario (con orden y recepción)</Link> · <strong>Factura de gastos</strong>
      </p>
      <h1>Registrar factura de gastos</h1>
      <p className="muted">
        Para gastos y servicios que no entran a almacén. Sin orden de compra, desde el monto de la política la aprueba el Controller antes de contabilizarse.
      </p>
      <div>
        <Field label="Proveedor" required error={fe.errors.partyId}>
          <select aria-label="Proveedor" value={values.partyId} onChange={(e) => set({ partyId: e.target.value, purchaseOrderId: "", lines: [{ ...EMPTY_EXPENSE_LINE }] })}>
            <option value="">—</option>
            {base.data.suppliers.map((s) => (
              <option key={s.supplierId} value={s.supplierId}>
                {s.legalName}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Orden de compra de gastos (opcional)">
          <select aria-label="Orden de compra de gastos" value={values.purchaseOrderId} onChange={(e) => void chooseOrder(e.target.value)}>
            <option value="">Sin orden</option>
            {(orders.data ?? []).map((o) => (
              <option key={o.purchaseOrderId} value={o.purchaseOrderId}>
                {o.poNo}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Planta" required error={fe.errors.plantId}>
          <select aria-label="Planta" value={values.plantId} disabled={values.purchaseOrderId !== ""} onChange={(e) => set({ plantId: e.target.value })}>
            <option value="">—</option>
            {base.data.plants.map((p) => (
              <option key={p.plantId} value={p.plantId}>
                {plantName(p.plantId, p.code)}
              </option>
            ))}
          </select>
        </Field>
        {foreign ? (
          <Field label="Número de la factura del proveedor" required error={fe.errors.fiscalNumber}>
            <input aria-label="Número de la factura del proveedor" maxLength={40} value={values.fiscalNumber} onChange={(e) => set({ fiscalNumber: e.target.value })} />
          </Field>
        ) : (
          <Field label="NCF" required error={fe.errors.fiscalNumber}>
            <input aria-label="NCF" value={values.fiscalNumber} onChange={(e) => set({ fiscalNumber: e.target.value.toUpperCase() })} />
          </Field>
        )}
        <Field label="Fecha de la factura" required error={fe.errors.docDate}>
          <input
            type="date"
            aria-label="Fecha de la factura"
            value={values.docDate}
            onChange={(e) => set({ docDate: e.target.value, dueDate: termsDays !== null && e.target.value ? addDays(e.target.value, termsDays) : values.dueDate })}
          />
        </Field>
        <Field label="Vence" required error={fe.errors.dueDate}>
          <input type="date" aria-label="Vence" value={values.dueDate} onChange={(e) => set({ dueDate: e.target.value })} />
        </Field>
        <Field label={foreign ? "Total según la factura en US$ (opcional)" : "Total según la factura (opcional)"} error={fe.errors.printedTotal}>
          <input aria-label="Total según la factura" inputMode="decimal" value={values.printedTotal} onChange={(e) => set({ printedTotal: e.target.value })} />
        </Field>
      </div>
      {foreign ? (
        <p className="muted" data-testid="foreign-invoice-note">
          Proveedor del exterior: la factura es en dólares, sin NCF, ITBIS ni retenciones, a la tasa aprobada de su fecha.
        </p>
      ) : null}
      <ExpenseLinesEditor
        lines={values.lines}
        onChange={(lines) => set({ lines })}
        masters={masters.data}
        errors={fe.errors}
        preview={preview.preview}
        fromOrder={values.purchaseOrderId !== ""}
        usd={foreign}
      />
      <ExpenseTotals preview={preview.preview} problem={preview.problem} usd={foreign} />
      <div className="actions form-actions">
        <button type="button" className="primary" disabled={register.busy || match.busy} onClick={submit}>
          Registrar y cotejar
        </button>
      </div>
      <ErrorBox error={register.error ?? match.error} />
    </>
  );
}
