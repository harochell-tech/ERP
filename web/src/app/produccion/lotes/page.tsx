"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { PlantSelect, useChosenPlant, usePlants, type PlantOption } from "@/components/Production";
import { ConfirmAction, ConfirmDialog, ErrorBox, Field, Loading, NoPermission, ReasonAction, RowActions, StatusBadge, useFieldErrors } from "@/components/ui";
import { formatQuantity, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { scrapLocations, stockLocations } from "@/lib/production";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Lot = Schemas["FgLotSummary"];

// MFG1-07 (E-MFG1-07-3): finished-goods lots in curing — Calidad releases a cured lot to a stock location, or blocks / unblocks it
// with a reason; the Gerente de planta scraps units (step-up, handled by useCommand). The API decides whether curing is done.

function Release({ lot, plant, onDone }: { lot: Lot; plant: PlantOption | undefined; onDone: () => void }) {
  const release = useCommand(`release-lot:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/release-lot");
  const targets = stockLocations(plant?.locations ?? []);
  const [toLocationId, setToLocationId] = useState(targets[0]?.locationId ?? "");
  const target = targets.find((l) => l.locationId === toLocationId);
  return (
    <span className="inline-form">
      <select aria-label={`Liberar ${lot.lotCode} a`} value={toLocationId} onChange={(e) => setToLocationId(e.target.value)}>
        <option value="">—</option>
        {targets.map((l) => (
          <option key={l.locationId} value={l.locationId}>
            {l.code}
          </option>
        ))}
      </select>
      <ConfirmAction
        label="Liberar"
        title={`¿Liberar el lote ${lot.lotCode}?`}
        consequence={`Las ${formatQuantity(lot.quantity)} unidades del lote pasan de curado a ${target?.code ?? "la ubicación elegida"} y quedan disponibles para despacho. No se puede deshacer.`}
        disabled={!toLocationId}
        busy={release.busy}
        onConfirm={async () =>
          (await release.run(
            { plantId: lot.plantId, lotId: lot.lotId, expectedVersion: lot.version, toLocationId },
            undefined,
            `Lote ${lot.lotCode} liberado a ${target?.code ?? "la ubicación elegida"}.`,
          )) && onDone()
        }
      />
      <ErrorBox error={release.error} />
    </span>
  );
}

function Scrap({ lot, plant, onDone }: { lot: Lot; plant: PlantOption | undefined; onDone: () => void }) {
  const scrap = useCommand(`scrap-lot:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/scrap-lot");
  const sources = scrapLocations(plant?.locations ?? []);
  const [open, setOpen] = useState(false);
  const [form, setForm] = useState(() => ({
    locationId: sources.find((l) => l.code === lot.locationCode)?.locationId ?? "",
    quantity: "",
    reason: "",
  }));
  const fe = useFieldErrors<"locationId" | "quantity" | "reason">();
  const quantity = normalizeInput(form.quantity);
  return (
    <>
      <button type="button" className="danger" disabled={scrap.busy} onClick={() => setOpen(true)}>
        Desechar unidades
      </button>
      <ConfirmDialog
        open={open}
        title={`¿Desechar unidades del lote ${lot.lotCode}?`}
        confirmLabel="Confirmar scrap"
        danger
        stepUp
        busy={scrap.busy}
        onCancel={() => setOpen(false)}
        onConfirm={async () => {
          const valid = fe.check({
            locationId: !form.locationId && "Elija la ubicación de donde salen las unidades.",
            quantity: !isPositiveDecimal(quantity, 6) && "Unidades mayores que cero (hasta 6 decimales).",
            reason: form.reason.trim() === "" && "Indique el motivo.",
          });
          if (!valid) {
            return;
          }
          setOpen(false);
          if (
            await scrap.run(
              { plantId: lot.plantId, lotId: lot.lotId, locationId: form.locationId, quantity, reason: form.reason.trim() },
              form,
              `${formatQuantity(quantity)} unidades del lote ${lot.lotCode} desechadas.`,
            )
          ) {
            onDone();
          }
        }}
      >
        <p>Las unidades salen del inventario y se contabiliza la pérdida. No se puede deshacer.</p>
        <Field label="Ubicación" required error={fe.errors.locationId}>
          <select aria-label={`Ubicación del scrap ${lot.lotCode}`} value={form.locationId} onChange={(e) => setForm({ ...form, locationId: e.target.value })}>
            <option value="">—</option>
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
        <Field label="Motivo" required error={fe.errors.reason} wide>
          <input aria-label={`Motivo del scrap ${lot.lotCode}`} value={form.reason} onChange={(e) => setForm({ ...form, reason: e.target.value })} />
        </Field>
      </ConfirmDialog>
      <ErrorBox error={scrap.error} />
    </>
  );
}

function LotRow({ lot, plant, onDone }: { lot: Lot; plant: PlantOption | undefined; onDone: () => void }) {
  const { can } = useSession();
  const block = useCommand(`block-lot:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/block-lot", `Lote ${lot.lotCode} bloqueado.`);
  const unblock = useCommand(`unblock-lot:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/unblock-lot", `Lote ${lot.lotCode} desbloqueado; vuelve a curado.`);
  const target = { plantId: lot.plantId, lotId: lot.lotId, expectedVersion: lot.version };
  const busy = block.busy || unblock.busy;
  const scrappable = lot.status === "CURING" || lot.status === "BLOCKED" || lot.status === "RELEASED";
  const actions = (
    <>
      {lot.status === "CURING" && can("fg_lot:release") ? (
        <>
          <Release lot={lot} plant={plant} onDone={onDone} />
          <ReasonAction
            label="Bloquear"
            consequence={`El lote ${lot.lotCode} queda bloqueado: no se libera ni se despacha hasta que Calidad lo desbloquee.`}
            busy={busy}
            onConfirm={async (reason) => (await block.run({ ...target, reason })) && onDone()}
          />
        </>
      ) : null}
      {lot.status === "BLOCKED" && can("fg_lot:release") ? (
        <ReasonAction
          label="Desbloquear"
          consequence={`El lote ${lot.lotCode} vuelve a curado y se podrá liberar.`}
          busy={busy}
          onConfirm={async (reason) => (await unblock.run({ ...target, reason })) && onDone()}
        />
      ) : null}
      {scrappable && can("fg_lot:scrap") ? <Scrap lot={lot} plant={plant} onDone={onDone} /> : null}
    </>
  );
  const buttonCount = (lot.status === "CURING" && can("fg_lot:release") ? 2 : 0) + (lot.status === "BLOCKED" && can("fg_lot:release") ? 1 : 0) + (scrappable && can("fg_lot:scrap") ? 1 : 0);
  return (
    <tr>
      <td className="mono">{lot.lotCode}</td>
      <td>{lot.itemCode}</td>
      <td className="mono">{lot.runNo}</td>
      <td>{formatDate(lot.businessDate)}</td>
      <td className="wrap">
        <StatusBadge status={lot.status} testId={`lot-status-${lot.lotCode}`} />
        {lot.blockReason ? <div className="muted">{lot.blockReason}</div> : null}
      </td>
      <td>
        {formatDateTime(lot.releasableAt)}
        {lot.status === "CURING" ? <div className="muted">{lot.curingDone ? "Curado cumplido" : "En curado mínimo"}</div> : null}
      </td>
      <td>{lot.locationCode ?? "—"}</td>
      <td className="num">{formatQuantity(lot.quantity)}</td>
      <td className="num">{lot.racks}</td>
      <td className="wrap">
        {buttonCount >= 3 ? <RowActions>{actions}</RowActions> : <div className="actions">{actions}</div>}
        <ErrorBox error={block.error ?? unblock.error} />
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const plants = usePlants();
  const { plant, setPlant } = useChosenPlant(plants.data);
  const plantId = plant?.plantId ?? "";
  const [status, setStatus] = useState("");
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
    return <Loading error={plants.error} />;
  }
  return (
    <>
      <h1>Curado y liberación</h1>
      {plants.data.length === 0 ? <p className="muted">No hay plantas con producción.</p> : <PlantSelect plants={plants.data} value={plantId} onChange={setPlant} />}
      <Field label="Estado">
        <select aria-label="Estado" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">Todos</option>
          <option value="CURING">En curado</option>
          <option value="BLOCKED">Bloqueado</option>
          <option value="RELEASED">Liberado</option>
          <option value="SCRAPPED">Desechado</option>
          <option value="VOIDED">Anulado</option>
        </select>
      </Field>
      {data === null ? (
        plantId ? <Loading error={error} /> : null
      ) : data.items.length === 0 ? (
        <p className="muted">No hay lotes.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Lote</th>
              <th>Producto</th>
              <th>Corrida</th>
              <th>Fecha</th>
              <th>Estado</th>
              <th>Liberable desde</th>
              <th>Ubicación</th>
              <th className="num">Unidades</th>
              <th className="num">Racks</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.items.map((l) => (
              <LotRow key={`${l.lotId}:${l.version}`} lot={l} plant={plant} onDone={reload} />
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
