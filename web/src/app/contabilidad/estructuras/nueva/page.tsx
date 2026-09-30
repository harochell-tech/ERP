"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, Loading, NoPermission, useFieldErrors } from "@/components/ui";
import { todayInDominicanRepublic } from "@/lib/labels";
import { accountClassLabel, REPORTS } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { lineOptionLabel, sameStructure, STRUCTURE_NEXT_STEP } from "@/lib/ux4a-contabilidad";

interface Line {
  lineCode: string;
  caption: string;
  parentLineCode: string;
  sign: number;
}

interface Values {
  effectiveFrom: string;
  lines: Line[];
  /** accountId → lineCode */
  placement: Record<string, string>;
}

const CLASSES: Readonly<Record<string, string[]>> = {
  BALANCE_SHEET: ["ASSET", "LIABILITY", "EQUITY"],
  INCOME_STATEMENT: ["REVENUE", "COST", "EXPENSE"],
};

// FIN1-04 (E-FIN1-04-10): a new DRAFT version, starting from a copy of the given (or active) version. Lines on top; below, every
// active account of the report's classes with the line that holds it.
// UX4-02 (A-17): no version without changes (the button waits for one), lines named by their concept instead of their code
// ("Activo (A)"), "Agrupada bajo" instead of "Dentro de", and what happens next (who approves).
function StructureEditor() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const search = useSearchParams();
  const report = search.get("reporte") === "INCOME_STATEMENT" ? "INCOME_STATEMENT" : "BALANCE_SHEET";
  const source = search.get("desde");
  const prepare = useCommand<"/api/v1/companies/{companyId}/finance/prepare-report-structure", Values>(
    `prepare-report-structure:${report}`,
    "/api/v1/companies/{companyId}/finance/prepare-report-structure",
    `Nueva versión de ${REPORTS[report]} preparada; falta su aprobación.`,
  );
  const [values, setValues] = useState<Values | null>(prepare.restored);
  const [invalid, setInvalid] = useState<string | null>(null);
  // UX4-02 (A-17): the copied version's lines and placement, to refuse a version without changes.
  const [original, setOriginal] = useState<Pick<Values, "lines" | "placement"> | null>(null);
  const fe = useFieldErrors();
  const allowed = can("account:manage") && can("configuration:read");

  const { data, error } = useLoad(
    allowed
      ? async () => {
          const [accounts, list] = await Promise.all([
            query("/api/v1/companies/{companyId}/finance/accounts", { path: { companyId } }),
            query("/api/v1/companies/{companyId}/finance/report-structures", { path: { companyId }, query: { report } }),
          ]);
          const from = source ?? list.items.find((s) => s.status === "ACTIVE")?.structureVersionId;
          const copy = from ? await query("/api/v1/companies/{companyId}/finance/report-structures/{structureVersionId}", { path: { companyId, structureVersionId: from } }) : null;
          const reportAccounts = accounts.items.filter((a) => a.status === "ACTIVE" && a.accountClass !== null && (CLASSES[report] ?? []).includes(a.accountClass ?? ""));
          const placement: Record<string, string> = {};
          for (const line of copy?.lines ?? []) {
            for (const a of line.accounts) {
              placement[a.accountId] = line.lineCode;
            }
          }
          const copiedLines = (copy?.lines ?? []).map((l) => ({ lineCode: l.lineCode, caption: l.caption, parentLineCode: l.parentLineCode ?? "", sign: l.sign }));
          setOriginal(copy ? { lines: copiedLines, placement } : null);
          if (values === null) {
            setValues({ effectiveFrom: todayInDominicanRepublic(), lines: copiedLines, placement });
          }
          return { accounts: reportAccounts, copiedVersion: copy?.header.version ?? null };
        }
      : null,
    [companyId, allowed, report, source],
  );

  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null || values === null) {
    return <Loading error={error} />;
  }

  const set = (change: Partial<Values>) => setValues({ ...values, ...change });
  const setLine = (index: number, change: Partial<Line>) => set({ lines: values.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) });
  const codes = values.lines.map((l) => l.lineCode.trim()).filter((c) => c.length > 0);
  const unchanged = original !== null && sameStructure(original, values);
  const captionOf = (code: string) => values.lines.find((l) => l.lineCode.trim() === code)?.caption ?? "";

  const submit = async () => {
    if (values.lines.length === 0) {
      setInvalid("Agregue al menos una línea.");
      return;
    }
    setInvalid(null);
    const found: Record<string, string | false> = { effectiveFrom: !values.effectiveFrom && "Indique desde cuándo rige la versión." };
    values.lines.forEach((l, index) => {
      const code = l.lineCode.trim();
      found[`line-${index}-code`] = !code ? "Indique el código." : codes.indexOf(code) !== codes.lastIndexOf(code) && "Código repetido.";
      found[`line-${index}-caption`] = !l.caption.trim() && "Indique el concepto.";
    });
    if (!fe.check(found)) {
      return;
    }
    const response = await prepare.run(
      {
        report,
        effectiveFrom: values.effectiveFrom,
        lines: values.lines.map((l, i) => ({
          lineCode: l.lineCode.trim(),
          caption: l.caption.trim(),
          parentLineCode: l.parentLineCode || null,
          sign: l.sign,
          orderNo: i + 1,
          accountIds: data.accounts.filter((a) => values.placement[a.accountId] === l.lineCode.trim()).map((a) => a.accountId),
        })),
      },
      values,
    );
    if (response) {
      router.push(`/contabilidad/estructura/?id=${response.resultRef}`);
    }
  };

  return (
    <>
      <h1>Nueva versión: {REPORTS[report]}</h1>
      <p className="muted">{data.copiedVersion ? `Copia de la versión ${data.copiedVersion}.` : "Sin versión anterior: agregue las líneas."} El orden de las filas es el orden del reporte.</p>
      <p className="muted">{STRUCTURE_NEXT_STEP}</p>
      <Field label="Regirá desde" required error={fe.errors.effectiveFrom} hint="Fecha desde la que los estados usarán esta versión, una vez aprobada.">
        <input type="date" aria-label="Regirá desde" value={values.effectiveFrom} onChange={(e) => set({ effectiveFrom: e.target.value })} />
      </Field>
      <h2>Líneas</h2>
      <LineTable>
        <thead>
          <tr>
            <th>Código</th>
            <th>Concepto</th>
            <th>Agrupada bajo</th>
            <th>Signo</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {values.lines.map((l, index) => (
            <tr key={index}>
              <td>
                <input
                  aria-label={`Código de línea ${index + 1}`}
                  size={8}
                  maxLength={20}
                  value={l.lineCode}
                  onChange={(e) => setLine(index, { lineCode: e.target.value })}
                  {...fieldAria(fe.errors[`line-${index}-code`], `line-${index}-code-message`, true)}
                />
                <FieldMessage id={`line-${index}-code-message`} error={fe.errors[`line-${index}-code`]} />
              </td>
              <td>
                <input
                  aria-label={`Concepto ${index + 1}`}
                  style={{ width: "min(22rem, 100%)" }}
                  maxLength={200}
                  value={l.caption}
                  onChange={(e) => setLine(index, { caption: e.target.value })}
                  {...fieldAria(fe.errors[`line-${index}-caption`], `line-${index}-caption-message`, true)}
                />
                <FieldMessage id={`line-${index}-caption-message`} error={fe.errors[`line-${index}-caption`]} />
              </td>
              <td>
                <select aria-label={`Agrupada bajo ${index + 1}`} value={l.parentLineCode} onChange={(e) => setLine(index, { parentLineCode: e.target.value })}>
                  <option value="">Ninguna (línea principal)</option>
                  {codes
                    .filter((c) => c !== l.lineCode.trim())
                    .map((c) => (
                      <option key={c} value={c}>
                        {lineOptionLabel(c, captionOf(c))}
                      </option>
                    ))}
                </select>
              </td>
              <td>
                <select aria-label={`Signo ${index + 1}`} value={l.sign} onChange={(e) => setLine(index, { sign: e.target.value === "-1" ? -1 : 1 })}>
                  <option value={1}>Deudor (+): activo, costo, gasto</option>
                  <option value={-1}>Acreedor (−): pasivo, patrimonio, ingreso</option>
                </select>
              </td>
              <td>
                <button type="button" onClick={() => set({ lines: values.lines.filter((_, i) => i !== index) })}>
                  Quitar
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </LineTable>
      <div className="actions">
        <button type="button" onClick={() => set({ lines: [...values.lines, { lineCode: "", caption: "", parentLineCode: "", sign: report === "BALANCE_SHEET" ? 1 : -1 }] })}>
          Agregar línea
        </button>
      </div>
      <h2>Cuentas</h2>
      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th>Cuenta</th>
            <th>Clase</th>
            <th>Se muestra en</th>
          </tr>
        </thead>
        <tbody>
          {data.accounts.map((a) => (
            <tr key={a.accountId}>
              <td>
                <span className="mono">{a.code}</span> {a.name}
              </td>
              <td>{accountClassLabel(a.accountClass)}</td>
              <td>
                <select aria-label={`Línea de ${a.code}`} value={values.placement[a.accountId] ?? ""} onChange={(e) => set({ placement: { ...values.placement, [a.accountId]: e.target.value } })}>
                  <option value="">— sin línea</option>
                  {codes.map((c) => (
                    <option key={c} value={c}>
                      {lineOptionLabel(c, captionOf(c))}
                    </option>
                  ))}
                </select>
              </td>
            </tr>
          ))}
        </tbody>
      </table></div>
      <div className="actions form-actions">
        <button type="button" className="primary" disabled={prepare.busy || unchanged} onClick={submit}>
          Preparar versión
        </button>
        {unchanged ? (
          <span className="muted" data-testid="structure-unchanged">
            Sin cambios respecto de la versión {data.copiedVersion}: cambie líneas o cuentas para preparar una versión nueva.
          </span>
        ) : null}
        {invalid ? <span className="field-error">{invalid}</span> : null}
      </div>
      <ErrorBox error={prepare.error} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <StructureEditor />
    </Suspense>
  );
}
