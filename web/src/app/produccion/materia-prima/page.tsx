"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, NoPermission } from "@/components/ui";
import { formatDecimal, formatQuantity } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-02c (E-VS3-02c-1…4): Producción › Materia prima en negativo — what production consumed without stock while the PRODUCTION policy
// allows it: the balance per material and location (stock − what is still to cover), each shortfall with what the receipts and the
// opening already covered, and the reason that lets the month's inventory close while one waits for its material.

const STATUS: Readonly<Record<string, string>> = { OPEN: "Por regularizar", SETTLED: "Regularizado", CANCELLED: "Anulado" };

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("production:read");
  const list = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/manufacturing/material-shortfalls", { path: { companyId } }) : null, [companyId]);
  const justify = useCommand("justify-shortfall", "/api/v1/companies/{companyId}/manufacturing/justify-material-shortfall", "Justificación guardada.");
  const [editing, setEditing] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  if (!allowed) {
    return <NoPermission />;
  }
  const data = list.data;
  return (
    <>
      <h1>Materia prima en negativo</h1>
      <p className="muted">
        Lo que producción consumió sin existencia queda por regularizar a su precio estándar; la próxima compra recibida o la apertura de ese material en esa ubicación lo tapa
        primero. Mientras quede algo abierto, el cierre de inventario del mes espera, salvo que el faltante esté justificado.
      </p>
      {data === null ? (
        <LoadingIndicator error={list.error} />
      ) : (
        <>
          <p className={data.allowed ? "muted" : "notice"} data-testid="shortfall-switch">
            {data.allowed
              ? "Hoy la política de Producción permite registrar producción aunque falte materia prima."
              : "Hoy la política de Producción no permite consumir materia prima sin existencia: un resumen de turno sin material suficiente no se registra."}
          </p>
          <h2>Existencia por material</h2>
          <div className="table-wrap">
            <table data-testid="shortfall-balances">
              <thead>
                <tr>
                  <th>Planta</th>
                  <th>Ubicación</th>
                  <th>Material</th>
                  <th className="num">Existencia</th>
                  <th className="num">Por regularizar</th>
                  <th className="num">Saldo</th>
                </tr>
              </thead>
              <tbody>
                {data.balances.map((b) => (
                  <tr key={`${b.plantCode}:${b.locationCode}:${b.materialCode}`}>
                    <td>{b.plantCode}</td>
                    <td>{b.locationCode}</td>
                    <td>
                      {b.materialCode} — {b.materialName}
                    </td>
                    <td className="num">
                      {formatQuantity(b.stock)} {b.uom}
                    </td>
                    <td className="num">
                      {formatQuantity(b.open)} {b.uom}
                    </td>
                    <td className="num">
                      <strong>
                        {formatQuantity(b.balance)} {b.uom}
                      </strong>
                    </td>
                  </tr>
                ))}
                {data.balances.length === 0 ? (
                  <tr>
                    <td colSpan={6} className="muted">
                      Ningún material en negativo.
                    </td>
                  </tr>
                ) : null}
              </tbody>
            </table>
          </div>
          <h2>
            Faltantes ({data.openCount} por regularizar, RD$ {formatDecimal(data.openValue)})
          </h2>
          <ErrorBox error={justify.error} />
          <div className="table-wrap">
            <table data-testid="shortfalls">
              <thead>
                <tr>
                  <th>Fecha</th>
                  <th>Corrida</th>
                  <th>Material</th>
                  <th>Ubicación</th>
                  <th className="num">Faltó</th>
                  <th className="num">Regularizado</th>
                  <th className="num">Valor (RD$)</th>
                  <th>Estado</th>
                  <th>Justificación</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((f) => (
                  <tr key={f.shortfallId}>
                    <td>{formatDate(f.businessDate)}</td>
                    <td>{f.runNo ?? "—"}</td>
                    <td>{f.materialCode}</td>
                    <td>{f.locationCode}</td>
                    <td className="num">
                      {formatQuantity(f.qty)} {f.uom}
                    </td>
                    <td className="num">{formatQuantity(f.settledQty)}</td>
                    <td className="num">{formatDecimal(f.value)}</td>
                    <td>{STATUS[f.status] ?? f.status}</td>
                    <td className="wrap">
                      {editing === f.shortfallId ? (
                        <form
                          className="inline-form"
                          onSubmit={async (e) => {
                            e.preventDefault();
                            if (await justify.run({ shortfallId: f.shortfallId, reason: reason.trim() })) {
                              setEditing(null);
                              setReason("");
                              list.reload();
                            }
                          }}
                        >
                          <input aria-label="Justificación" value={reason} required maxLength={500} placeholder="Ej. llega el cemento del pedido 0045 el lunes" onChange={(e) => setReason(e.target.value)} />
                          <button type="submit" className="primary" disabled={justify.busy}>
                            Guardar
                          </button>
                          <button type="button" onClick={() => setEditing(null)}>
                            Cancelar
                          </button>
                        </form>
                      ) : (
                        <>
                          {f.justification ?? (f.status === "OPEN" ? <span className="badge tone-attention">Bloquea el cierre</span> : "—")}
                          {f.status === "OPEN" && can("shift_summary:post") ? (
                            <>
                              {" "}
                              <button
                                type="button"
                                onClick={() => {
                                  setEditing(f.shortfallId);
                                  setReason(f.justification ?? "");
                                }}
                              >
                                {f.justification ? "Cambiar" : "Justificar"}
                              </button>
                            </>
                          ) : null}
                        </>
                      )}
                    </td>
                  </tr>
                ))}
                {data.items.length === 0 ? (
                  <tr>
                    <td colSpan={9} className="muted">
                      Sin faltantes en los últimos 31 días.
                    </td>
                  </tr>
                ) : null}
              </tbody>
            </table>
          </div>
        </>
      )}
    </>
  );
}
