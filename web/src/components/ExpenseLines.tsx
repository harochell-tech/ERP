"use client";

import { useEffect, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { MoneyText } from "@/components/SalesUx4";
import { FieldMessage, fieldAria, LineTable, Money } from "@/components/ui";
import { describeError } from "@/lib/errors";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { type ExpenseLine, EMPTY_EXPENSE_LINE } from "@/lib/expenses";
import { useLoad } from "@/lib/useQuery";
import { previewQuery } from "@/lib/ux4a";
import { SearchSelect } from "@/components/SearchSelect";

// GAS1-07 (E-GAS-07-2/3): the lines of an expense order or invoice — what is bought, its category and tax type (lists of the
// ACTIVE categories and of the types in force on the document's date), quantity and price — and the server's preview of the net and
// taxes. Nothing is computed here.

export type ExpenseMasters = { categories: Schemas["ExpenseCategoryView"][]; types: Schemas["PurchaseTaxTypeView"][] };

export function useExpenseMasters(companyId: string, date: string, enabled: boolean): { data: ExpenseMasters | null; error: unknown } {
  return useLoad(
    enabled && date
      ? async () => {
          const [categories, types] = await Promise.all([
            query("/api/v1/companies/{companyId}/procurement/expense-categories", { path: { companyId }, query: { status: "ACTIVE" } }),
            query("/api/v1/companies/{companyId}/tax/purchase-tax-types", { path: { companyId }, query: { date } }),
          ]);
          return { categories: categories.items, types: types.items };
        }
      : null,
    [companyId, date, enabled],
  );
}

/**
 * Lines whose every field is complete enough to price, in the shape both previews take. In USD (a foreign supplier, E-USD1-03-3) the
 * lines carry no tax type.
 */
export function previewLines(lines: readonly ExpenseLine[], usd = false): Schemas["ExpenseOrderLineInput"][] | null {
  const ready = lines.map((l) => ({ ...l, quantity: normalizeInput(l.quantity), unitPrice: normalizeInput(l.unitPrice) }));
  return ready.length > 0 &&
    ready.every((l) => l.description.trim() && l.expenseCategoryId && (usd || l.taxTypeId) && isPositiveDecimal(l.quantity, 6) && isPositiveDecimal(l.unitPrice, 6))
    ? ready.map((l) => ({
        description: l.description.trim(),
        expenseCategoryId: l.expenseCategoryId,
        taxTypeId: usd ? null : l.taxTypeId,
        quantity: l.quantity,
        unitPrice: l.unitPrice,
      }))
    : null;
}

type PreviewPath = "/api/v1/companies/{companyId}/procurement/expense-purchase-orders/preview" | "/api/v1/companies/{companyId}/procurement/expense-invoices/preview";

/** The server's net and taxes of the lines on <paramref name="date"/>, asked 400 ms after the last change. */
export function useExpensePreview(companyId: string, path: PreviewPath, date: string, lines: readonly ExpenseLine[], currency = "DOP") {
  const ready = date ? previewLines(lines, currency === "USD") : null;
  const key = ready ? JSON.stringify({ orderDate: date, lines: ready, currency }) : "";
  const [state, setState] = useState<{ key: string; preview: Schemas["ExpenseOrderPreview"] | null; problem: string | null }>({ key: "", preview: null, problem: null });
  useEffect(() => {
    if (!key) {
      return;
    }
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      previewQuery(path, companyId, JSON.parse(key) as Schemas["ExpenseOrderPreviewRequest"], controller.signal)
        .then((preview) => setState({ key, preview, problem: null }))
        .catch((caught: unknown) => {
          if (!controller.signal.aborted) {
            setState({ key, preview: null, problem: describeError(caught).message });
          }
        });
    }, 400);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [companyId, key, path]);
  return state.key === key && key ? state : { key, preview: null, problem: null };
}

