"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmAction, ErrorBox, Field, LineTable, Loading, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { categoryCode, LINE_CLASS_LABELS } from "@/lib/expenses";
import { ISR_WITHHOLDING_TYPES, isrWithholdingTypeLabel } from "@/lib/fiscalRuleForm";
import { GOODS_TYPES } from "@/lib/fiscalReports";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { SearchSelect } from "@/components/SearchSelect";

// GAS1-07 (E-GAS-07-1): Maestros › Categorías de gasto. Each category says which expense account a purchase goes to, its 606 type
// and whether it is a service or a good. The Contador or Controller prepares; the Controller approves (several at once, with
// step-up, never his own); a category goes out of use and back.

type Category = Schemas["ExpenseCategoryView"];

const STATUS_LABELS: Readonly<Record<string, string>> = { DRAFT: "Por aprobar", ACTIVE: "Activa", INACTIVE: "Inactiva" };

interface Draft {
  expenseCategoryId: string | null;
  version: number;
  name: string;
  accountId: string;
  goodsType606: string;
  lineClass: string;
  /** X1-02 (E-X1-02-1): the 606 ISR withholding type of its purchases; "" = none. */
  isrWithholdingType: string;
}

const EMPTY: Draft = { expenseCategoryId: null, version: 0, name: "", accountId: "", goodsType606: "", lineClass: "SERVICE", isrWithholdingType: "" };

