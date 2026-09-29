"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { PlantSelect, useChosenPlant, usePlants } from "@/components/Production";
import { ErrorBox, Field, Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// MFG1-07 (E-MFG1-07-5): the monthly cost collectors (WIP per plant and product) — the Controller settles an ended month's
// collector (P-13: usage and price variances). Amounts are shown as the API sends them.

function Settle({ collector, onDone }: { collector: Schemas["CostCollectorView"]; onDone: () => void }) {
  const settle = useCommand(`settle-cost-collector:${collector.collectorId}`, "/api/v1/companies/{companyId}/manufacturing/settle-cost-collector");
  return (
    <>
      <button
        type="button"
        disabled={settle.busy}
        onClick={async () => (await settle.run({ plantId: collector.plantId, collectorId: collector.collectorId, expectedVersion: collector.version })) && onDone()}
      >
        Liquidar
      </button>
      <ErrorBox error={settle.error} />
    </>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const plants = usePlants();
  const { plant, setPlant } = useChosenPlant(plants.data);
  const plantId = plant?.plantId ?? "";
  const [month, setMonth] = useState("");
  const [status, setStatus] = useState("");
  const { data, error, reload } = useLoad(
    can("production:read") && plantId
      ? () =>
          query("/api/v1/companies/{companyId}/manufacturing/cost-collectors", {
            path: { companyId },
            query: { plantId, month: month ? `${month}-01` : undefined, status },
          })
      : null,
    [companyId, plantId, month, status],
  );
  if (!can("production:read")) {
    return <NoPermission />;
  }
  if (plants.data === null) {
    return <Loading error={plants.error} />;
  }
  return (
    <>
      <h1>Costos de producción</h1>
      {plants.data.length === 0 ? <p className="muted">No hay plantas con producción.</p> : <PlantSelect plants={plants.data} value={plantId} onChange={setPlant} />}
      <Field label="Mes">
        <input type="month" value={month} max={todayInDominicanRepublic().slice(0, 7)} onChange={(e) => setMonth(e.target.value)} />
      </Field>
      <Field label="Estado">
        <select aria-label="Estado" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">Todos</option>
          <option value="OPEN">Abierto</option>
          <option value="SETTLED">Liquidado</option>
        </select>
      </Field>
      {data === null ? (
        plantId ? <Loading error={error} /> : null
      ) : data.items.length === 0 ? (
        <p className="muted">No hay colectores de costos.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Planta</th>
              <th>Producto</th>
              <th>Mes</th>
              <th>Estado</th>
              <th className="num">Corridas</th>
              <th className="num">Saldo en proceso</th>
              <th className="num">Variación de uso</th>
              <th className="num">Variación de precio</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.items.map((c) => (
              <tr key={`${c.collectorId}:${c.version}`}>
                <td>{c.plantCode}</td>
                <td>{c.itemCode}</td>
                <td className="mono">{c.periodMonth.slice(0, 7)}</td>
                <td>
                  <StatusBadge status={c.status} />
                </td>
                <td className="num">{c.runs}</td>
                <td className="num">
                  <Money value={c.wipBalance} />
                </td>
                <td className="num">
                  <Money value={c.usageVariance} />
                </td>
                <td className="num">
                  <Money value={c.priceVariance} />
                </td>
                <td className="actions">{c.status === "OPEN" && can("cost_collector:settle") ? <Settle collector={c} onDone={reload} /> : null}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