export function ExpenseTotals({ preview, problem, usd = false }: { preview: Schemas["ExpenseOrderPreview"] | null; problem: string | null; usd?: boolean }) {
  if (problem) {
    return (
      <p className="muted" data-testid="expense-preview-problem">
        Vista previa no disponible: {problem}
      </p>
    );
  }
  if (!preview) {
    return <p className="muted">Complete las líneas para ver el neto y los impuestos calculados por el sistema.</p>;
  }
  return (
    <div className="preview-box" data-testid="expense-preview">
      <h3>Importes calculados por el sistema (vista previa, no se guarda)</h3>
      <dl>
        <dt>{usd ? "Neto (US$)" : "Neto"}</dt>
        <dd>
          <MoneyText value={preview.netTotal} testId="expense-preview-net" />
        </dd>
        {usd ? (
          <>
            <dt>Tasa del día</dt>
            <dd data-testid="expense-preview-rate">
              {preview.exchangeRate ?? "—"}
              {preview.rateDate ? ` (aprobada para el ${preview.rateDate})` : ""}
            </dd>
            <dt>Equivalente en RD$</dt>
            <dd>{preview.totalDop != null ? <MoneyText value={preview.totalDop} testId="expense-preview-dop" /> : "—"}</dd>
          </>
        ) : (
          <>
            <dt>Impuestos según el tipo de cada línea</dt>
            <dd>
              {preview.taxTotal !== null ? (
                <MoneyText value={preview.taxTotal} testId="expense-preview-taxes" />
              ) : (
                <span>No se pueden calcular: {preview.taxesUnavailableReason ?? preview.taxesUnavailableCode}</span>
              )}
            </dd>
          </>
        )}
        <dt>{usd ? "Total (US$)" : "Total"}</dt>
        <dd>{preview.total !== null ? <MoneyText value={preview.total} testId="expense-preview-total" /> : "—"}</dd>
      </dl>
    </div>
  );
}

