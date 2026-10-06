"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { ASSET_STATUS, FixedAssetTabs } from "@/components/FixedAssets";
import { SearchSelect } from "@/components/SearchSelect";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// AF1-05 (E-AF1-05-2): Contabilidad › Activos fijos — the cards by status, plant and category, with their cost, accumulated depreciation and
// book value. The Contador creates the cards of invoices posted before AF-1 (E-AF1-02-8).

function Assets() {
  const { companyId, can, plantName } = useSession();
  const router = useRouter();
  const params = useSearchParams();
  const status = params.get("estado") ?? "";
  const plantId = params.get("planta") ?? "";
  const categoryId = params.get("categoria") ?? "";
  const go = (change: Record<string, string>) => {
    const next = new URLSearchParams({ estado: status, planta: plantId, categoria: categoryId, ...change });
    for (const [k, v] of [...next.entries()]) {
      if (!v) next.delete(k);
    }
    router.push(`/contabilidad/activos/?${next.toString()}`);
  };
  const filters = useLoad(
    can("ledger:read")
      ? async () => {
          const [plants, categories] = await Promise.all([
            query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId } }),
            can("master_data:read")
              ? query("/api/v1/companies/{companyId}/procurement/expense-categories", { path: { companyId } })
              : Promise.resolve({ items: [] }),
          ]);
          return { plants: plants.items, categories: categories.items.filter((c) => c.goodsType606 === "04") };
        }
      : null,
    [companyId],
  );
  const list = useLoad(
    can("ledger:read")
      ? () =>
          query("/api/v1/companies/{companyId}/fixed-assets/assets", {
            path: { companyId },
            query: { status: status || undefined, plantId: plantId || undefined, expenseCategoryId: categoryId || undefined, limit: 200 },
          })
      : null,
    [companyId, status, plantId, categoryId],
  );
  const backfill = useCommand("fa-backfill", "/api/v1/companies/{companyId}/fixed-assets/create-cards-for-posted-invoices");
  if (!can("ledger:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Activos fijos</h1>
      <FixedAssetTabs />
      <p className="muted">
        Cada activo nace al contabilizar la factura de una categoría de activo fijo; se pone en servicio con su clase y se deprecia cada mes desde el mes
        siguiente.
      </p>
      <div className="inline-form">
        <Field label="Estado">
          <select aria-label="Estado" value={status} onChange={(e) => go({ estado: e.target.value })}>
            <option value="">Todos</option>
            {Object.entries(ASSET_STATUS).map(([code, label]) => (
              <option key={code} value={code}>
                {label}
              </option>
            ))}
          </select>
        </Field>
        {filters.data ? (
          <>
            <Field label="Planta">
              <select aria-label="Planta" value={plantId} onChange={(e) => go({ planta: e.target.value })}>
                <option value="">Todas</option>
                {filters.data.plants.map((p) => (
                  <option key={p.plantId} value={p.plantId}>
                    {plantName(p.plantId, p.code)}
                  </option>
                ))}
              </select>
            </Field>
            <Field label="Categoría">
              <SearchSelect
                aria-label="Categoría"
                value={categoryId}
                onChange={(categoria) => go({ categoria })}
                options={filters.data.categories.map((c) => ({ value: c.expenseCategoryId, label: c.name, keywords: c.code }))}
                placeholder="Todas"
              />
            </Field>
          </>
        ) : null}
        {can("fixed_asset:manage") ? (
          <ConfirmAction
            label="Crear fichas de facturas ya contabilizadas"
            busy={backfill.busy}
            consequence="Se crea la ficha de cada línea de activo fijo de facturas contabilizadas que todavía no la tenga, con el costo de sus liquidaciones. Si no falta ninguna, no se crea nada."
            onConfirm={async () => (await backfill.run({}, undefined, "Fichas creadas de las facturas ya contabilizadas.")) && list.reload()}
          />
        ) : null}
      </div>
      <ErrorBox error={backfill.error} />
      {list.data === null ? (
        <LoadingIndicator error={list.error} />
      ) : list.data.items.length === 0 ? (
        <EmptyState title="No hay activos con esos filtros." />
      ) : (
        <div className="table-wrap">
          <table data-testid="fixed-assets">
            <thead>
              <tr>
                <th>Número</th>
                <th>Código anterior</th>
                <th>Descripción</th>
                <th>Categoría</th>
                <th>Planta</th>
                <th>Estado</th>
                <th>En servicio</th>
                <th className="num">Costo</th>
                <th className="num">Depreciación acumulada</th>
                <th className="num">Valor en libros</th>
              </tr>
            </thead>
            <tbody>
              {list.data.items.map((a) => (
                <tr key={a.assetId} data-testid={`asset:${a.description}`}>
                  <td>
                    <Link href={`/contabilidad/activo/?id=${a.assetId}`}>{a.assetNo}</Link>
                  </td>
                  <td>{a.externalCode ?? "—"}</td>
                  <td>{a.description}</td>
                  <td>{a.categoryName}</td>
                  <td>{a.plantName}</td>
                  <td>
                    <StatusBadge status={a.status} label={ASSET_STATUS[a.status]} />
                  </td>
                  <td>{a.inServiceOn ? formatDate(a.inServiceOn) : "—"}</td>
                  <td className="num">
                    <Money value={a.cost} />
                  </td>
                  <td className="num">
                    <Money value={a.accumulated} />
                  </td>
                  <td className="num">
                    <Money value={a.bookValue} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Assets />
    </Suspense>
  );
}
