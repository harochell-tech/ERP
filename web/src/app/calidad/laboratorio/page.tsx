"use client";

import { useEffect, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatDecimal, formatPercent, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { alertsText, BLOCK_CONDITION, FIELD_CODE_WAITS, specimenBody, todayIso, verdictBadge, type SpecimenDraft } from "@/lib/lab";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { previewQuery } from "@/lib/ux4b";

// LAB1-01 (E-LAB1-01-6…14): Calidad › Laboratorio, made for a phone next to the press. The technician picks the lot by its field code,
// types the specimens broken on one date (a missing measure is the item's nominal one) and the server shows each strength before
// saving. A test is never edited: it is voided with a reason and typed again. Absorption blocks (Ws, Wi, Wd) go below.

type Lot = Schemas["LabLotView"];
type Detail = Schemas["LabLotDetail"];

const EMPTY: SpecimenDraft = { widthCm: "", heightCm: "", lengthCm: "", weightKg: "", loadKg: "", blockCondition: "", failureType: "", notes: "" };

function lotName(lot: Lot): string {
  return lot.fieldCode ?? lot.lotCode;
}

function CompressionForm({ lot, failureTypes, onDone }: { lot: Lot; failureTypes: Schemas["FailureTypeView"][]; onDone: () => void }) {
  const { companyId } = useSession();
  const record = useCommand(`lab-compression:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/record-compression-tests", `Probetas del lote ${lotName(lot)} guardadas.`);
  const [breakDate, setBreakDate] = useState(todayIso());
  const [rows, setRows] = useState<SpecimenDraft[]>([{ ...EMPTY }]);
  // What the server answered, with the input it answered for: a preview of an older input is never shown.
  const [answer, setAnswer] = useState<{ key: string; preview: Schemas["CompressionPreview"] | null; problem: unknown } | null>(null);
  const ready = rows.every((r) => isPositiveDecimal(normalizeInput(r.loadKg), 6));
  const key = ready && breakDate ? JSON.stringify([lot.lotId, breakDate, rows.map(specimenBody)]) : "";
  useEffect(() => {
    if (!key) {
      return;
    }
    let cancelled = false;
    const timer = window.setTimeout(async () => {
      try {
        const [lotId, date, specimens] = JSON.parse(key) as [string, string, Schemas["CompressionSpecimen"][]];
        const result = await previewQuery("/api/v1/companies/{companyId}/manufacturing/lab/compression-tests/preview", companyId, { lotId, breakDate: date, specimens });
        if (!cancelled) {
          setAnswer({ key, preview: result, problem: null });
        }
      } catch (caught) {
        if (!cancelled) {
          setAnswer({ key, preview: null, problem: caught });
        }
      }
    }, 350);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [key, companyId]);
  const preview = answer !== null && answer.key === key ? answer.preview : null;
  const problem = answer !== null && answer.key === key ? answer.problem : null;
  const last = rows[rows.length - 1] ?? EMPTY;
  const set = (i: number, patch: Partial<SpecimenDraft>) => setRows(rows.map((r, n) => (n === i ? { ...r, ...patch } : r)));
  return (
    <form
      className="card"
      data-testid="lab-compression-form"
      onSubmit={async (e) => {
        e.preventDefault();
        if (await record.run({ plantId: lot.plantId, lotId: lot.lotId, breakDate, specimens: rows.map(specimenBody) })) {
          setRows([{ ...EMPTY }]);
          onDone();
        }
      }}
    >
      <h2>Rotura a compresión</h2>
      <Field label="Fecha de rotura" required hint={preview ? `Edad: ${preview.ageDays} día(s)${preview.ageZero ? " — rota el día de producción: no entra en estimaciones" : ""}` : undefined}>
        <input type="date" value={breakDate} max={todayIso()} min={lot.productionDate} required onChange={(e) => setBreakDate(e.target.value)} />
      </Field>
      {rows.map((r, i) => {
        const line = preview?.lines[i];
        return (
          <fieldset key={i} className="card" data-testid={`lab-specimen:${i + 1}`}>
            <legend>Probeta {i + 1}</legend>
            <div className="inline-form">
              <Field label="Carga (kg)" required>
                <input inputMode="decimal" value={r.loadKg} required onChange={(e) => set(i, { loadKg: e.target.value })} />
              </Field>
              <Field label="Ancho (cm)">
                <input inputMode="decimal" value={r.widthCm} placeholder="nominal" onChange={(e) => set(i, { widthCm: e.target.value })} />
              </Field>
              <Field label="Alto (cm)">
                <input inputMode="decimal" value={r.heightCm} placeholder="nominal" onChange={(e) => set(i, { heightCm: e.target.value })} />
              </Field>
              <Field label="Largo (cm)">
                <input inputMode="decimal" value={r.lengthCm} placeholder="nominal" onChange={(e) => set(i, { lengthCm: e.target.value })} />
              </Field>
              <Field label="Peso (kg)">
                <input inputMode="decimal" value={r.weightKg} onChange={(e) => set(i, { weightKg: e.target.value })} />
              </Field>
              <Field label="Condición">
                <select value={r.blockCondition} onChange={(e) => set(i, { blockCondition: e.target.value })}>
                  <option value="">—</option>
                  {Object.entries(BLOCK_CONDITION).map(([code, label]) => (
                    <option key={code} value={code}>
                      {label}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Tipo de falla">
                <select value={r.failureType} onChange={(e) => set(i, { failureType: e.target.value })}>
                  <option value="">—</option>
                  {failureTypes
                    .filter((t) => t.status === "ACTIVE")
                    .map((t) => (
                      <option key={t.code} value={t.code}>
                        {t.name}
                      </option>
                    ))}
                </select>
              </Field>
              <Field label="Observaciones" wide>
                <input value={r.notes} maxLength={500} onChange={(e) => set(i, { notes: e.target.value })} />
              </Field>
            </div>
            <p data-testid={`lab-specimen-result:${i + 1}`} aria-live="polite">
              {line?.strengthKgcm2 ? (
                <>
                  <strong>{formatDecimal(line.strengthKgcm2)} kg/cm²</strong> · {formatDecimal(line.strengthMpa)} MPa · área {formatDecimal(line.grossAreaCm2)} cm²
                  {line.nominalUsed ? " (medidas nominales)" : ""}
                </>
              ) : line?.error === "LAB_SPEC_MISSING" ? (
                <span className="muted">Escriba las medidas: este ítem aún no tiene medidas nominales.</span>
              ) : line?.error ? (
                <span className="muted">Revise las medidas y la carga.</span>
              ) : (
                <span className="muted">Escriba la carga para ver la resistencia.</span>
              )}
            </p>
            {rows.length > 1 ? (
              <button type="button" onClick={() => setRows(rows.filter((_, n) => n !== i))}>
                Quitar probeta {i + 1}
              </button>
            ) : null}
          </fieldset>
        );
      })}
      <div className="inline-form">
        <button type="button" disabled={rows.length >= 30} onClick={() => setRows([...rows, { ...EMPTY, widthCm: last.widthCm, heightCm: last.heightCm, lengthCm: last.lengthCm, blockCondition: last.blockCondition }])}>
          Agregar probeta
        </button>
        <button type="submit" className="primary" disabled={record.busy || !ready}>
          Guardar {rows.length} probeta(s)
        </button>
      </div>
      <ErrorBox error={record.error ?? problem} />
    </form>
  );
}

function AbsorptionForm({ lot, onDone }: { lot: Lot; onDone: () => void }) {
  const record = useCommand(`lab-absorption:${lot.lotId}`, "/api/v1/companies/{companyId}/manufacturing/record-absorption-tests", `Bloques de absorción del lote ${lotName(lot)} guardados.`);
  const [testDate, setTestDate] = useState(todayIso());
  const [rows, setRows] = useState([{ wsKg: "", wiKg: "", wdKg: "", notes: "" }]);
  const set = (i: number, patch: Partial<(typeof rows)[number]>) => setRows(rows.map((r, n) => (n === i ? { ...r, ...patch } : r)));
  const ready = rows.every((r) => [r.wsKg, r.wdKg].every((v) => isPositiveDecimal(normalizeInput(v), 6)) && /^\d+(\.\d{1,6})?$/.test(normalizeInput(r.wiKg)));
  return (
    <form
      className="card"
      data-testid="lab-absorption-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const blocks = rows.map((r) => ({ wsKg: normalizeInput(r.wsKg), wiKg: normalizeInput(r.wiKg), wdKg: normalizeInput(r.wdKg), notes: r.notes.trim() || null }));
        if (await record.run({ plantId: lot.plantId, lotId: lot.lotId, testDate, blocks })) {
          setRows([{ wsKg: "", wiKg: "", wdKg: "", notes: "" }]);
          onDone();
        }
      }}
    >
      <h2>Absorción y densidad</h2>
      <Field label="Fecha del ensayo" required>
        <input type="date" value={testDate} max={todayIso()} min={lot.productionDate} required onChange={(e) => setTestDate(e.target.value)} />
      </Field>
      {rows.map((r, i) => (
        <fieldset key={i} className="card">
          <legend>Bloque {i + 1}</legend>
          <div className="inline-form">
            <Field label="Peso saturado Ws (kg)" required>
              <input inputMode="decimal" value={r.wsKg} required onChange={(e) => set(i, { wsKg: e.target.value })} />
            </Field>
            <Field label="Peso sumergido Wi (kg)" required>
              <input inputMode="decimal" value={r.wiKg} required onChange={(e) => set(i, { wiKg: e.target.value })} />
            </Field>
            <Field label="Peso seco Wd (kg)" required>
              <input inputMode="decimal" value={r.wdKg} required onChange={(e) => set(i, { wdKg: e.target.value })} />
            </Field>
            <Field label="Observaciones del bloque" wide>
              <input value={r.notes} maxLength={500} onChange={(e) => set(i, { notes: e.target.value })} />
            </Field>
          </div>
          {rows.length > 1 ? (
            <button type="button" onClick={() => setRows(rows.filter((_, n) => n !== i))}>
              Quitar bloque {i + 1}
            </button>
          ) : null}
        </fieldset>
      ))}
      <div className="inline-form">
        <button type="button" disabled={rows.length >= 30} onClick={() => setRows([...rows, { wsKg: "", wiKg: "", wdKg: "", notes: "" }])}>
          Agregar bloque
        </button>
        <button type="submit" className="primary" disabled={record.busy || !ready}>
          Guardar {rows.length} bloque(s)
        </button>
      </div>
      <ErrorBox error={record.error} />
    </form>
  );
}

function VoidTest({ kind, plantId, testId, onDone }: { kind: "compression" | "absorption"; plantId: string; testId: string; onDone: () => void }) {
  const compression = useCommand(`void-compression:${testId}`, "/api/v1/companies/{companyId}/manufacturing/void-compression-test", "Ensayo anulado.");
  const absorption = useCommand(`void-absorption:${testId}`, "/api/v1/companies/{companyId}/manufacturing/void-absorption-test", "Ensayo anulado.");
  const command = kind === "compression" ? compression : absorption;
  return (
    <>
      <ReasonAction
        label="Anular"
        title="Anular el ensayo"
        stepUp
        busy={command.busy}
        consequence="El ensayo queda anulado, a la vista y sin contar para el lote. Si hubo un error de digitación, regístrelo de nuevo."
        onConfirm={async (reason) => (await command.run({ plantId, testId, reason })) && onDone()}
      />
      <ErrorBox error={command.error} />
    </>
  );
}

function LotTests({ lot, detail, canRecord, onDone }: { lot: Lot; detail: Detail; canRecord: boolean; onDone: () => void }) {
  const summary = detail.absorptionSummary;
  return (
    <>
      <h2>Probetas del lote</h2>
      <div className="table-wrap">
        <table data-testid="lab-compression-tests">
          <thead>
            <tr>
              <th>Rotura</th>
              <th className="num">Edad</th>
              <th>Medidas (cm)</th>
              <th className="num">Carga (kg)</th>
              <th className="num">kg/cm²</th>
              <th className="num">MPa</th>
              <th>Falla</th>
              <th>Técnico</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {detail.compression.map((t) => (
              <tr key={t.testId} className={t.status === "VOIDED" ? "muted" : undefined}>
                <td>{t.breakDate}</td>
                <td className="num">{t.ageDays} d</td>
                <td>
                  {formatDecimal(t.widthCm, 1)} × {formatDecimal(t.heightCm, 1)} × {formatDecimal(t.lengthCm, 1)}
                  {t.nominalUsed ? " (nominales)" : ""}
                </td>
                <td className="num">{formatDecimal(t.loadKg, 0)}</td>
                <td className="num">{formatDecimal(t.strengthKgcm2)}</td>
                <td className="num">{formatDecimal(t.strengthMpa)}</td>
                <td className="wrap">
                  {t.failureTypeName ?? "—"}
                  {t.blockCondition ? ` · ${BLOCK_CONDITION[t.blockCondition] ?? t.blockCondition}` : ""}
                  {t.notes ? <div className="muted">{t.notes}</div> : null}
                </td>
                <td>{t.testedBy}</td>
                <td className="actions">
                  {t.status === "VOIDED" ? (
                    <span title={t.voidReason ?? undefined}>Anulado: {t.voidReason}</span>
                  ) : canRecord ? (
                    <VoidTest kind="compression" plantId={lot.plantId} testId={t.testId} onDone={onDone} />
                  ) : null}
                </td>
              </tr>
            ))}
            {detail.compression.length === 0 ? (
              <tr>
                <td colSpan={9} className="muted">
                  Este lote todavía no tiene probetas.
                </td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
      {detail.absorption.length > 0 ? (
        <>
          <h2>Absorción y densidad del lote</h2>
          {summary ? (
            <p data-testid="lab-absorption-summary">
              Promedio de {summary.blocks} bloque(s): absorción {formatDecimal(summary.absorptionKgm3)} kg/m³ ({formatPercent(summary.absorptionFraction)}), densidad{" "}
              {formatDecimal(summary.densityKgm3)} kg/m³ — peso {summary.densityClass.toLowerCase()}, límite {formatDecimal(summary.absorptionLimitKgm3, 0)} kg/m³.
            </p>
          ) : null}
          <div className="table-wrap">
            <table data-testid="lab-absorption-tests">
              <thead>
                <tr>
                  <th>Fecha</th>
                  <th className="num">Ws</th>
                  <th className="num">Wi</th>
                  <th className="num">Wd</th>
                  <th className="num">Absorción (kg/m³)</th>
                  <th className="num">Densidad (kg/m³)</th>
                  <th>Clase</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {detail.absorption.map((t) => (
                  <tr key={t.testId} className={t.status === "VOIDED" ? "muted" : undefined}>
                    <td>{t.testDate}</td>
                    <td className="num">{formatDecimal(t.wsKg)}</td>
                    <td className="num">{formatDecimal(t.wiKg)}</td>
                    <td className="num">{formatDecimal(t.wdKg)}</td>
                    <td className="num">
                      {formatDecimal(t.absorptionKgm3)} {t.guide === "ALTA" ? <span className="badge tone-attention">Alta</span> : null}
                    </td>
                    <td className="num">{formatDecimal(t.densityKgm3)}</td>
                    <td>{t.densityClass.toLowerCase()}</td>
                    <td className="actions">
                      {t.status === "VOIDED" ? (
                        <span>Anulado: {t.voidReason}</span>
                      ) : canRecord ? (
                        <VoidTest kind="absorption" plantId={lot.plantId} testId={t.testId} onDone={onDone} />
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      ) : null}
    </>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("lab:read");
  const [search, setSearch] = useState("");
  const [lotId, setLotId] = useState<string | null>(null);
  const lots = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/manufacturing/lab/lots", { path: { companyId }, query: { search: search.trim() || undefined, limit: 50 } }) : null, [companyId, search]);
  const settings = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/manufacturing/lab/settings", { path: { companyId } }) : null, [companyId]);
  const detail = useLoad(allowed && lotId ? () => query("/api/v1/companies/{companyId}/manufacturing/lab/lots/{lotId}", { path: { companyId, lotId } }) : null, [companyId, lotId]);
  if (!allowed) {
    return <NoPermission />;
  }
  const canRecord = can("lab_test:record");
  const lot = lotId ? detail.data?.lot ?? null : null;
  return (
    <>
      <h1>Laboratorio</h1>
      {lotId === null ? (
        <>
          <p className="muted">Busque el lote por el código de su etiqueta (por ejemplo 8070325P1) y registre sus probetas o sus bloques de absorción.</p>
          <Field label="Código del lote">
            <input value={search} inputMode="search" autoCapitalize="characters" placeholder="8070325P1" onChange={(e) => setSearch(e.target.value)} />
          </Field>
          {lots.data && lots.data.withoutFieldCode > 0 ? (
            <p className="notice tone-attention" data-testid="lab-lots-without-code">
              {lots.data.withoutFieldCode} lote(s) todavía no tienen código de campo: falta el prefijo del ítem (Requisitos por ítem) o el código corto de la máquina.
            </p>
          ) : null}
          {lots.data === null ? (
            <LoadingIndicator error={lots.error} />
          ) : (
            <div className="table-wrap">
              <table data-testid="lab-lots">
                <thead>
                  <tr>
                    <th>Lote</th>
                    <th>Producto</th>
                    <th>Producción</th>
                    <th>Estado</th>
                    <th className="num">Probetas</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {lots.data.items.map((l) => (
                    <tr key={l.lotId} data-testid={`lab-lot:${lotName(l)}`}>
                      <td className="mono">
                        {l.fieldCode ?? <span title={l.lotCode}>{l.lotCode}</span>}
                        {l.fieldCodeWaitsFor ? <div className="muted">{FIELD_CODE_WAITS[l.fieldCodeWaitsFor] ?? "Sin código de campo"}</div> : null}
                      </td>
                      <td className="wrap">{l.itemDescription}</td>
                      <td>
                        {l.productionDate} · {l.machineShortCode ?? l.machineCode} · {l.shiftCode}
                      </td>
                      <td>
                        <StatusBadge status={l.status} />
                      </td>
                      <td className="num">
                        {l.compressionTests}
                        {l.lastBreakDate ? <div className="muted">última {l.lastBreakDate}</div> : null}
                      </td>
                      <td className="actions">
                        <button type="button" className="primary" onClick={() => setLotId(l.lotId)}>
                          {canRecord ? "Ensayar" : "Ver"}
                        </button>
                      </td>
                    </tr>
                  ))}
                  {lots.data.items.length === 0 ? (
                    <tr>
                      <td colSpan={6} className="muted">
                        No hay lotes con ese código.
                      </td>
                    </tr>
                  ) : null}
                </tbody>
              </table>
            </div>
          )}
        </>
      ) : lot === null || settings.data === null ? (
        <LoadingIndicator error={detail.error ?? settings.error} />
      ) : (
        <>
          <p>
            <button type="button" onClick={() => setLotId(null)}>
              ← Otro lote
            </button>
          </p>
          <dl className="summary" data-testid="lab-lot">
            <dt>Lote</dt>
            <dd className="mono">{lotName(lot)}</dd>
            <dt>Producto</dt>
            <dd>{lot.itemDescription}</dd>
            <dt>Producción</dt>
            <dd>
              {lot.productionDate} · máquina {lot.machineShortCode ?? lot.machineCode} · turno {lot.shiftCode}
            </dd>
            <dt>Estado</dt>
            <dd>
              <StatusBadge status={lot.status} />
            </dd>
            <dt>Veredicto</dt>
            <dd data-testid="lab-lot-verdict">
              <span className={`badge ${verdictBadge(lot.verdict, lot.basis).tone}`}>{verdictBadge(lot.verdict, lot.basis).label}</span>
              {lot.strength28d ? ` ${formatDecimal(lot.strength28d)} kg/cm² a 28 d` : ""}
              {lot.alerts.length > 0 ? <span className="muted"> · {alertsText(lot.alerts)}</span> : null}
              {lot.status === "BLOCKED" && lot.blockCause === "LAB" ? <div className="muted">El lote quedó bloqueado: no se despacha hasta que Calidad lo revise.</div> : null}
            </dd>
            <dt>Medidas nominales</dt>
            <dd>
              {detail.data?.spec
                ? `${formatDecimal(detail.data.spec.nominalWidthCm, 1)} × ${formatDecimal(detail.data.spec.nominalHeightCm, 1)} × ${formatDecimal(detail.data.spec.nominalLengthCm, 1)} cm`
                : "Sin requisitos: escriba las medidas de cada probeta"}
            </dd>
          </dl>
          {canRecord ? <CompressionForm key={`c:${lot.lotId}`} lot={lot} failureTypes={settings.data.failureTypes} onDone={detail.reload} /> : null}
          {detail.data ? <LotTests lot={lot} detail={detail.data} canRecord={canRecord} onDone={detail.reload} /> : null}
          {canRecord ? <AbsorptionForm key={`a:${lot.lotId}`} lot={lot} onDone={detail.reload} /> : null}
        </>
      )}
    </>
  );
}
