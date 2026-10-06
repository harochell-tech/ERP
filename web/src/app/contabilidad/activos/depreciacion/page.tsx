"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { FixedAssetTabs, lastEndedMonth, RUN_STATUS } from "@/components/FixedAssets";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// AF1-05 (E-AF1-05-5, E-AF1-03-1…6): the month's depreciation. The server's preview shows what each asset in service depreciates and where;
// the Contador posts it (step-up) from the month's last day, months in order. Below, the months posted; the latest can be undone while its
// period is open.

export default function Page() {
  const { companyId, can } = useSession();
  const [month, setMonth] = useState(lastEndedMonth().slice(0, 7));
  const preview = useLoad(
    can("ledger:read") && month
      ? () => query("/api/v1/companies/{companyId}/fixed-assets/depreciation-preview", { path: { companyId }, query: { month: `${month}-01` } })
      : null,
    [companyId, month],
  );
  const runs = useLoad(can("ledger:read") ? () => query("/api/v1/companies/{companyId}/fixed-assets/depreciation-runs", { path: { companyId } }) : null, [
    companyId,
  ]);
  const post = useCommand("fa-depreciation", "/api/v1/companies/{companyId}/fixed-assets/post-depreciation");
  const undo = useCommand("fa-depreciation-undo", "/api/v1/companies/{companyId}/fixed-assets/undo-depreciation");
  const [reason, setReason] = useState("");
  if (!can("ledger:read")) {
    return <NoPermission />;
  }
  const reload = () => {
    preview.reload();
    runs.reload();
  };
  const p = preview.data;
  const latest = runs.data?.items.find((r) => r.status === "POSTED");
  return (
    <>
      <h1>Depreciación del mes</h1>
      <FixedAssetTabs />
      <p className="muted">
        Cada activo en servicio se deprecia en línea recta: lo que le falta (costo − residual − acumulada) entre los meses que le quedan; el último mes cierra
        exacto.
      </p>
      <div className="inline-form">
        <Field label="Mes">
          <input type="month" aria-label="Mes a depreciar" value={month} onChange={(e) => setMonth(e.target.value)} />
        </Field>
      </div>
      {p === null ? (
        <LoadingIndicator error={preview.error} />
      ) : (
        <section data-testid="depreciation-preview">
          {p.alreadyPosted ? (
            <p className="notice">{p.month} ya está depreciado.</p>
          ) : p.skippedMonth ? (
            <p className="notice">Primero deprecie {p.skippedMonth}: los meses van en orden.</p>
          ) : !p.ended ? (
            <p className="notice">El mes se deprecia a partir de su último día ({formatDate(p.lastDay)}).</p>
          ) : null}
          {p.lines.length === 0 ? (
            <EmptyState title={`Ningún activo se deprecia en ${p.month}.`} />
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Activo</th>
                    <th>Descripción</th>
                    <th>Planta</th>
                    <th>Mes</th>
                    <th className="num">Depreciación</th>
                  </tr>
                </thead>
                <tbody>
                  {p.lines.map((l) => (
                    <tr key={l.assetId}>
                      <td>{l.assetNo}</td>
                      <td>{l.description}</td>
                      <td>{l.plantName}</td>
                      <td>
                        {l.monthNumber} de {l.usefulLifeMonths}
                      </td>
                      <td className="num">
                        <Money value={l.amount} />
                      </td>
                    </tr>
                  ))}
                </tbody>
                <tfoot>
                  <tr>
                    <th colSpan={4}>Total</th>
                    <th className="num">
                      <Money value={p.total} testId="depreciation-total" />
                    </th>
                  </tr>
                </tfoot>
              </table>
            </div>
          )}
          {can("fixed_asset:manage") ? (
            <div className="actions">
              <ConfirmAction
                label="Registrar depreciación"
                className="primary"
                stepUp
                busy={post.busy}
                disabled={!p.ended || p.alreadyPosted || Boolean(p.skippedMonth) || p.lines.length === 0}
                consequence={`Se registra la depreciación de ${p.month} el ${formatDate(p.lastDay)}: ${p.lines.length} activo(s).`}
                onConfirm={async () => (await post.run({ month: `${month}-01` }, undefined, `Depreciación de ${p.month} registrada.`)) && reload()}
              />
            </div>
          ) : null}
        </section>
      )}
      <ErrorBox error={post.error ?? undo.error} />
      <h2>Meses depreciados</h2>
      {runs.data === null ? (
        <LoadingIndicator error={runs.error} />
      ) : runs.data.items.length === 0 ? (
        <p className="muted">Todavía no se ha depreciado ningún mes.</p>
      ) : (
        <div className="table-wrap">
          <table data-testid="depreciation-runs">
            <thead>
              <tr>
                <th>Mes</th>
                <th className="num">Activos</th>
                <th className="num">Total</th>
                <th>Estado</th>
                <th>Registró</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {runs.data.items.map((r) => (
                <tr key={`${r.runId}:${r.version}`} data-testid={`run:${r.month}:${r.status}`}>
                  <td>{r.month}</td>
                  <td className="num">{r.assets}</td>
                  <td className="num">
                    <Money value={r.total} />
                  </td>
                  <td>
                    <StatusBadge status={r.status} label={RUN_STATUS[r.status]} />
                  </td>
                  <td>{r.postedBy ?? "—"}</td>
                  <td>
                    {latest?.runId === r.runId && can("fixed_asset:manage") ? (
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
                          consequence="Se reversa el asiento del mes y los activos vuelven a su depreciación anterior; el mes se puede registrar otra vez."
                          onConfirm={async () => {
                            if (
                              await undo.run(
                                { runId: r.runId, expectedVersion: r.version, reason: reason.trim() },
                                undefined,
                                `Depreciación de ${r.month} deshecha.`,
                              )
                            ) {
                              setReason("");
                              reload();
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
