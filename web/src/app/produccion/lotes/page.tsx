"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useRef, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { PlantSelect, ProductionBadge, useChosenPlant, usePlants, type PlantOption } from "@/components/Production";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, NoPermission, useFieldErrors } from "@/components/ui";
import { formatQuantity, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import {
  defaultLotFilter,
  isReadyToRelease,
  lotActions,
  lotQueryStatus,
  READY_TO_RELEASE,
  scrapLocations,
  stockLocations,
  type LotAction,
} from "@/lib/production";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { curingRemainingText, dayHref } from "@/lib/ux4bProduction";

type Lot = Schemas["FgLotSummary"];

// MFG1-07 (E-MFG1-07-3): finished-goods lots in curing — Calidad releases a cured lot to a stock location, or blocks / unblocks it
// with a reason; the Gerente de planta scraps units (step-up, handled by useCommand). The API decides whether curing is done.
// UX3-02 (E-UX3-12): one "Acciones" button per lot opens a dialog with the actions the user may take on it (a sheet on a phone);
// Calidad opens the screen on "Listos para liberar".
// UX4-03: "Liberar" only once the curing is done, the remaining hours otherwise (P-29/P-26); no location preselected (P-31); the run
// links to its production day (P-32); "?lote=" opens the screen on one lot (P-17); a released lot reads done (P-12).

const ACTION_LABELS: Readonly<Record<LotAction, string>> = {
  release: "Liberar",
  block: "Bloquear",
  unblock: "Desbloquear",
  scrap: "Desechar unidades",
};

function LotActions({ lot, plant, onDone }: { lot: Lot; plant: PlantOption | undefined; onDone: () => void }) {
  const { can } = useSession();
  const ref = useRef<HTMLDialogElement>(null);
  const [open, setOpen] = useState(false);
  const [action, setAction] = useState<LotAction | null>(null);
  const targets = stockLocations(plant?.locations ?? []);
  const sources = scrapLocations(plant?.locations ?? []);
  const [form, setForm] = useState(() => ({
    toLocationId: "",
    reason: "",
    scrapLocationId: sources.find((l) => l.code === lot.locationCode)?.locationId ?? "",
    quantity: "",
  }));
  const fe = useFieldErrors<"toLocationId" | "reason" | "scrapLocationId" | "quantity">();
  const release = useCommand(`release-lot:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/release-lot");
  const block = useCommand(`block-lot:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/block-lot", `Lote ${lot.lotCode} bloqueado.`);
  const unblock = useCommand(`unblock-lot:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/unblock-lot", `Lote ${lot.lotCode} desbloqueado; vuelve al estado que tenía.`);
  const scrap = useCommand(`scrap-lot:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/scrap-lot");
  const busy = release.busy || block.busy || unblock.busy || scrap.busy;
  const available = lotActions(lot.status, can, lot.curingDone);
  const remaining = curingRemainingText(lot.status, lot.curingHoursRemaining);
  const target = { plantId: lot.plantId, lotId: lot.lotId, expectedVersion: lot.version };
  const toLocation = targets.find((l) => l.locationId === form.toLocationId);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) {
      return;
    }
    if (open && !dialog.open) {
      if (typeof dialog.showModal === "function") {
        dialog.showModal();
      } else {
        dialog.setAttribute("open", "");
      }
    } else if (!open && dialog.open) {
      dialog.close();
    }
  }, [open]);

  if (available.length === 0) {
    return null;
  }
  const close = () => {
    setOpen(false);
    setAction(null);
    fe.clear();
  };
  const done = (response: unknown) => {
    if (response) {
      close();
      onDone();
    }
  };
  const confirm = async () => {
    const quantity = normalizeInput(form.quantity);
    switch (action) {
      case "release":
        if (fe.check({ toLocationId: !form.toLocationId && "Elija a dónde se libera el lote." })) {
          done(
            await release.run({ ...target, toLocationId: form.toLocationId }, undefined, `Lote ${lot.lotCode} liberado a ${toLocation?.code ?? "la ubicación elegida"}.`),
          );
        }
        return;
      case "block":
      case "unblock":
        if (fe.check({ reason: form.reason.trim() === "" && "Indique el motivo." })) {
          done(await (action === "block" ? block : unblock).run({ ...target, reason: form.reason.trim() }));
        }
        return;
      case "scrap":
        if (
          fe.check({
            scrapLocationId: !form.scrapLocationId && "Elija la ubicación de donde salen las unidades.",
            quantity: !isPositiveDecimal(quantity, 6) && "Unidades mayores que cero (hasta 6 decimales).",
            reason: form.reason.trim() === "" && "Indique el motivo.",
          })
        ) {
          done(
            await scrap.run(
              { plantId: lot.plantId, lotId: lot.lotId, locationId: form.scrapLocationId, quantity, reason: form.reason.trim() },
              form,
              `${formatQuantity(quantity)} unidades del lote ${lot.lotCode} desechadas.`,
            ),
          );
        }
        return;
      default:
        return;
    }
  };

  return (
    <>
      <button type="button" onClick={() => setOpen(true)} aria-label={`Acciones del lote ${lot.lotCode}`}>
        Acciones
      </button>
      <dialog
        ref={ref}
        className="confirm-dialog lot-actions"
        aria-label={`Acciones del lote ${lot.lotCode}`}
        onCancel={(e) => {
          e.preventDefault();
          close();
        }}
        onClick={(e) => {
          if (e.target === ref.current) {
            close();
          }
        }}
      >
        {open ? (
          <form
            method="dialog"
            noValidate
            onSubmit={(e) => {
              e.preventDefault();
              if (!busy && action) {
                void confirm();
              }
            }}
          >
            <h2>
              Lote <span className="mono">{lot.lotCode}</span>
            </h2>
            <p className="muted">
              {lot.itemCode} · {formatQuantity(lot.quantity)} unidades · <ProductionBadge kind="lot" status={lot.status} />
              {remaining ? ` · ${remaining}` : ""}
            </p>
            {lot.status === "CURING" && !lot.curingDone ? (
              <p className="notice" data-testid="release-not-yet">
                Todavía no se puede liberar: se podrá desde {formatDateTime(lot.releasableAt)}.
              </p>
            ) : null}
            {action === null ? (
              <div className="actions">
                {available.map((a) => (
                  <button key={a} type="button" className={a === "release" ? "primary" : a === "scrap" ? "danger" : undefined} onClick={() => setAction(a)}>
                    {ACTION_LABELS[a]}
                  </button>
                ))}
              </div>
            ) : (
              <section>
                <h3>{ACTION_LABELS[action]}</h3>
                {action === "release" ? (
                  <>
                    <p>Las {formatQuantity(lot.quantity)} unidades del lote pasan de curado a la ubicación elegida y quedan disponibles para despacho. No se puede deshacer.</p>
                    <Field label="Liberar a" required error={fe.errors.toLocationId}>
                      <select aria-label={`Liberar ${lot.lotCode} a`} value={form.toLocationId} onChange={(e) => setForm({ ...form, toLocationId: e.target.value })}>
                        <option value="">Seleccione…</option>
                        {targets.map((l) => (
                          <option key={l.locationId} value={l.locationId}>
                            {l.code}
                          </option>
                        ))}
                      </select>
                    </Field>
                  </>
                ) : null}
                {action === "block" ? <p>El lote queda bloqueado: no se libera ni se despacha hasta que Calidad lo desbloquee.</p> : null}
                {action === "unblock" ? <p>El lote vuelve al estado que tenía antes del bloqueo.</p> : null}
                {action === "scrap" ? (
                  <>
                    <p>Las unidades salen del inventario y se contabiliza la pérdida. No se puede deshacer.</p>
                    <p className="notice">
                      Esta acción requiere autenticación reciente: si su última autenticación no es reciente, al confirmar el sistema le pedirá entrar de nuevo con su
                      cuenta y luego deberá pulsar otra vez.
                    </p>
                    <Field label="Desechar desde" required error={fe.errors.scrapLocationId} hint="La ubicación donde están las unidades.">
                      <select aria-label={`Desechar desde ${lot.lotCode}`} value={form.scrapLocationId} onChange={(e) => setForm({ ...form, scrapLocationId: e.target.value })}>
                        <option value="">Seleccione…</option>
                        {sources.map((l) => (
                          <option key={l.locationId} value={l.locationId}>
                            {l.code}
                          </option>
                        ))}
                      </select>
                    </Field>
                    <Field label="Unidades" required error={fe.errors.quantity}>
                      <input aria-label={`Unidades a desechar ${lot.lotCode}`} inputMode="decimal" value={form.quantity} onChange={(e) => setForm({ ...form, quantity: e.target.value })} />
                    </Field>
                  </>
                ) : null}
                {action !== "release" ? (
                  <Field label="Motivo" required error={fe.errors.reason} wide>
                    <input aria-label={`Motivo: ${ACTION_LABELS[action]} ${lot.lotCode}`} value={form.reason} onChange={(e) => setForm({ ...form, reason: e.target.value })} />
                  </Field>
                ) : null}
              </section>
            )}
            <ErrorBox error={release.error ?? block.error ?? unblock.error ?? scrap.error} />
            <div className="dialog-actions">
              <button type="button" autoFocus onClick={action === null ? close : () => setAction(null)}>
                {action === null ? "Cerrar" : "Volver"}
              </button>
              {action !== null ? (
                <button type="submit" className={action === "release" || action === "unblock" ? "primary" : "danger-solid"} disabled={busy}>
                  Confirmar: {ACTION_LABELS[action]}
                </button>
              ) : null}
            </div>
          </form>
        ) : null}
      </dialog>
    </>
  );
}

