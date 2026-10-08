"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { percent, STOPPAGE_REASONS } from "@/components/Efficiency";
import { LoadingIndicator } from "@/components/StateNotices";
import { Field, NoPermission } from "@/components/ui";
import { formatDecimal, formatQuantity } from "@/lib/decimal";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// MFG3-04 (E-MFG3-4…7): Producción › Eficiencia — each paired machine's availability, performance, quality and overall efficiency
// per shift and for the period (the server's figures), the minutes stopped by reason and the blocks they cost at standard cost.
// The reasons are written in the portal (Reporte de paros); «Sin razón» counts on Inicio.

function daysBefore(day: string, days: number): string {
  const d = new Date(`${day}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() - days);
  return d.toISOString().slice(0, 10);
}

function reasonText(reasons: { reason: string; count: number; minutes: string }[]): string {
  return reasons.length === 0 ? "—" : reasons.map((r) => `${STOPPAGE_REASONS[r.reason] ?? r.reason}: ${formatQuantity(r.minutes)} min (${r.count})`).join(" · ");
}

export default function Page() {
  const { companyId, can } = useSession();
  const today = todayInDominicanRepublic();
  const [from, setFrom] = useState(() => daysBefore(today, 6));
  const [to, setTo] = useState(today);
  const allowed = can("production:read");
  const { data, error } = useLoad(
    allowed && from && to ? () => query("/api/v1/companies/{companyId}/manufacturing/efficiency", { path: { companyId }, query: { from, to } }) : null,
    [companyId, from, to],
  );
  if (!allowed) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Eficiencia de las máquinas</h1>
      <p className="muted">
        Disponibilidad = tiempo en marcha ÷ tiempo planificado (sin mantenimientos). Rendimiento = ciclos × ciclo ideal ÷ tiempo en marcha. Calidad = buenas ÷ (buenas + merma).
        Eficiencia general = las tres multiplicadas. Los bloques perdidos son los que no se hicieron durante los paros, valorados a costo estándar.
      </p>
      <div className="inline-form">
        <Field label="Desde">
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label="Hasta">
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </Field>
      </div>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : (
        <>
          {data.missingIdealCycles.length > 0 ? (
            <p className="notice" data-testid="missing-ideal-cycles">
              Falta el ciclo ideal de: {data.missingIdealCycles.join(", ")}. Fíjelo en Producción › Máquinas y turnos para calcular el rendimiento.
            </p>
          ) : null}
          {data.stoppagesWithoutReason > 0 ? (
            <p className="notice" data-testid="stoppages-without-reason">
              {data.stoppagesWithoutReason} paro(s) sin razón en el período: escríbala en el portal (Reporte de paros).
            </p>
          ) : null}
          <h2>Por máquina</h2>
          <div className="table-wrap">
            <table data-testid="efficiency-machines">
              <thead>
                <tr>
                  <th>Máquina</th>
                  <th className="num">Planificado (min)</th>
                  <th className="num">Paros (min)</th>
                  <th className="num">Disponibilidad</th>
                  <th className="num">Rendimiento</th>
                  <th className="num">Calidad</th>
                  <th className="num">Eficiencia general</th>
                  <th className="num">Bloques perdidos</th>
                  <th className="num">Costo (RD$)</th>
                  <th>Paros por razón</th>
                </tr>
              </thead>
              <tbody>
                {data.machines.map((m) => (
                  <tr key={m.machineId}>
                    <td>
                      {m.machineCode} — {m.machineName}
                    </td>
                    <td className="num">{formatQuantity(m.plannedMinutes)}</td>
                    <td className="num">{formatQuantity(m.stoppageMinutes)}</td>
                    <td className="num">{percent(m.availability)}</td>
                    <td className="num">{percent(m.performance)}</td>
                    <td className="num">{percent(m.quality)}</td>
                    <td className="num">
                      <strong>{percent(m.oee)}</strong>
                    </td>
                    <td className="num">{m.lostUnits ? formatQuantity(m.lostUnits) : "—"}</td>
                    <td className="num">{m.lostValue ? formatDecimal(m.lostValue) : "—"}</td>
                    <td className="wrap">{reasonText(m.reasons)}</td>
                  </tr>
                ))}
                {data.machines.length === 0 ? (
                  <tr>
                    <td colSpan={10} className="muted">
                      Sin lecturas del portal en el período.
                    </td>
                  </tr>
                ) : null}
              </tbody>
            </table>
          </div>
          <h2>Por turno</h2>
          <div className="table-wrap">
            <table data-testid="efficiency-shifts">
              <thead>
                <tr>
                  <th>Fecha</th>
                  <th>Turno</th>
                  <th>Máquina</th>
                  <th className="num">Ciclos</th>
                  <th className="num">Mantenimiento (min)</th>
                  <th className="num">Paros (min)</th>
                  <th className="num">Disponibilidad</th>
                  <th className="num">Rendimiento</th>
                  <th className="num">Calidad</th>
                  <th className="num">Eficiencia general</th>
                  <th className="num">Bloques perdidos</th>
                </tr>
              </thead>
              <tbody>
                {data.shifts.map((s) => (
                  <tr key={`${s.date}:${s.shiftNo}:${s.machineId}`}>
                    <td>{formatDate(s.date)}</td>
                    <td>{s.shiftNo}</td>
                    <td>{s.machineCode}</td>
                    <td className="num">{s.cycles}</td>
                    <td className="num">{formatQuantity(s.maintenanceMinutes)}</td>
                    <td className="num">
                      {formatQuantity(s.stoppageMinutes)}
                      {s.stoppagesWithoutReason > 0 ? <span className="badge tone-attention"> {s.stoppagesWithoutReason} sin razón</span> : null}
                    </td>
                    <td className="num">{percent(s.availability)}</td>
                    <td className="num">{percent(s.performance)}</td>
                    <td className="num">{percent(s.quality)}</td>
                    <td className="num">
                      <strong>{percent(s.oee)}</strong>
                    </td>
                    <td className="num">{s.lostUnits ? formatQuantity(s.lostUnits) : "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </>
  );
}
