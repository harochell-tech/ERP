"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { FixedAssetTabs, LOAD_STATUS, lastMonthEnd } from "@/components/FixedAssets";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { previewQuery } from "@/lib/ux4b";

// AF1-05 (E-AF1-05-7, E-AF-8, E-AF1-04-1…6): the assets the company already has, loaded once from a CSV or Excel file at a cut-off (a
// month's last day). The server's preview shows each row's months depreciated and monthly amount, or its error; one error loads nothing.
// The Contador prepares; the Controller approves (P-46 against the opening balances) or reverses a load not yet depreciated.

const TEMPLATE =
  "Código,Descripción,Categoría,Planta,Fecha de compra,Costo,Depreciación acumulada\nCAM-001,Camión Volvo FMX 2023,CAMIONES,MATILLA,2023-06-15,2400000.00,960000.00\n";

function toBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ""));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

export default function Page() {
  const { companyId, can, isMine } = useSession();
  const loads = useLoad(can("ledger:read") ? () => query("/api/v1/companies/{companyId}/fixed-assets/loads", { path: { companyId } }) : null, [companyId]);
  const prepare = useCommand("fa-load-prepare", "/api/v1/companies/{companyId}/fixed-assets/prepare-asset-load");
  const approve = useCommand("fa-load-approve", "/api/v1/companies/{companyId}/fixed-assets/approve-asset-load");
  const discard = useCommand("fa-load-discard", "/api/v1/companies/{companyId}/fixed-assets/discard-asset-load");
  const reverse = useCommand("fa-load-reverse", "/api/v1/companies/{companyId}/fixed-assets/reverse-asset-load");
  const [cutoff, setCutoff] = useState(lastMonthEnd());
  const [upload, setUpload] = useState<{ fileName: string; contentBase64: string } | null>(null);
  const [preview, setPreview] = useState<Schemas["AssetLoadPreview"] | null>(null);
  const [previewError, setPreviewError] = useState<unknown>(null);
  if (!can("ledger:read")) {
    return <NoPermission />;
  }
  const check = async (next: { fileName: string; contentBase64: string }, date: string) => {
    setPreview(null);
    setPreviewError(null);
    try {
      setPreview(await previewQuery("/api/v1/companies/{companyId}/fixed-assets/loads/preview", companyId, { ...next, cutoffDate: date }));
    } catch (caught) {
      setPreviewError(caught);
    }
  };
  return (
    <>
      <h1>Carga inicial de activos</h1>
      <FixedAssetTabs />
      <p className="muted">
        Los activos que la empresa ya tiene se cargan una vez, con su costo y su depreciación acumulada a la fecha de corte. La depreciación sigue desde el mes
        siguiente al corte.{" "}
        <a href={`data:text/csv;charset=utf-8,${encodeURIComponent(TEMPLATE)}`} download="plantilla-activos.csv">
          Descargar la plantilla
        </a>
      </p>
      {can("fixed_asset:manage") ? (
        <section className="card" data-testid="asset-load-form">
          <Field label="Fecha de corte" hint="El último día de un mes ya terminado.">
            <input
              type="date"
              value={cutoff}
              onChange={(e) => {
                setCutoff(e.target.value);
                if (upload) void check(upload, e.target.value);
              }}
            />
          </Field>
          <Field label="Archivo (CSV o Excel)">
            <input
              type="file"
              accept=".csv,.xlsx"
              onChange={async (e) => {
                const file = e.target.files?.[0];
                if (!file) return;
                const next = { fileName: file.name, contentBase64: await toBase64(file) };
                setUpload(next);
                await check(next, cutoff);
              }}
            />
          </Field>
          <ErrorBox error={previewError ?? prepare.error} />
          {preview ? (
            <>
              <p data-testid="asset-load-summary">
                {preview.rows} fila(s): {preview.valid} correcta(s), {preview.invalid} con error. Costo <Money value={preview.totalCost} />, acumulada{" "}
                <Money value={preview.totalAccumulated} />, valor en libros <Money value={preview.totalBookValue} />.
              </p>
              <div className="table-wrap">
                <table data-testid="asset-load-preview">
                  <thead>
                    <tr>
                      <th>Fila</th>
                      <th>Código</th>
                      <th>Descripción</th>
                      <th>Categoría</th>
                      <th className="num">Costo</th>
                      <th className="num">Acumulada</th>
                      <th>Meses</th>
                      <th className="num">Cuota mensual</th>
                      <th>Error</th>
                    </tr>
                  </thead>
                  <tbody>
                    {preview.items.map((r) => (
                      <tr key={r.row} className={r.error ? "has-error" : undefined} data-testid={`load-row:${r.code}`}>
                        <td>{r.row}</td>
                        <td>{r.code}</td>
                        <td>{r.description}</td>
                        <td>{r.categoryCode}</td>
                        <td className="num">{r.cost ? <Money value={r.cost} /> : "—"}</td>
                        <td className="num">{r.accumulated ? <Money value={r.accumulated} /> : "—"}</td>
                        <td>{r.monthsDepreciated !== null && r.usefulLifeMonths !== null ? `${r.monthsDepreciated} de ${r.usefulLifeMonths}` : "—"}</td>
                        <td className="num">{r.monthlyAmount ? <Money value={r.monthlyAmount} /> : "—"}</td>
                        <td>{r.error ?? ""}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div className="actions">
                <ConfirmAction
                  label="Preparar carga"
                  className="primary"
                  busy={prepare.busy}
                  disabled={!upload || preview.invalid > 0 || preview.valid === 0}
                  consequence={`Se preparan ${preview.valid} activo(s) al ${formatDate(cutoff)}; el Controller aprueba la carga.`}
                  onConfirm={async () => {
                    if (upload && (await prepare.run({ ...upload, cutoffDate: cutoff }, undefined, "Carga preparada; el Controller la aprueba."))) {
                      setUpload(null);
                      setPreview(null);
                      loads.reload();
                    }
                  }}
                />
              </div>
            </>
          ) : null}
        </section>
      ) : null}
      <h2>Cargas</h2>
      <ErrorBox error={approve.error ?? discard.error ?? reverse.error} />
      {loads.data === null ? (
        <LoadingIndicator error={loads.error} />
      ) : loads.data.items.length === 0 ? (
        <p className="muted">Todavía no hay cargas.</p>
      ) : (
        <div className="table-wrap">
          <table data-testid="asset-loads">
            <thead>
              <tr>
                <th>Corte</th>
                <th>Archivo</th>
                <th className="num">Activos</th>
                <th className="num">Costo</th>
                <th className="num">Acumulada</th>
                <th>Estado</th>
                <th>Preparó</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {loads.data.items.map((l) => (
                <tr key={`${l.loadId}:${l.version}`}>
                  <td>{formatDate(l.cutoffDate)}</td>
                  <td>{l.fileName}</td>
                  <td className="num">{l.rows}</td>
                  <td className="num">
                    <Money value={l.cost} />
                  </td>
                  <td className="num">
                    <Money value={l.accumulated} />
                  </td>
                  <td>
                    <StatusBadge status={l.status} label={LOAD_STATUS[l.status]} />
                  </td>
                  <td>{l.preparedBy ?? "—"}</td>
                  <td>
                    <div className="actions row-buttons">
                      {l.status === "DRAFT" && can("fixed_asset:approve") && !isMine(l.preparedBy) ? (
                        <ConfirmAction
                          label="Aprobar carga"
                          className="primary"
                          stepUp
                          busy={approve.busy}
                          consequence={`Se crean ${l.rows} activo(s) en servicio y se contabiliza la carga al ${formatDate(l.cutoffDate)} contra la contrapartida de saldos de apertura.`}
                          onConfirm={async () =>
                            (await approve.run({ loadId: l.loadId, expectedVersion: l.version }, undefined, "Carga contabilizada.")) && loads.reload()
                          }
                        />
                      ) : null}
                      {l.status === "DRAFT" && can("fixed_asset:manage") ? (
                        <ConfirmAction
                          label="Descartar"
                          danger
                          busy={discard.busy}
                          consequence="La carga preparada se descarta."
                          onConfirm={async () =>
                            (await discard.run({ loadId: l.loadId, expectedVersion: l.version }, undefined, "Carga descartada.")) && loads.reload()
                          }
                        />
                      ) : null}
                      {l.status === "POSTED" && can("fixed_asset:approve") ? (
                        <ReasonAction
                          label="Reversar"
                          stepUp
                          minLength={10}
                          busy={reverse.busy}
                          consequence="Se reversa el asiento de la carga y sus activos quedan anulados; solo si ninguno se ha depreciado ni dado de baja."
                          onConfirm={async (reason) =>
                            (await reverse.run({ loadId: l.loadId, expectedVersion: l.version, reason }, undefined, "Carga reversada.")) && loads.reload()
                          }
                        />
                      ) : null}
                    </div>
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
