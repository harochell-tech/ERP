"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatDecimal, formatPercent, formatQuantity } from "@/lib/decimal";
import { alertsText, certifiableDates, certificateVoidText, qualityActions, verdictBadge } from "@/lib/lab";
import { statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// LAB1-02 (E-LAB1-4, 5; E-LAB1-02-1…15): Calidad › Lotes. Every lot with the lab's verdict — 28-day strength real or estimated — and its
// alerts. A NO CUMPLE has already blocked the lot when it shows here; Calidad unblocks with a reason, blocks by hand, gives the final
// release (only on a CUMPLE with real breaks) and sees the recall: which deliveries and customers got the lot and what is left of it.
// LAB1-03 (E-LAB1-03-2…10): the lot's certificates — issue one per break date, with a delivery's customer or none; print; void — and
// the rack labels. A rack label's QR opens this page on its lot (`?lote=`).

type Lot = Schemas["LabLotView"];

const VIEWS: Record<string, string> = { "": "Todos", BLOCKED_BY_LAB: "Bloqueados por laboratorio", READY_FINAL: "Listos para liberación final" };

function initialView(): string {
  if (typeof window === "undefined") {
    return "";
  }
  return window.location.hash === "#bloqueados" ? "BLOCKED_BY_LAB" : window.location.hash === "#liberacion-final" ? "READY_FINAL" : "";
}

/** E-LAB1-03-10: a rack label's QR opens the page on its lot. */
function initialLot(): string | null {
  if (typeof window === "undefined") {
    return null;
  }
  const lot = new URLSearchParams(window.location.search).get("lote");
  return lot && /^[0-9a-f-]{36}$/i.test(lot) ? lot.toLowerCase() : null;
}

type Detail = Schemas["LabLotDetail"];
type Recall = Schemas["LotRecall"];

function Certificates({ detail, recall, onDone }: { detail: Detail; recall: Recall; onDone: () => void }) {
  const { can } = useSession();
  const lot = detail.lot;
  const name = lot.fieldCode ?? lot.lotCode;
  const dates = certifiableDates(detail.compression);
  const deliveries = [...new Map(recall.deliveries.map((d) => [d.deliveryId, d])).values()];
  const [breakDate, setBreakDate] = useState(dates[0] ?? "");
  const [deliveryId, setDeliveryId] = useState("");
  const issue = useCommand(`issue-certificate:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/issue-lab-certificate", `Lote ${name}: certificado emitido.`);
  const voidOne = useCommand(`void-certificate:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/void-lab-certificate", "Certificado anulado.");
  const canIssue = can("fg_lot:final_release");
  return (
    <>
      <h2>Certificados</h2>
      <div className="table-wrap">
        <table data-testid="lot-certificates">
          <thead>
            <tr>
              <th>Número</th>
              <th>Rotura</th>
              <th className="num">Probetas</th>
              <th>Conduce y cliente</th>
              <th>Estado</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {detail.certificates.map((c) => (
              <tr key={c.certificateId} data-testid={`lot-certificate:${c.certificateNo}`}>
                <td className="mono">{c.certificateNo}</td>
                <td>{c.breakDate}</td>
                <td className="num">{c.specimens}</td>
                <td className="wrap">{c.deliveryNo ? `${c.deliveryNo} · ${c.customerName ?? ""}` : "Sin conduce"}</td>
                <td className="wrap">
                  <StatusBadge status={c.status} />
                  {c.status === "VOIDED" ? <div className="muted">{`${certificateVoidText(c.voidCause)}: ${c.voidReason ?? ""}`}</div> : null}
                </td>
                <td className="actions">
                  <Link href={`/calidad/certificado/?id=${c.certificateId}`}>Imprimir</Link>
                  {canIssue && c.status === "ISSUED" ? (
                    <ReasonAction
                      label="Anular"
                      stepUp
                      busy={voidOne.busy}
                      consequence={`El certificado ${c.certificateNo} queda anulado y su verificación pública lo dirá.`}
                      onConfirm={async (reason) => (await voidOne.run({ plantId: lot.plantId, certificateId: c.certificateId, reason })) && onDone()}
                    />
                  ) : null}
                </td>
              </tr>
            ))}
            {detail.certificates.length === 0 ? (
              <tr>
                <td colSpan={6} className="muted">
                  Este lote no tiene certificados.
                </td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
      {canIssue ? (
        lot.fieldCode === null || lot.fieldCode === undefined ? (
          <p className="muted">Para emitir un certificado el lote necesita su código de campo.</p>
        ) : dates.length === 0 ? (
          <p className="muted">Para emitir un certificado el lote necesita al menos una probeta válida.</p>
        ) : (
          <div className="inline-form" data-testid="issue-certificate">
            <Field label="Fecha de rotura">
              <select value={breakDate} onChange={(e) => setBreakDate(e.target.value)}>
                {dates.map((d) => (
                  <option key={d} value={d}>
                    {d}
                  </option>
                ))}
              </select>
            </Field>
            <Field label="Cliente y obra del conduce">
              <select value={deliveryId} onChange={(e) => setDeliveryId(e.target.value)}>
                <option value="">Sin conduce (no muestra cliente ni obra)</option>
                {deliveries.map((d) => (
                  <option key={d.deliveryId} value={d.deliveryId}>
                    {d.deliveryNo} · {d.customerName}
                  </option>
                ))}
              </select>
            </Field>
            <ConfirmAction
              label="Emitir certificado"
              className="primary"
              stepUp
              busy={issue.busy}
              consequence={`Se emite el certificado de las probetas válidas del lote ${name} rotas el ${breakDate}, con los resultados ensayados (nunca la estimación a 28 días). Queda como una foto: si después se anula una de sus probetas, el certificado se anula solo.`}
              onConfirm={async () => (await issue.run({ plantId: lot.plantId, lotId: lot.lotId, breakDate, deliveryId: deliveryId || null })) && onDone()}
            />
          </div>
        )
      ) : null}
      <ErrorBox error={issue.error ?? voidOne.error} />
    </>
  );
}

function Verdict({ lot }: { lot: Pick<Lot, "verdict" | "basis" | "lotId"> }) {
  const badge = verdictBadge(lot.verdict, lot.basis);
  return (
    <span className={`badge ${badge.tone}`} data-testid={`lot-verdict:${lot.lotId}`}>
      {badge.label}
    </span>
  );
}

function Actions({ lot, onDone }: { lot: Lot; onDone: () => void }) {
  const { can } = useSession();
  const name = lot.fieldCode ?? lot.lotCode;
  const finalRelease = useCommand(`final-release:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/final-release-lot", `Lote ${name}: liberación final.`);
  const block = useCommand(`quality-block:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/block-lot", `Lote ${name} bloqueado.`);
  const unblock = useCommand(`quality-unblock:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/unblock-lot", `Lote ${name} desbloqueado.`);
  const reevaluate = useCommand(`reevaluate:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/reevaluate-lot", `Lote ${name} evaluado de nuevo.`);
  const actions = qualityActions(lot, can);
  const target = { plantId: lot.plantId, lotId: lot.lotId, expectedVersion: lot.version };
  return (
    <div className="inline-form" data-testid="lot-quality-actions">
      {actions.includes("finalRelease") ? (
        <ConfirmAction
          label="Liberación final"
          className="primary"
          stepUp
          busy={finalRelease.busy}
          consequence={
            <>
              El lote {name} queda con liberación final: cumple con roturas reales a 28 días.
              {lot.alerts.length > 0 ? ` Alertas que quedan registradas: ${alertsText(lot.alerts)}.` : ""}
            </>
          }
          onConfirm={async () => (await finalRelease.run(target)) && onDone()}
        />
      ) : null}
      {actions.includes("unblock") ? (
        <ReasonAction
          label="Desbloquear"
          busy={unblock.busy}
          consequence={`El lote ${name} vuelve al estado que tenía antes del bloqueo y se puede despachar de nuevo.`}
          onConfirm={async (reason) => (await unblock.run({ ...target, reason })) && onDone()}
        />
      ) : null}
      {actions.includes("block") ? (
        <ReasonAction
          label="Bloquear"
          busy={block.busy}
          consequence={`El lote ${name} deja de despacharse aunque tenga saldo en el patio, hasta que Calidad lo desbloquee.`}
          onConfirm={async (reason) => (await block.run({ ...target, reason })) && onDone()}
        />
      ) : null}
      {actions.includes("reevaluate") ? (
        <button type="button" disabled={reevaluate.busy} onClick={async () => (await reevaluate.run({ plantId: lot.plantId, lotId: lot.lotId })) && onDone()}>
          Evaluar de nuevo
        </button>
      ) : null}
      <ErrorBox error={finalRelease.error ?? block.error ?? unblock.error ?? reevaluate.error} />
    </div>
  );
}

function LotDetail({ lotId, onBack, onChanged }: { lotId: string; onBack: () => void; onChanged: () => void }) {
  const { companyId, can } = useSession();
  const { data, error, reload } = useLoad(
    async () => {
      const [detail, recall] = await Promise.all([
        query("/api/v1/companies/{companyId}/manufacturing/lab/lots/{lotId}", { path: { companyId, lotId } }),
        query("/api/v1/companies/{companyId}/manufacturing/lab/lots/{lotId}/recall", { path: { companyId, lotId } }),
      ]);
      return { detail, recall };
    },
    [companyId, lotId],
  );
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const { detail, recall } = data;
  const lot = detail.lot;
  const e = detail.evaluation;
  return (
    <>
      <p>
        <button type="button" onClick={onBack}>
          ← Todos los lotes
        </button>
      </p>
      <dl className="summary" data-testid="quality-lot">
        <dt>Lote</dt>
        <dd className="mono">{lot.fieldCode ?? lot.lotCode}</dd>
        <dt>Producto</dt>
        <dd>{lot.itemDescription}</dd>
        <dt>Producción</dt>
        <dd>
          {lot.productionDate} · máquina {lot.machineShortCode ?? lot.machineCode} · turno {lot.shiftCode}
        </dd>
        <dt>Estado</dt>
        <dd>
          <StatusBadge status={lot.status} testId="quality-lot-status" />
          {lot.status === "BLOCKED" ? <div className="muted">{lot.blockCause === "LAB" ? "Bloqueado por el laboratorio. " : "Bloqueado por Calidad. "}{lot.blockReason}</div> : null}
        </dd>
        <dt>Veredicto</dt>
        <dd>
          <Verdict lot={lot} />
          {lot.alerts.length > 0 ? <span className="muted"> {alertsText(lot.alerts)}</span> : null}
        </dd>
      </dl>
      <Actions
        key={`${lot.lotId}:${lot.version}`}
        lot={lot}
        onDone={() => {
          reload();
          onChanged();
        }}
      />
      {can("production:read") ? (
        <p>
          <Link href={`/produccion/etiquetas/?lote=${lot.lotId}`}>Imprimir etiquetas de los racks</Link>
        </p>
      ) : null}
      {e ? (
        <>
          <h2>Evaluación</h2>
          <dl className="summary" data-testid="quality-lot-evaluation">
            <dt>Probetas</dt>
            <dd>
              {e.specimens}
              {e.ageMin !== null && e.ageMin !== undefined ? ` · edad ${e.ageMin === e.ageMax ? `${e.ageMin} d` : `${e.ageMin} a ${e.ageMax} d`}` : ""}
            </dd>
            <dt>Promedio / mínimo / máximo</dt>
            <dd>{e.avgStrength ? `${formatDecimal(e.avgStrength)} / ${formatDecimal(e.minStrength)} / ${formatDecimal(e.maxStrength)} kg/cm²` : "—"}</dd>
            <dt>Desviación y CV</dt>
            <dd>{e.stdDev ? `${formatDecimal(e.stdDev)} kg/cm² · ${formatPercent(e.cv)}` : "—"}</dd>
            <dt>Resistencia a 28 días</dt>
            <dd>
              {e.strength28d
                ? e.basis === "REAL"
                  ? `${formatDecimal(e.strength28d)} kg/cm² (real), mínimo ${formatDecimal(e.min28d)}`
                  : `${formatDecimal(e.strength28d)} kg/cm² estimada: ${formatDecimal(e.earlyAvg)} a ${e.earlyAge} d ÷ factor ${formatDecimal(e.factorUsed)} (${e.factorSource === "OWN" ? "propio" : "inicial"}); mínimo ${formatDecimal(e.min28d)}`
                : "Sin dato"}
            </dd>
            <dt>Requisito</dt>
            <dd>
              {e.minAvgRequired
                ? `Promedio ≥ ${formatDecimal(e.minAvgRequired)}${e.minIndividualRequired ? `, individual ≥ ${formatDecimal(e.minIndividualRequired)}` : ""} kg/cm² (requisitos v${e.specVersion})`
                : "El ítem no tiene requisito a 28 días"}
            </dd>
          </dl>
        </>
      ) : (
        <p className="muted">Este lote todavía no tiene ensayos.</p>
      )}
      <Certificates
        detail={detail}
        recall={recall}
        onDone={() => {
          reload();
          onChanged();
        }}
      />
      <h2>Recall: a dónde fue el lote</h2>
      <p data-testid="quality-lot-recall-summary">
        Despachado: {formatQuantity(recall.dispatched)} un a {recall.customers} cliente(s) · En existencia: {formatQuantity(recall.inStock)} un
        {recall.stock.length > 0 ? ` (${recall.stock.map((s) => `${s.locationCode} ${formatQuantity(s.quantity)}`).join(", ")})` : ""}
      </p>
      <div className="table-wrap">
        <table data-testid="quality-lot-recall">
          <thead>
            <tr>
              <th>Conduce</th>
              <th>Salida</th>
              <th>Estado</th>
              <th>Cliente</th>
              <th>Obra</th>
              <th className="num">Cantidad</th>
              <th>Factura</th>
            </tr>
          </thead>
          <tbody>
            {recall.deliveries.map((d) => (
              <tr key={`${d.deliveryId}:${d.itemCode}`}>
                <td className="mono">{d.deliveryNo}</td>
                <td>{d.gateOutAt ? new Date(d.gateOutAt).toLocaleString("es-DO", { timeZone: "America/Santo_Domingo", dateStyle: "short", timeStyle: "short" }) : "—"}</td>
                <td>{statusLabel(d.status)}</td>
                <td className="wrap">
                  {d.customerName}
                  <div className="muted">Pedido {d.orderNo}</div>
                </td>
                <td className="wrap">{d.siteAddress ?? "Retira en planta"}</td>
                <td className="num">{formatQuantity(d.baseQuantity)}</td>
                <td className="mono">{d.invoiceNos ?? "Sin facturar"}</td>
              </tr>
            ))}
            {recall.deliveries.length === 0 ? (
              <tr>
                <td colSpan={7} className="muted">
                  Ningún conduce ha tomado este lote.
                </td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
    </>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("lab:read");
  const [view, setView] = useState(initialView);
  const [search, setSearch] = useState("");
  const [lotId, setLotId] = useState<string | null>(initialLot);
  const { data, error, reload } = useLoad(
    allowed ? () => query("/api/v1/companies/{companyId}/manufacturing/lab/lots", { path: { companyId }, query: { search: search.trim() || undefined, view: view || undefined, limit: 200 } }) : null,
    [companyId, view, search],
  );
  if (!allowed) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Lotes y veredicto del laboratorio</h1>
      {lotId !== null ? (
        <LotDetail key={lotId} lotId={lotId} onBack={() => setLotId(null)} onChanged={reload} />
      ) : (
        <>
          <p className="muted">
            El veredicto sale de las roturas del lote contra el requisito del ítem a 28 días: real cuando hay roturas a esa edad, estimado antes. Un «No cumple» —real o estimado— bloquea
            el lote y el despacho deja de tomarlo. La liberación final solo se da con «Cumple» real.
          </p>
          <div className="inline-form">
            <Field label="Ver">
              <select value={view} onChange={(e) => setView(e.target.value)}>
                {Object.entries(VIEWS).map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </select>
            </Field>
            <Field label="Código del lote">
              <input value={search} autoCapitalize="characters" placeholder="8070325P1" onChange={(e) => setSearch(e.target.value)} />
            </Field>
          </div>
          {data === null ? (
            <LoadingIndicator error={error} />
          ) : (
            <>
              <p data-testid="quality-lot-counts">
                {data.blockedByLab} bloqueado(s) por laboratorio · {data.readyForFinalRelease} listo(s) para liberación final
              </p>
              <div className="table-wrap">
                <table data-testid="quality-lots">
                  <thead>
                    <tr>
                      <th>Lote</th>
                      <th>Producto</th>
                      <th>Producción</th>
                      <th>Estado</th>
                      <th>Veredicto</th>
                      <th className="num">28 d (kg/cm²)</th>
                      <th>Alertas</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {data.items.map((l) => (
                      <tr key={l.lotId} data-testid={`quality-lot:${l.fieldCode ?? l.lotCode}`}>
                        <td className="mono">{l.fieldCode ?? l.lotCode}</td>
                        <td className="wrap">{l.itemDescription}</td>
                        <td>
                          {l.productionDate} · {l.machineShortCode ?? l.machineCode}
                        </td>
                        <td>
                          <StatusBadge status={l.status} />
                        </td>
                        <td>
                          <Verdict lot={l} />
                        </td>
                        <td className="num">{l.strength28d ? formatDecimal(l.strength28d) : "—"}</td>
                        <td className="wrap">{alertsText(l.alerts) || "—"}</td>
                        <td className="actions">
                          <button type="button" onClick={() => setLotId(l.lotId)}>
                            Ver
                          </button>
                        </td>
                      </tr>
                    ))}
                    {data.items.length === 0 ? (
                      <tr>
                        <td colSpan={8} className="muted">
                          No hay lotes con ese filtro.
                        </td>
                      </tr>
                    ) : null}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </>
      )}
    </>
  );
}