/** The editable lines. With <paramref name="fromOrder"/>, category and tax type are the order line's and cannot change. */
export function ExpenseLinesEditor({
  lines,
  onChange,
  masters,
  errors,
  preview,
  fromOrder = false,
  usd = false,
}: {
  lines: ExpenseLine[];
  onChange: (lines: ExpenseLine[]) => void;
  masters: ExpenseMasters;
  errors: Partial<Record<string, string>>;
  preview: Schemas["ExpenseOrderPreview"] | null;
  fromOrder?: boolean;
  /** E-USD1-07-3: a foreign supplier's lines are in USD, without tax type. */
  usd?: boolean;
}) {
  const money = usd ? "US$" : "RD$";
  const set = (index: number, change: Partial<ExpenseLine>) => onChange(lines.map((l, i) => (i === index ? { ...l, ...change } : l)));
  return (
    <>
      <LineTable>
        <thead>
          <tr>
            <th>Descripción</th>
            <th>Categoría de gasto</th>
            {usd ? null : <th>Tipo de impuesto</th>}
            <th className="num">Cantidad</th>
            <th className="num">Precio ({money})</th>
            <th className="num">Neto ({money})</th>
            {usd ? null : <th className="num">Impuestos (RD$)</th>}
            <th />
          </tr>
        </thead>
        <tbody>
          {lines.map((line, index) => {
            const priced = preview?.lines[index];
            return (
              <tr key={index}>
                <td>
                  <input
                    aria-label={`Descripción ${index + 1}`}
                    maxLength={200}
                    value={line.description}
                    onChange={(e) => set(index, { description: e.target.value })}
                    {...fieldAria(errors[`line-${index}-description`], `expense-line-${index}-description`, true)}
                  />
                  <FieldMessage id={`expense-line-${index}-description`} error={errors[`line-${index}-description`]} />
                </td>
                <td>
                  <SearchSelect
                    aria-label={`Categoría ${index + 1}`}
                    disabled={fromOrder}
                    value={line.expenseCategoryId}
                    onChange={(expenseCategoryId) => set(index, { expenseCategoryId })}
                    {...fieldAria(errors[`line-${index}-category`], `expense-line-${index}-category`, true)}
                    options={masters.categories.map((c) => ({ value: c.expenseCategoryId, label: c.name, keywords: c.code }))}
                  />
                  <FieldMessage id={`expense-line-${index}-category`} error={errors[`line-${index}-category`]} />
                </td>
                {usd ? null : (
                  <td>
                    <select
                      aria-label={`Tipo de impuesto ${index + 1}`}
                      disabled={fromOrder}
                      value={line.taxTypeId}
                      onChange={(e) => set(index, { taxTypeId: e.target.value })}
                      {...fieldAria(errors[`line-${index}-tax`], `expense-line-${index}-tax`, true)}
                    >
                      <option value="">Seleccione…</option>
                      {masters.types.map((t) => (
                        <option key={t.taxTypeId} value={t.taxTypeId}>
                          {t.label}
                        </option>
                      ))}
                    </select>
                    <FieldMessage id={`expense-line-${index}-tax`} error={errors[`line-${index}-tax`]} />
                  </td>
                )}
                <td className="num">
                  <input
                    aria-label={`Cantidad ${index + 1}`}
                    inputMode="decimal"
                    value={line.quantity}
                    onChange={(e) => set(index, { quantity: e.target.value })}
                    {...fieldAria(errors[`line-${index}-quantity`], `expense-line-${index}-quantity`, true)}
                  />
                  <FieldMessage id={`expense-line-${index}-quantity`} error={errors[`line-${index}-quantity`]} />
                </td>
                <td className="num">
                  <input
                    aria-label={`Precio ${index + 1}`}
                    inputMode="decimal"
                    value={line.unitPrice}
                    onChange={(e) => set(index, { unitPrice: e.target.value })}
                    {...fieldAria(errors[`line-${index}-price`], `expense-line-${index}-price`, true)}
                  />
                  <FieldMessage id={`expense-line-${index}-price`} error={errors[`line-${index}-price`]} />
                </td>
                <td className="num">{priced ? <Money value={priced.netAmount} testId={`expense-line-net:${index + 1}`} /> : <span className="muted">—</span>}</td>
                {usd ? null : (
                  <td className="num">{priced?.taxes != null ? <Money value={priced.taxes} testId={`expense-line-taxes:${index + 1}`} /> : <span className="muted">—</span>}</td>
                )}
                <td>
                  {lines.length > 1 && !fromOrder ? (
                    <button type="button" onClick={() => onChange(lines.filter((_, i) => i !== index))}>
                      Quitar
                    </button>
                  ) : null}
                </td>
              </tr>
            );
          })}
        </tbody>
      </LineTable>
      {fromOrder ? null : (
        <div className="actions">
          <button type="button" onClick={() => onChange([...lines, { ...EMPTY_EXPENSE_LINE }])}>
            Agregar línea
          </button>
        </div>
      )}
    </>
  );
}

/** The form's own checks of the lines, keyed as the editor shows them. */
export function checkExpenseLines(lines: readonly ExpenseLine[], usd = false): Record<string, string | false> {
  const found: Record<string, string | false> = {};
  lines.forEach((l, i) => {
    found[`line-${i}-description`] = !l.description.trim() && "Diga qué se compra.";
    found[`line-${i}-category`] = !l.expenseCategoryId && "Elija la categoría.";
    found[`line-${i}-tax`] = !usd && !l.taxTypeId && "Elija el tipo de impuesto.";
    found[`line-${i}-quantity`] = !isPositiveDecimal(normalizeInput(l.quantity), 6) && "Cantidad mayor que cero (hasta 6 decimales).";
    found[`line-${i}-price`] = !isPositiveDecimal(normalizeInput(l.unitPrice), 6) && "Precio mayor que cero (hasta 6 decimales).";
  });
  return found;
}
