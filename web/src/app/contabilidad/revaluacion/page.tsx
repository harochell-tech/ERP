"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// USD1-07b (E-USD1-07-6, E-USD1-06-1…3): Contabilidad › Revaluación de saldos en dólares. The Contador or the Controller revalues a month that
// has ended: its open USD payables and USD bank accounts at the approved rate of its last day, the unrealized difference on that day and
// its opposite the next. One per month; undone (both journals reversed) to redo it.

const STATUS: Readonly<Record<string, string>> = { POSTED: "Registrada", UNDONE: "Deshecha" };

/** The last month that has ended, as YYYY-MM. */
function lastMonth(): string {
  const today = todayInDominicanRepublic();
  const year = Number(today.slice(0, 4));
  const month = Number(today.slice(5, 7));
  return month === 1 ? `${year - 1}-12` : `${year}-${String(month - 1).padStart(2, "0")}`;
}

export default function Page() {
  const { companyId, can } = useSession();
  const list = useLoad(can("exchange_rate:read") ? () => query("/api/v1/companies/{companyId}/finance/fx-revaluations", { path: { companyId } }) : null, [
    companyId,
  ]);
  const post = useCommand("post-fx-revaluation", "/api/v1/companies/{companyId}/finance/post-fx-revaluation");
  const undo = useCommand("undo-fx-revaluation", "/api/v1/companies/{companyId}/finance/undo-fx-revaluation");
  const [month, setMonth] = useState(lastMonth());
  const [reason, setReason] = useState("");
  if (!can("exchange_rate:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Revaluación de saldos en dólares</h1>
      <p className="muted">
        Al cierre de cada mes las cuentas por pagar y los bancos en dólares se valoran a la tasa del último día; la diferencia es no realizada y se reversa el
        día siguiente.
      </p>
      {can("fx_revaluation:post") ? (
        <div className="actions">
          <Field label="Mes">
            <input type="month" aria-label="Mes a revaluar" value={month} onChange={(e) => setMonth(e.target.value)} />
          </Field>
          <ConfirmAction
            label="Revaluar"
            className="primary"
            stepUp
            busy={post.busy}
            disabled={!month}
            consequence={`Se registra la revaluación de ${month} a la tasa aprobada de su último día y su reversa el primer día del mes siguiente.`}
            onConfirm={async () => (await post.run({ month: `${month}-01` }, undefined, `Saldos en dólares de ${month} revaluados.`)) && list.reload()}
          />
        </div>
      ) : null}
      <ErrorBox error={post.error ?? undo.error} />
      {list.data === null ? (
        <LoadingIndicator error={list.error} />
      ) : list.data.items.length === 0 ? (
        <p className="muted">Todavía no hay revaluaciones.</p>
      ) : (
        <div className="table-wrap">
          <table data-testid="revaluations">
            <thead>
              <tr>
                <th>Mes</th>
                <th>Tasa</th>
                <th>Tasa del</th>
                <th className="num">Pérdida (+) / ganancia (−) no realizada (RD$)</th>
                <th>Estado</th>
                <th>Registró</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {list.data.items.map((r) => (
                <tr key={`${r.revaluationId}:${r.version}`} data-testid={`revaluation:${r.month}:${r.status}`}>
                  <td>{r.month}</td>
                  <td className="mono">{r.rate}</td>
                  <td>{formatDate(r.rateDate)}</td>
                  <td className="num">
                    <Money value={r.loss} />
                  </td>
                  <td>
                    <StatusBadge status={r.status} label={STATUS[r.status]} />
                  </td>
                  <td>{r.postedBy ?? "—"}</td>
                  <td>
                    {r.status === "POSTED" && can("fx_revaluation:post") ? (
                      <div className="actions row-buttons">
                        <input
                          aria-label={`Motivo para deshacer ${r.month}`}
                          placeholder="Motivo (10+ caracteres)"
                          value={reason}
                          onChange={(e) => setReason(e.target.value)}
                        />
                        <ConfirmAction
                          label="Deshacer"
                          danger
                          stepUp
                          busy={undo.busy}
                          disabled={reason.trim().length < 10}
                          consequence="Se reversan los dos asientos de la revaluación; el mes se puede revaluar otra vez."
                          onConfirm={async () => {
                            if (
                              await undo.run(
                                { revaluationId: r.revaluationId, expectedVersion: r.version, reason: reason.trim() },
                                undefined,
                                `Revaluación de ${r.month} deshecha.`,
                              )
                            ) {
                              setReason("");
                              list.reload();
                            }
                          }}
                        />
                      </div>
                    ) : null}
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