export default function ExpenseCategories() {
  const { companyId, can, isMyUserId } = useSession();
  const readable = can("master_data:read");
  const canPrepare = can("expense_category:prepare");
  const canApprove = can("expense_category:approve");
  const [status, setStatus] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [draft, setDraft] = useState<Draft | null>(null);
  const list = useLoad(readable ? () => query("/api/v1/companies/{companyId}/procurement/expense-categories", { path: { companyId }, query: { status: status || undefined } }) : null, [
    companyId,
    status,
  ]);
  const accounts = useLoad(canPrepare && can("configuration:read") ? () => query("/api/v1/companies/{companyId}/finance/accounts", { path: { companyId } }) : null, [companyId]);
  const prepare = useCommand("prepare-expense-category", "/api/v1/companies/{companyId}/procurement/prepare-expense-category");
  const update = useCommand("update-expense-category", "/api/v1/companies/{companyId}/procurement/update-expense-category-draft");
  const approve = useCommand("approve-expense-categories", "/api/v1/companies/{companyId}/procurement/approve-expense-categories");
  const deactivate = useCommand("deactivate-expense-category", "/api/v1/companies/{companyId}/procurement/deactivate-expense-category");
  const reactivate = useCommand("reactivate-expense-category", "/api/v1/companies/{companyId}/procurement/reactivate-expense-category");
  const setIsr = useCommand("set-expense-category-isr", "/api/v1/companies/{companyId}/procurement/set-expense-category-isr-type");
  const fe = useFieldErrors<string>();

  if (!readable) {
    return <NoPermission />;
  }
  if (list.data === null) {
    return <Loading error={list.error} />;
  }
  const items = list.data.items;
  const approvable = items.filter((c) => c.status === "DRAFT" && !isMyUserId(c.preparedBy));
  // E-USD1-03-6: a category may also point to a fixed-asset account (ASSET, not control) when its 606 type is 04.
  const expenseAccounts = (accounts.data?.items ?? []).filter(
    (a) => a.status === "ACTIVE" && !a.isControl && (a.accountClass === "EXPENSE" || a.code.startsWith("6") || a.accountClass === "ASSET"),
  );
  const assetIds = new Set(expenseAccounts.filter((a) => a.accountClass === "ASSET").map((a) => a.accountId));
  const busy = prepare.busy || update.busy || approve.busy || deactivate.busy || reactivate.busy || setIsr.busy;
  const toggle = (id: string) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });

  const save = async () => {
    if (!draft) {
      return;
    }
    const found = {
      name: !draft.name.trim() && "Indique el nombre.",
      accountId: !draft.expenseCategoryId && !draft.accountId && "Elija la cuenta de gasto.",
      goodsType606:
        (!draft.goodsType606 && "Elija el tipo del 606.") ||
        (assetIds.has(draft.accountId) && draft.goodsType606 !== "04" && "Una categoría de activo fijo es del tipo 04 del 606."),
    };
    if (!fe.check(found)) {
      return;
    }
    const response = draft.expenseCategoryId
      ? await update.run(
          {
            expenseCategoryId: draft.expenseCategoryId,
            expectedVersion: draft.version,
            name: draft.name.trim(),
            goodsType606: draft.goodsType606,
            lineClass: draft.lineClass,
            isrWithholdingType: draft.isrWithholdingType || null,
          },
          undefined,
          `Categoría ${draft.name.trim()} corregida.`,
        )
      : await prepare.run(
          {
            code: categoryCode(draft.name),
            name: draft.name.trim(),
            accountId: draft.accountId,
            goodsType606: draft.goodsType606,
            lineClass: draft.lineClass,
            isrWithholdingType: draft.isrWithholdingType || null,
          },
          undefined,
          `Categoría ${draft.name.trim()} preparada; la aprueba el Controller.`,
        );
    if (response) {
      setDraft(null);
      list.reload();
    }
  };

  const approveSelected = async () => {
    const ids = approvable.filter((c) => selected.has(c.expenseCategoryId)).map((c) => c.expenseCategoryId);
    const response = await approve.run({ expenseCategoryIds: ids }, undefined, `${ids.length} categoría(s) enviadas a aprobación.`);
    if (response) {
      setSelected(new Set());
      list.reload();
    }
  };

  return (
    <>
      <h1>Categorías de gasto</h1>
      <p className="muted">
        Cada compra de gastos o servicios se clasifica en una categoría: ella dice a qué cuenta va el gasto, su tipo en el 606 y si es servicio o bien (retenciones).
        Una categoría la prepara el Contador o el Controller y la aprueba el Controller, nunca quien la preparó.
      </p>
      <div className="actions">
        <Field label="Estado">
          <select aria-label="Estado" value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Todas</option>
            {Object.entries(STATUS_LABELS).map(([code, label]) => (
              <option key={code} value={code}>
                {label}
              </option>
            ))}
          </select>
        </Field>
        {canPrepare && !draft ? (
          <button type="button" className="primary" onClick={() => setDraft({ ...EMPTY })}>
            Nueva categoría
          </button>
        ) : null}
        {canApprove && approvable.length > 0 ? (
          <ConfirmAction
            label={`Aprobar seleccionadas (${approvable.filter((c) => selected.has(c.expenseCategoryId)).length})`}
            title="¿Aprobar las categorías seleccionadas?"
            consequence="Quedan activas y se pueden usar en órdenes y facturas de gastos. Su cuenta no se puede cambiar después."
            disabled={!approvable.some((c) => selected.has(c.expenseCategoryId))}
            busy={busy}
            stepUp
            className="primary"
            testId="approve-categories"
            onConfirm={() => void approveSelected()}
          />
        ) : null}
      </div>
      {draft ? (
        <section className="panel" data-testid="category-form">
          <h2>{draft.expenseCategoryId ? "Corregir categoría" : "Nueva categoría"}</h2>
          <Field label="Nombre" required error={fe.errors.name}>
            <input aria-label="Nombre de la categoría" maxLength={120} value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
          </Field>
          {draft.expenseCategoryId ? null : (
            <Field label="Cuenta de gasto o de activo fijo" required error={fe.errors.accountId}>
              <SearchSelect
                aria-label="Cuenta de gasto"
                value={draft.accountId}
                onChange={(accountId) => setDraft({ ...draft, accountId, goodsType606: assetIds.has(accountId) ? "04" : draft.goodsType606 })}
                options={expenseAccounts.map((a) => ({ value: a.accountId, label: `${a.code} ${a.name}`, hint: assetIds.has(a.accountId) ? "activo fijo" : null }))}
              />
            </Field>
          )}
          <Field label="Tipo en el 606" required error={fe.errors.goodsType606}>
            <select aria-label="Tipo en el 606" value={draft.goodsType606} onChange={(e) => setDraft({ ...draft, goodsType606: e.target.value })}>
              <option value="">Seleccione…</option>
              {Object.entries(GOODS_TYPES).map(([code, label]) => (
                <option key={code} value={code}>
                  {code} — {label}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Servicio o bien" required>
            <select aria-label="Servicio o bien" value={draft.lineClass} onChange={(e) => setDraft({ ...draft, lineClass: e.target.value })}>
              {Object.entries(LINE_CLASS_LABELS).map(([code, label]) => (
                <option key={code} value={code}>
                  {label}
                </option>
              ))}
            </select>
          </Field>
          <IsrTypeField value={draft.isrWithholdingType} onChange={(isrWithholdingType) => setDraft({ ...draft, isrWithholdingType })} />
          <div className="actions">
            <button type="button" className="primary" disabled={busy} onClick={() => void save()}>
              {draft.expenseCategoryId ? "Guardar corrección" : "Preparar"}
            </button>
            <button type="button" onClick={() => setDraft(null)}>
              Cancelar
            </button>
          </div>
        </section>
      ) : null}
      <ErrorBox error={prepare.error ?? update.error ?? approve.error ?? deactivate.error ?? reactivate.error ?? setIsr.error ?? accounts.error} />
      <LineTable testId="expense-categories">
        <thead>
          <tr>
            {canApprove ? <th /> : null}
            <th>Categoría</th>
            <th>Cuenta</th>
            <th>606</th>
            <th>Clase</th>
            <th>Retención ISR</th>
            <th>Estado</th>
            <th>Preparó / aprobó</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {items.map((c: Category) => (
            <tr key={c.expenseCategoryId} data-testid={`category:${c.code}`}>
              {canApprove ? (
                <td>
                  {approvable.includes(c) ? (
                    <input type="checkbox" aria-label={`Seleccionar ${c.name}`} checked={selected.has(c.expenseCategoryId)} onChange={() => toggle(c.expenseCategoryId)} />
                  ) : null}
                </td>
              ) : null}
              <td>
                {c.name}
                <div className="muted">{c.code}</div>
              </td>
              <td>
                {c.accountCode} {c.accountName}
              </td>
              <td title={GOODS_TYPES[c.goodsType606]}>{c.goodsType606}</td>
              <td>{LINE_CLASS_LABELS[c.lineClass] ?? c.lineClass}</td>
              <td>
                {canApprove && c.status === "ACTIVE" ? (
                  <select
                    aria-label={`Retención ISR de ${c.name}`}
                    value={c.isrWithholdingType ?? ""}
                    disabled={busy}
                    onChange={(e) =>
                      void setIsr
                        .run(
                          { expenseCategoryId: c.expenseCategoryId, expectedVersion: c.version, isrWithholdingType: e.target.value || null },
                          undefined,
                          `Retención de ISR de ${c.name} cambiada.`,
                        )
                        .then((r) => r && list.reload())
                    }
                  >
                    <option value="">Ninguna</option>
                    {ISR_WITHHOLDING_TYPES.map((t) => (
                      <option key={t} value={t}>
                        {isrWithholdingTypeLabel(t)}
                      </option>
                    ))}
                  </select>
                ) : c.isrWithholdingType ? (
                  isrWithholdingTypeLabel(c.isrWithholdingType)
                ) : (
                  "—"
                )}
              </td>
              <td>
                <StatusBadge status={c.status} label={STATUS_LABELS[c.status]} testId={`category-status:${c.code}`} />
              </td>
              <td>
                {c.preparedByName ?? "—"}
                {c.approvedByName ? ` / ${c.approvedByName}` : ""}
              </td>
              <td>
                {canPrepare && c.status === "DRAFT" ? (
                  <button
                    type="button"
                    onClick={() =>
                      setDraft({
                        expenseCategoryId: c.expenseCategoryId,
                        version: c.version,
                        name: c.name,
                        accountId: c.accountId,
                        goodsType606: c.goodsType606,
                        lineClass: c.lineClass,
                        isrWithholdingType: c.isrWithholdingType ?? "",
                      })
                    }
                  >
                    Corregir
                  </button>
                ) : null}
                {canPrepare && c.status !== "INACTIVE" ? (
                  <ConfirmAction
                    label={c.status === "DRAFT" ? "Descartar" : "Desactivar"}
                    consequence={c.status === "DRAFT" ? "El borrador se descarta." : "Deja de ofrecerse en órdenes y facturas nuevas; lo contabilizado la conserva."}
                    busy={busy}
                    danger
                    onConfirm={() =>
                      void deactivate
                        .run({ expenseCategoryId: c.expenseCategoryId, expectedVersion: c.version }, undefined, `Categoría ${c.name} desactivada.`)
                        .then((r) => r && list.reload())
                    }
                  />
                ) : null}
                {canApprove && c.status === "INACTIVE" && c.approvedBy ? (
                  <ConfirmAction
                    label="Reactivar"
                    consequence="Vuelve a ofrecerse en órdenes y facturas de gastos."
                    busy={busy}
                    onConfirm={() =>
                      void reactivate
                        .run({ expenseCategoryId: c.expenseCategoryId, expectedVersion: c.version }, undefined, `Categoría ${c.name} reactivada.`)
                        .then((r) => r && list.reload())
                    }
                  />
                ) : null}
              </td>
            </tr>
          ))}
        </tbody>
      </LineTable>
    </>
  );
}

/** X1-02 (E-X1-02-1): an ISR withholding rule of a type withholds only on the categories of that type. */
function IsrTypeField({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  return (
    <Field label="Retención de ISR (606)" hint="Honorarios, alquileres u otras rentas: la regla de retención de ese tipo se aplica solo a las compras de esta categoría.">
      <select aria-label="Retención de ISR (606)" value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Ninguna</option>
        {ISR_WITHHOLDING_TYPES.map((t) => (
          <option key={t} value={t}>
            {isrWithholdingTypeLabel(t)}
          </option>
        ))}
      </select>
    </Field>
  );
}