function LotRow({ lot, plant, onDone }: { lot: Lot; plant: PlantOption | undefined; onDone: () => void }) {
  const remaining = curingRemainingText(lot.status, lot.curingHoursRemaining);
  return (
    <tr>
      <td>
        <span className="mono">{lot.lotCode}</span>
        {/* On a phone the table scrolls sideways: the lot's one button stays in its first cell, always in view. */}
        <div>
          <LotActions lot={lot} plant={plant} onDone={onDone} />
        </div>
      </td>
      <td>{lot.itemCode}</td>
      <td className="mono">
        <Link href={dayHref(lot.businessDate)} title="Ver la producción de ese día">
          {lot.runNo}
        </Link>
      </td>
      <td>{formatDate(lot.businessDate)}</td>
      <td className="wrap">
        <ProductionBadge kind="lot" status={lot.status} testId={`lot-status-${lot.lotCode}`} />
        {lot.blockReason ? <div className="muted">{lot.blockReason}</div> : null}
      </td>
      <td>
        {formatDateTime(lot.releasableAt)}
        {remaining ? (
          <div className="muted" data-testid={`lot-remaining-${lot.lotCode}`}>
            {remaining}
          </div>
        ) : null}
      </td>
      <td>{lot.locationCode ?? "—"}</td>
      <td className="num">{formatQuantity(lot.quantity)} un</td>
      <td className="num">{lot.racks}</td>
    </tr>
  );
}

