"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { FixedAssetTabs } from "@/components/FixedAssets";
import { SearchSelect } from "@/components/SearchSelect";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, NoPermission, StatusBadge, SuffixInput } from "@/components/ui";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// AF1-05 (E-AF1-05-4, E-AF-2, E-AF1-01-3): each fixed-asset category with its class in force and its draft — useful life, residual %,
// accumulated depreciation and depreciation accounts. The Contador prepares or discards; the Controller (someone else) approves, replacing the
// version in force. Categories without an approved class stand out: their assets cannot go into service.

export default function Page() {
  const { companyId, can, isMine } = useSession();
  const data = useLoad(
    can("ledger:read")
      ? async () => {
          const [classes, categories, accounts] = await Promise.all([
            query("/api/v1/companies/{companyId}/fixed-assets/classes", { path: { companyId } }),
            can("master_data:read")
              ? query("/api/v1/companies/{companyId}/procurement/expense-categories", { path: { companyId }, query: { status: "ACTIVE" } })
              : Promise.resolve({ items: [] }),
            query("/api/v1/companies/{companyId}/finance/accounts", { path: { companyId } }),
          ]);
          return { classes: classes.items, categories: categories.items.filter((c) => c.goodsType606 === "04"), accounts: accounts.items };
        }
      : null,
    [companyId],
  );
  const prepare = useCommand("fa-class-prepare", "/api/v1/companies/{companyId}/fixed-assets/prepare-asset-class");
  const approve = useCommand("fa-class-approve", "/api/v1/companies/{companyId}/fixed-assets/approve-asset-class");
  const discard = useCommand("fa-class-discard", "/api/v1/companies/{companyId}/fixed-assets/discard-asset-class");
  const [form, setForm] = useState({ categoryId: "", life: "", residual: "", accumulatedId: "", expenseId: "" });
  if (!can("ledger:read")) {
    return <NoPermission />;
  }
  if (data.data === null) {
    return <LoadingIndicator error={data.error} />;
  }
  const { classes, categories, accounts } = data.data;
  const active = (categoryId: string) => classes.find((k) => k.expenseCategoryId === categoryId && k.status === "ACTIVE");
  const set = (change: Partial<typeof form>) => setForm((f) => ({ ...f, ...change }));
  const accountOptions = (cls: readonly string[]) =>
    accounts
      .filter((a) => a.status === "ACTIVE" && !a.isControl && cls.includes(a.accountClass ?? ""))
      .map((a) => ({ value: a.accountId, label: `${a.code} — ${a.name}` }));
  return (
    <>
      <h1>Clases de activos fijos</h1>
      <FixedAssetTabs />
      <p className="muted">
        La clase de una categoría fija la vida útil, el porcentaje de valor residual y las cuentas de la depreciación. Un activo copia la clase vigente al
        ponerse en servicio; un cambio de clase solo afecta a los que se pongan en servicio después.
      </p>
      <div className="table-wrap">
        <table data-testid="asset-categories">
          <thead>
            <tr>
              <th>Categoría</th>
              <th>Clase vigente</th>
              <th>Por aprobar</th>
            </tr>
          </thead>
          <tbody>
            {categories.map((c) => {
              const current = active(c.expenseCategoryId);
              const draft = classes.find((k) => k.expenseCategoryId === c.expenseCategoryId && k.status === "DRAFT");
              return (
                <tr key={c.expenseCategoryId} data-testid={`asset-category:${c.code}`} className={current ? undefined : "has-error"}>
                  <td>
                    {c.name} <span className="muted">({c.accountCode})</span>
                  </td>
                  <td>
                    {current ? (
                      `${current.usefulLifeMonths} meses · residual ${current.residualPct} % · ${current.accumulatedAccountCode} / ${current.expenseAccountCode}`
                    ) : (
                      <StatusBadge status="ATTENTION" label="Sin clase aprobada" />
                    )}
                  </td>
                  <td>
                    {draft ? (
                      <div className="actions row-buttons">
                        <span>
                          {draft.usefulLifeMonths} meses · {draft.residualPct} % · {draft.accumulatedAccountCode} / {draft.expenseAccountCode} (
                          {draft.preparedByName})
                        </span>
                        {can("fixed_asset:approve") && !isMine(draft.preparedByName) ? (
                          <ConfirmAction
                            label="Aprobar clase"
                            className="primary"
                            stepUp
                            busy={approve.busy}
                            consequence={`La clase queda vigente para ${c.name}${current ? " y reemplaza a la anterior" : ""}; los activos que se pongan en servicio desde ahora la usan.`}
                            onConfirm={async () =>
                              (await approve.run(
                                { assetClassId: draft.assetClassId, expectedVersion: draft.version },
                                undefined,
                                `Clase de ${c.name} aprobada.`,
                              )) && data.reload()
                            }
                          />
                        ) : null}
                        {can("fixed_asset:manage") ? (
                          <ConfirmAction
                            label="Descartar"
                            danger
                            busy={discard.busy}
                            consequence="El borrador se descarta; la clase vigente, si hay, sigue."
                            onConfirm={async () =>
                              (await discard.run({ assetClassId: draft.assetClassId, expectedVersion: draft.version }, undefined, "Borrador descartado.")) &&
                              data.reload()
                            }
                          />
                        ) : null}
                      </div>
                    ) : (
                      "—"
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <ErrorBox error={approve.error ?? discard.error} />

      {can("fixed_asset:manage") ? (
        <form
          className="card"
          noValidate
          data-testid="asset-class-form"
          onSubmit={async (e) => {
            e.preventDefault();
            if (
              await prepare.run(
                {
                  expenseCategoryId: form.categoryId,
                  usefulLifeMonths: Number(form.life),
                  residualPct: form.residual,
                  accumulatedAccountId: form.accumulatedId,
                  expenseAccountId: form.expenseId,
                },
                undefined,
                "Clase preparada; el Controller la aprueba.",
              )
            ) {
              setForm({ categoryId: "", life: "", residual: "", accumulatedId: "", expenseId: "" });
              data.reload();
            }
          }}
        >
          <h2>Preparar una clase</h2>
          <Field label="Categoría de activo fijo" required>
            <SearchSelect
              aria-label="Categoría de activo fijo"
              value={form.categoryId}
              onChange={(categoryId) => set({ categoryId })}
              options={categories.map((c) => ({ value: c.expenseCategoryId, label: c.name, keywords: c.code }))}
            />
          </Field>
          <Field label="Vida útil" required>
            <SuffixInput suffix="meses" inputMode="numeric" value={form.life} onChange={(life) => set({ life })} />
          </Field>
          <Field label="Valor residual" required>
            <SuffixInput suffix="%" inputMode="decimal" value={form.residual} onChange={(residual) => set({ residual })} />
          </Field>
          <Field label="Cuenta de depreciación acumulada" required>
            <SearchSelect
              aria-label="Cuenta de depreciación acumulada"
              value={form.accumulatedId}
              onChange={(accumulatedId) => set({ accumulatedId })}
              options={accountOptions(["ASSET"])}
            />
          </Field>
          <Field label="Cuenta de gasto de depreciación" required>
            <SearchSelect
              aria-label="Cuenta de gasto de depreciación"
              value={form.expenseId}
              onChange={(expenseId) => set({ expenseId })}
              options={accountOptions(["EXPENSE", "COST"])}
            />
          </Field>
          <div className="actions">
            <button type="submit" className="primary" disabled={prepare.busy}>
              Preparar clase
            </button>
          </div>
          <ErrorBox error={prepare.error} />
        </form>
      ) : null}
    </>
  );
}
