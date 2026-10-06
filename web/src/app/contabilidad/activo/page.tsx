"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { ASSET_STATUS, FixedAssetTabs, MOVEMENT_KIND } from "@/components/FixedAssets";
import { History } from "@/components/History";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate, formatDateTime, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// AF1-05 (E-AF1-05-3): one fixed asset — where it came from, its class and what is left to depreciate, its movements and history. By status and
// permission the Contador puts it into service, transfers it, corrects it and prepares its disposal (the Controller approves on Bajas).

function Asset() {
  const { companyId, can, plantName } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const today = todayInDominicanRepublic();
  const detail = useLoad(
    can("ledger:read") && id ? () => query("/api/v1/companies/{companyId}/fixed-assets/assets/{assetId}", { path: { companyId, assetId: id } }) : null,
    [companyId, id],
  );
  const plants = useLoad(() => query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId } }), [companyId]);
  const service = useCommand(`fa-service:${id}`, "/api/v1/companies/{companyId}/fixed-assets/put-fixed-asset-in-service");
  const transfer = useCommand(`fa-transfer:${id}`, "/api/v1/companies/{companyId}/fixed-assets/transfer-fixed-asset");
  const update = useCommand(`fa-update:${id}`, "/api/v1/companies/{companyId}/fixed-assets/update-fixed-asset");
  const dispose = useCommand(`fa-dispose:${id}`, "/api/v1/companies/{companyId}/fixed-assets/prepare-asset-disposal");
  const [form, setForm] = useState({ date: today, plantId: "", responsible: "", description: "", kind: "SALE", price: "", reason: "" });
  if (!can("ledger:read")) {
    return <NoPermission />;
  }
  if (detail.data === null) {
    return <LoadingIndicator error={detail.error} />;
  }
  const d = detail.data;
  const a = d.asset;
  const live = a.status === "AWAITING_SERVICE" || a.status === "IN_SERVICE";
  const manage = can("fixed_asset:manage");
  const set = (change: Partial<typeof form>) => setForm((f) => ({ ...f, ...change }));
  const plantOptions = (plants.data?.items ?? []).map((p) => (
    <option key={p.plantId} value={p.plantId}>
      {plantName(p.plantId, p.code)}
    </option>
  ));
  const done = () => detail.reload();
  return (
    <>
      <p>
        <Link href="/contabilidad/activos/">← Activos fijos</Link>
      </p>
      <h1>
        {a.assetNo} · {a.description}
      </h1>
      <FixedAssetTabs />
      <p>
        <StatusBadge status={a.status} label={ASSET_STATUS[a.status]} testId="asset-status" />
      </p>
      <dl className="summary">
        <dt>Categoría</dt>
        <dd>{a.categoryName}</dd>
        <dt>Planta</dt>
        <dd>{a.plantName}</dd>
        <dt>Origen</dt>
        <dd>
          {d.supplierInvoiceId ? (
            <Link href={`/cxp/factura/?id=${d.supplierInvoiceId}`}>
              Factura {d.invoiceNumber} de {d.supplierName}
            </Link>
          ) : (
            `Carga inicial${d.externalCode ? ` (código ${d.externalCode})` : ""}`
          )}
        </dd>
        <dt>Comprado</dt>
        <dd>{formatDate(a.acquiredOn)}</dd>
        <dt>En servicio desde</dt>
        <dd>{a.inServiceOn ? formatDate(a.inServiceOn) : "—"}</dd>
        <dt>Responsable</dt>
        <dd>{a.responsible ?? "—"}</dd>
        <dt>Costo</dt>
        <dd>
          <Money value={a.cost} testId="asset-cost" />
        </dd>
        <dt>Depreciación acumulada</dt>
        <dd>
          <Money value={a.accumulated} testId="asset-accumulated" />
        </dd>
        <dt>Valor en libros</dt>
        <dd>
          <Money value={a.bookValue} testId="asset-book-value" />
        </dd>
        <dt>Vida útil</dt>
        <dd>
          {a.usefulLifeMonths
            ? `${a.usefulLifeMonths} meses · ${a.monthsDepreciated} depreciados · ${d.monthsRemaining} por depreciar`
            : "Se fija al ponerlo en servicio"}
        </dd>
        <dt>Valor residual</dt>
        <dd>{d.residualValue ? <Money value={d.residualValue} /> : "—"}</dd>
        <dt>Falta por depreciar</dt>
        <dd>{d.depreciable ? <Money value={d.depreciable} testId="asset-depreciable" /> : "—"}</dd>
      </dl>
      <ErrorBox error={service.error ?? transfer.error ?? update.error ?? dispose.error} />

      {manage && a.status === "AWAITING_SERVICE" ? (
        <section className="card" data-testid="service-form">
          <h2>Poner en servicio</h2>
          <p className="muted">La depreciación empieza el mes siguiente; la vida útil y el residual se toman de la clase aprobada de su categoría.</p>
          <Field label="Fecha de puesta en servicio">
            <input type="date" value={form.date} max={today} onChange={(e) => set({ date: e.target.value })} />
          </Field>
          <Field label="Planta">
            <select value={form.plantId || a.plantId} onChange={(e) => set({ plantId: e.target.value })}>
              {plantOptions}
            </select>
          </Field>
          <Field label="Responsable">
            <input value={form.responsible} placeholder="Persona o cargo" onChange={(e) => set({ responsible: e.target.value })} />
          </Field>
          <ConfirmAction
            label="Poner en servicio"
            className="primary"
            busy={service.busy}
            disabled={!form.responsible.trim()}
            consequence={`${a.assetNo} queda en servicio desde ${formatDate(form.date)}; se deprecia desde el mes siguiente.`}
            onConfirm={async () =>
              (await service.run(
                {
                  assetId: a.assetId,
                  expectedVersion: a.version,
                  inServiceOn: form.date,
                  plantId: form.plantId || a.plantId,
                  responsible: form.responsible.trim(),
                },
                undefined,
                `${a.assetNo} en servicio.`,
              )) && done()
            }
          />
        </section>
      ) : null}

      {manage && live ? (
        <section className="card">
          <h2>Trasladar o corregir</h2>
          <Field label="Planta nueva">
            <select aria-label="Planta nueva" value={form.plantId} onChange={(e) => set({ plantId: e.target.value })}>
              <option value="">—</option>
              {plantOptions}
            </select>
          </Field>
          <Field label="Desde">
            <input type="date" aria-label="Fecha del traslado" value={form.date} max={today} onChange={(e) => set({ date: e.target.value })} />
          </Field>
          <ConfirmAction
            label="Trasladar"
            busy={transfer.busy}
            disabled={!form.plantId || form.plantId === a.plantId}
            consequence="El activo pasa a la otra planta desde esa fecha, sin asiento; la depreciación siguiente se carga a la planta nueva."
            onConfirm={async () =>
              (await transfer.run(
                { assetId: a.assetId, expectedVersion: a.version, plantId: form.plantId, effectiveOn: form.date },
                undefined,
                `${a.assetNo} trasladado.`,
              )) && done()
            }
          />
          <Field label="Descripción">
            <input aria-label="Descripción nueva" value={form.description || a.description} onChange={(e) => set({ description: e.target.value })} />
          </Field>
          <Field label="Responsable">
            <input aria-label="Responsable nuevo" value={form.responsible || (a.responsible ?? "")} onChange={(e) => set({ responsible: e.target.value })} />
          </Field>
          <button
            type="button"
            disabled={update.busy}
            onClick={async () =>
              (await update.run(
                {
                  assetId: a.assetId,
                  expectedVersion: a.version,
                  description: (form.description || a.description).trim(),
                  responsible: (form.responsible || a.responsible || "").trim() || null,
                },
                undefined,
                `${a.assetNo} corregido.`,
              )) && done()
            }
          >
            Guardar corrección
          </button>
        </section>
      ) : null}

      {manage && a.status === "IN_SERVICE" ? (
        <section className="card" data-testid="disposal-form">
          <h2>Preparar la baja</h2>
          <p className="muted">
            El activo debe estar depreciado hasta el mes anterior; la baja la aprueba el Controller en «Bajas». El precio de una venta queda en «Venta de
            activos por cobrar».
          </p>
          <Field label="Tipo de baja">
            <select aria-label="Tipo de baja" value={form.kind} onChange={(e) => set({ kind: e.target.value })}>
              <option value="SALE">Venta</option>
              <option value="SCRAP">Desecho</option>
            </select>
          </Field>
          <Field label="Fecha de la baja">
            <input type="date" aria-label="Fecha de la baja" value={form.date} max={today} onChange={(e) => set({ date: e.target.value })} />
          </Field>
          {form.kind === "SALE" ? (
            <Field label="Precio de venta">
              <input inputMode="decimal" aria-label="Precio de venta" value={form.price} onChange={(e) => set({ price: e.target.value })} />
            </Field>
          ) : null}
          <Field label="Motivo">
            <input aria-label="Motivo de la baja" value={form.reason} onChange={(e) => set({ reason: e.target.value })} />
          </Field>
          <ConfirmAction
            label="Preparar baja"
            busy={dispose.busy}
            disabled={form.reason.trim().length < 3 || (form.kind === "SALE" && !form.price)}
            consequence="La baja queda por aprobar; el Controller la aprueba y entonces se contabiliza."
            onConfirm={async () =>
              (await dispose.run(
                {
                  assetId: a.assetId,
                  kind: form.kind,
                  disposalDate: form.date,
                  price: form.kind === "SALE" ? form.price : null,
                  reason: form.reason.trim(),
                },
                undefined,
                `Baja de ${a.assetNo} preparada.`,
              )) && done()
            }
          />
        </section>
      ) : null}

      <h2>Movimientos</h2>
      <div className="table-wrap">
        <table data-testid="asset-movements">
          <thead>
            <tr>
              <th>Fecha</th>
              <th>Movimiento</th>
              <th className="num">Monto</th>
              <th>Planta</th>
              <th>Registrado</th>
            </tr>
          </thead>
          <tbody>
            {d.movements.map((m, i) => (
              <tr key={i}>
                <td>{formatDate(m.date)}</td>
                <td>{MOVEMENT_KIND[m.kind] ?? m.kind}</td>
                <td className="num">{m.amount ? <Money value={m.amount} /> : "—"}</td>
                <td>{m.plantName ?? "—"}</td>
                <td>{formatDateTime(m.recordedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <History history={d.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Asset />
    </Suspense>
  );
}