function Lots() {
  const { companyId, can } = useSession();
  const plants = usePlants();
  const { plant, setPlant } = useChosenPlant(plants.data);
  const plantId = plant?.plantId ?? "";
  const params = useSearchParams();
  const [lotCode, setLotCode] = useState(() => params.get("lote")?.trim() ?? "");
  const [filter, setFilter] = useState(() => (lotCode ? "" : defaultLotFilter(can("fg_lot:release"))));
  const status = lotQueryStatus(filter);
  const { data, error, reload } = useLoad(
    can("production:read") && plantId
      ? () => query("/api/v1/companies/{companyId}/manufacturing/lots", { path: { companyId }, query: { plantId, status, limit: 200 } })
      : null,
    [companyId, plantId, status],
  );
  if (!can("production:read")) {
    return <NoPermission />;
  }
  if (plants.data === null) {
    return <LoadingIndicator error={plants.error} />;
  }
  const byFilter = data === null ? null : filter === READY_TO_RELEASE ? data.items.filter(isReadyToRelease) : data.items;
  const lots = byFilter === null ? null : lotCode ? byFilter.filter((l) => l.lotCode === lotCode) : byFilter;
  return (
    <>
      <h1>Curado y liberación</h1>
      {plants.data.length === 0 ? <EmptyState title="No hay plantas con producción." /> : <PlantSelect plants={plants.data} value={plantId} onChange={setPlant} />}
      <Field label="Estado">
        <select aria-label="Estado" value={filter} onChange={(e) => setFilter(e.target.value)}>
          <option value="">Todos</option>
          <option value={READY_TO_RELEASE}>Listos para liberar</option>
          <option value="CURING">En curado</option>
          <option value="BLOCKED">Bloqueado</option>
          <option value="RELEASED">Liberado</option>
          <option value="FINAL_RELEASED">Liberación final</option>
          <option value="SCRAPPED">Desechado</option>
          <option value="VOIDED">Anulado</option>
        </select>
      </Field>
      {lotCode ? (
        <p className="notice" data-testid="lot-focus">
          Mostrando el lote <span className="mono">{lotCode}</span>.{" "}
          <button type="button" onClick={() => setLotCode("")}>
            Ver todos los lotes
          </button>
        </p>
      ) : null}
      {lots === null ? (
        plantId ? <LoadingIndicator error={error} /> : null
      ) : lots.length === 0 ? (
        <EmptyState title={filter === READY_TO_RELEASE ? "No hay lotes con el curado cumplido por liberar." : "No hay lotes con ese filtro."}>
          <p>
            {filter === READY_TO_RELEASE
              ? "Los lotes entran a curado al cerrar el resumen del turno; elija «En curado» para ver cuánto les falta."
              : "Los lotes se crean al cerrar el resumen del turno de una corrida."}
          </p>
        </EmptyState>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Lote</th>
                <th>Producto</th>
                <th>Corrida</th>
                <th>Fecha</th>
                <th>Estado</th>
                <th>Se puede liberar desde</th>
                <th>Ubicación actual</th>
                <th className="num">Unidades</th>
                <th className="num">Racks</th>
              </tr>
            </thead>
            <tbody>
              {lots.map((l) => (
                <LotRow key={`${l.lotId}:${l.version}`} lot={l} plant={plant} onDone={reload} />
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
      <Lots />
    </Suspense>
  );
}
