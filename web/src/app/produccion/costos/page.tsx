"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { PlantSelect, ProductionBadge, useChosenPlant, usePlants } from "@/components/Production";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission } from "@/components/ui";
import { todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { monthEnded, monthName, previousMonth } from "@/lib/ux4bProduction";

// MFG1-07 (E-MFG1-07-5): the monthly cost collectors (WIP per plant and product) — the Controller settles an ended month's
// collector (P-13: usage and price variances). Amounts are shown as the API sends them.
// UX4-03: plain words (E-UX4-14: "Costo acumulado en proceso", no "colector"), the month opens on the previous one (P-40), "Liquidar"
// only for an ended month, RD$ and what "—" means (P-41).

function Settle({ collector, onDone }: { collector: Schemas["CostCollectorView"]; onDone: () => void }) {
  const month = monthName(collector.periodMonth);
  const settle = useCommand(
    `settle-cost-collector:${collector.collectorId}`,
    "/api/v1/companies/{companyId}/manufacturing/settle-cost-collector",
    `Costos de ${collector.itemCode} de ${month} liquidados.`,
  );
  return (
    <>
      <ConfirmAction
        label="Liquidar"
        title={`¿Liquidar los costos de ${collector.itemCode} de ${month}?`}
        consequence="Se cierran los costos del mes de este producto: el costo acumulado en proceso se reparte entre la variación de uso y la de precio y se registra en contabilidad. No se puede deshacer."
        stepUp
        busy={settle.busy}
        onConfirm={async () => (await settle.run({ plantId: collector.plantId, collectorId: collector.collectorId, expectedVersion: collector.version })) && onDone()}
      />
      <ErrorBox error={settle.error} />
    </>
  );
}

export default function Page() {
  const { companyId, can, plantName } = useSession();
  const plants = usePlants();
  const { plant, setPlant } = useChosenPlant(plants.data);
  const plantId = plant?.plantId ?? "";
  const today = todayInDominicanRepublic();
  const [month, setMonth] = useState(() => previousMonth(today));
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
    return <LoadingIndicator error={plants.error} />;
  }
  return (
    <>
      <h1>Costos de producción</h1>
      <p className="muted">
        Cada mes el sistema acumula el costo de lo producido por producto (materiales y conversión). Al terminar el mes, el Controller lo
        liquida: la diferencia contra el costo estándar se reparte entre la variación de uso (se usó más o menos material) y la de precio.
      </p>
      {plants.data.length === 0 ? <EmptyState title="No hay plantas con producción." /> : <PlantSelect plants={plants.data} value={plantId} onChange={setPlant} />}
      <Field label="Mes" hint="Vacío: todos los meses.">
        <input type="month" value={month} max={today.slice(0, 7)} onChange={(e) => setMonth(e.target.value)} />
      </Field>
      <Field label="Estado">
        <select aria-label="Estado" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">Todos</option>
          <option value="OPEN">Abierto</option>
          <option value="SETTLED">Liquidado</option>
        </select>
      </Field>
      {data === null ? (
        plantId ? <LoadingIndicator error={error} /> : null
      ) : data.items.length === 0 ? (
        <EmptyState title={month ? `No hay costos de producción de ${monthName(`${month}-01`)}.` : "No hay costos de producción."}>
          <p>Se acumulan al cerrar los resúmenes de turno de las corridas del mes. Elija otro mes o deje el mes vacío para verlos todos.</p>
        </EmptyState>
      ) : (
        <>
        <p className="muted">Montos en RD$. «—» en las variaciones: se calculan al liquidar el mes. «Liquidar» aparece cuando el mes terminó.</p>
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Planta</th>
              <th>Producto</th>
              <th>Mes</th>
              <th>Estado</th>
              <th className="num">Corridas</th>
              <th className="num">Costo acumulado en proceso (RD$)</th>
              <th className="num">Variación de uso (RD$)</th>
              <th className="num">Variación de precio (RD$)</th>
              <th>Acciones</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((c) => (
              <tr key={`${c.collectorId}:${c.version}`}>
                <td>{plantName(c.plantId, c.plantCode)}</td>
                <td>{c.itemCode}</td>
                <td>{monthName(c.periodMonth)}</td>
                <td>
                  <ProductionBadge kind="collector" status={c.status} />
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
                <td className="actions">
                  {c.status === "OPEN" && can("cost_collector:settle") ? (
                    monthEnded(c.periodMonth, today) ? (
                      <Settle collector={c} onDone={reload} />
                    ) : (
                      <span className="muted">Se liquida al terminar el mes</span>
                    )
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table></div>
        </>
      )}
    </>
  );
}
