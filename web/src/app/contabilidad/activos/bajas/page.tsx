"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { DISPOSAL_KIND, DISPOSAL_STATUS, FixedAssetTabs } from "@/components/FixedAssets";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { formatDecimal } from "@/lib/decimal";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// AF1-05 (E-AF1-05-6, E-AF-7, E-AF1-03-7…10): the disposals. The Controller (not the preparer) sees the book value and the gain or loss the
// server computes and approves (step-up), posting P-45 on the disposal date; the Contador cancels a draft. A sale's price stays in «Venta de
// activos por cobrar» until it is invoiced and collected.

export default function Page() {
  const { companyId, can, isMine } = useSession();
  const [status, setStatus] = useState("DRAFT");
  const list = useLoad(
    can("ledger:read")
      ? () => query("/api/v1/companies/{companyId}/fixed-assets/disposals", { path: { companyId }, query: { status: status || undefined } })
      : null,
    [companyId, status],
  );
  const approve = useCommand("fa-disposal-approve", "/api/v1/companies/{companyId}/fixed-assets/approve-asset-disposal");
  const cancel = useCommand("fa-disposal-cancel", "/api/v1/companies/{companyId}/fixed-assets/cancel-asset-disposal");
  if (!can("ledger:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Bajas de activos</h1>
      <FixedAssetTabs />
      <p className="muted">
        La baja se prepara desde la ficha del activo. Al aprobarla, sale su costo y su depreciación acumulada; el precio de una venta queda en «Venta de activos
        por cobrar» hasta que se factura y se cobra.
      </p>
      <div className="inline-form">
        <Field label="Estado">
          <select aria-label="Estado" value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Todas</option>
            {Object.entries(DISPOSAL_STATUS).map(([code, label]) => (
              <option key={code} value={code}>
                {label}
              </option>
            ))}
          </select>
        </Field>
      </div>
      <ErrorBox error={approve.error ?? cancel.error} />
      {list.data === null ? (
        <LoadingIndicator error={list.error} />
      ) : list.data.items.length === 0 ? (
        <EmptyState title="No hay bajas con ese estado." />
      ) : (
        <div className="table-wrap">
          <table data-testid="disposals">
            <thead>
              <tr>
                <th>Activo</th>
                <th>Tipo</th>
                <th>Fecha</th>
                <th className="num">Precio</th>
                <th className="num">Valor en libros</th>
                <th className="num">Ganancia</th>
                <th className="num">Pérdida</th>
                <th>Motivo</th>
                <th>Estado</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {list.data.items.map((d) => (
                <tr key={`${d.disposalId}:${d.version}`} data-testid={`disposal:${d.assetNo}`}>
                  <td>
                    <Link href={`/contabilidad/activo/?id=${d.assetId}`}>{d.assetNo}</Link> {d.description}
                  </td>
                  <td>{DISPOSAL_KIND[d.kind] ?? d.kind}</td>
                  <td>{formatDate(d.disposalDate)}</td>
                  <td className="num">{d.price ? <Money value={d.price} /> : "—"}</td>
                  <td className="num">
                    <Money value={d.bookValue} />
                  </td>
                  <td className="num">
                    <Money value={d.gain} />
                  </td>
                  <td className="num">
                    <Money value={d.loss} testId={`disposal-loss:${d.assetNo}`} />
                  </td>
                  <td>{d.reason}</td>
                  <td>
                    <StatusBadge status={d.status} label={DISPOSAL_STATUS[d.status]} />
                  </td>
                  <td>
                    {d.status === "DRAFT" ? (
                      <div className="actions row-buttons">
                        {can("fixed_asset:approve") && !isMine(d.preparedBy) ? (
                          <ConfirmAction
                            label="Aprobar baja"
                            className="primary"
                            stepUp
                            busy={approve.busy}
                            consequence={`Se contabiliza la baja de ${d.assetNo} el ${formatDate(d.disposalDate)}: valor en libros RD$ ${formatDecimal(d.bookValue)}, ${
                              d.loss !== "0.00" ? `pérdida RD$ ${formatDecimal(d.loss)}` : `ganancia RD$ ${formatDecimal(d.gain)}`
                            }.`}
                            onConfirm={async () =>
                              (await approve.run({ disposalId: d.disposalId, expectedVersion: d.version }, undefined, `Baja de ${d.assetNo} contabilizada.`)) &&
                              list.reload()
                            }
                          />
                        ) : null}
                        {can("fixed_asset:manage") ? (
                          <ConfirmAction
                            label="Cancelar"
                            danger
                            busy={cancel.busy}
                            consequence="La baja preparada se cancela; el activo sigue en servicio."
                            onConfirm={async () =>
                              (await cancel.run({ disposalId: d.disposalId, expectedVersion: d.version }, undefined, `Baja de ${d.assetNo} cancelada.`)) &&
                              list.reload()
                            }
                          />
                        ) : null}
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
