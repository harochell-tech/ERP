"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type CommandResponse } from "@/api/client";
import { ErrorBox, Field, Loading, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { isDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { bankAccountLabel } from "@/lib/ux4a";
import { statementLinesText } from "@/lib/ux4a-tesoreria";

/** E-VS2-05-2: the file travels base64, at most 5 MB. */
const MAX_FILE_BYTES = 5 * 1024 * 1024;

function toBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ""));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

interface ImportResult {
  inserted: number;
  linesInFile: number;
  duplicates: { row: number; existingStatementId: string }[];
}

// VS2-08: the treasurer imports a bank's CSV (E-VS2-05-1…9). Period and balances go only when the bank's format lacks them; one
// unreadable row rejects the whole file; a repeated file is refused and an overlapping one reports its duplicates.
function ImportForm({ onDone }: { onDone: () => void }) {
  const { companyId } = useSession();
  const importStatement = useCommand("import-statement", "/api/v1/companies/{companyId}/treasury/import-bank-statement", (response) => {
    const summary = response.result as unknown as ImportResult;
    return `Extracto importado: ${summary.inserted} de ${summary.linesInFile} líneas.`;
  });
  const { data: banks } = useLoad(() => query("/api/v1/companies/{companyId}/treasury/bank-accounts", { path: { companyId } }), [companyId]);
  const active = (banks?.items ?? []).filter((b) => b.status === "ACTIVE");
  const [bankAccountId, setBankAccountId] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [periodFrom, setPeriodFrom] = useState("");
  const [periodTo, setPeriodTo] = useState("");
  const [opening, setOpening] = useState("");
  const [closing, setClosing] = useState("");
  const [result, setResult] = useState<{ statementId: string; summary: ImportResult } | null>(null);
  const fe = useFieldErrors<"bank" | "file" | "opening" | "closing">();
  const bank = bankAccountId || active[0]?.bankAccountId || "";
  const balance = (value: string) => (value.trim() === "" ? null : normalizeInput(value));
  const badBalance = (value: string) => value.trim() !== "" && !isDecimal(normalizeInput(value), 2) && "Saldo con máximo 2 decimales (ej. -10770.00).";

  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const valid = fe.check({
          bank: bank === "" && "Elija la cuenta bancaria.",
          file: file === null ? "Elija el archivo CSV del banco." : file.size > MAX_FILE_BYTES && "El archivo supera 5 MB.",
          opening: badBalance(opening),
          closing: badBalance(closing),
        });
        if (!valid || !file) {
          return;
        }
        const response: CommandResponse | undefined = await importStatement.run({
          bankAccountId: bank,
          fileName: file.name,
          contentBase64: await toBase64(file),
          periodFrom: periodFrom || null,
          periodTo: periodTo || null,
          openingBalance: balance(opening),
          closingBalance: balance(closing),
        });
        if (response) {
          setResult({ statementId: response.resultRef, summary: response.result as unknown as ImportResult });
          setFile(null);
          onDone();
        }
      }}
    >
      <h2 style={{ marginTop: 0 }}>Importar extracto (CSV)</h2>
      <Field label="Cuenta bancaria" required error={fe.errors.bank}>
        <select value={bank} onChange={(e) => setBankAccountId(e.target.value)}>
          {active.map((b) => (
            <option key={b.bankAccountId} value={b.bankAccountId}>
              {bankAccountLabel(b)}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Archivo del banco" required error={fe.errors.file} hint="CSV, máximo 5 MB.">
        <input type="file" accept=".csv,text/csv,text/plain" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
      </Field>
      <div>
        <p className="muted">Solo si el formato del banco no trae período o saldos:</p>
        <Field label="Desde">
          <input type="date" value={periodFrom} onChange={(e) => setPeriodFrom(e.target.value)} />
        </Field>
        <Field label="Hasta">
          <input type="date" value={periodTo} onChange={(e) => setPeriodTo(e.target.value)} />
        </Field>
        <Field label="Saldo inicial" error={fe.errors.opening}>
          <input className="mono" inputMode="decimal" value={opening} onChange={(e) => setOpening(e.target.value)} />
        </Field>
        <Field label="Saldo final" error={fe.errors.closing}>
          <input className="mono" inputMode="decimal" value={closing} onChange={(e) => setClosing(e.target.value)} />
        </Field>
      </div>
      <ErrorBox error={importStatement.error} />
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={importStatement.busy}>
          Importar
        </button>
      </div>
      {result ? (
        <div className="notice" data-testid="import-result">
          Importadas {result.summary.inserted} de {result.summary.linesInFile} líneas
          {result.summary.duplicates.length > 0 ? `; ${result.summary.duplicates.length} ya estaban importadas (filas ${result.summary.duplicates.map((d) => d.row).join(", ")})` : ""}.{" "}
          <Link href={`/tesoreria/conciliacion/?cuenta=${bank}&extracto=${result.statementId}`}>Conciliar este extracto</Link>
        </div>
      ) : null}
    </form>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const { data, error, reload } = useLoad(
    can("bank:read") ? () => query("/api/v1/companies/{companyId}/treasury/bank-statements", { path: { companyId }, query: { limit: 200 } }) : null,
    [companyId],
  );
  const [importing, setImporting] = useState(false);
  if (!can("bank:read")) {
    return <NoPermission />;
  }
  return (
    <>
      {/* C-30: the create action sits beside the title, as on Pagos; the form opens below it. */}
      <div className="actions" style={{ justifyContent: "space-between" }}>
        <h1>Extractos bancarios</h1>
        {can("bank_statement:import") && !importing ? (
          <button type="button" className="primary" onClick={() => setImporting(true)}>
            Importar extracto
          </button>
        ) : null}
      </div>
      {can("bank_statement:import") && importing ? (
        <>
          <ImportForm onDone={reload} />
          <div className="actions">
            <button type="button" onClick={() => setImporting(false)}>
              Cerrar formulario
            </button>
          </div>
        </>
      ) : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">Todavía no hay extractos importados. Importe el archivo CSV que descarga del banco con «Importar extracto».</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Cuenta</th>
              <th>Período</th>
              <th className="num">Saldo inicial (RD$)</th>
              <th className="num">Saldo final (RD$)</th>
              <th>Archivo</th>
              <th>Importado</th>
              <th>Líneas del extracto</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((s) => (
              <tr key={s.statementId}>
                <td className="mono">{bankAccountLabel({ alias: s.bankAccountAlias, bankCode: s.bankCode, accountNumber: s.accountNumber })}</td>
                <td>
                  {formatDate(s.periodFrom)} – {formatDate(s.periodTo)}
                </td>
                <td className="num">
                  <Money value={s.openingBalance} />
                </td>
                <td className="num">
                  <Money value={s.closingBalance} />
                </td>
                <td className="wrap">{s.fileName}</td>
                <td>
                  {formatDateTime(s.importedAt)} <span className="muted">{s.importedBy ?? ""}</span>
                </td>
                <td>
                  {s.lines > 0 && s.unmatched === 0 ? <StatusBadge status="MATCHED" label={statementLinesText(s.lines, s.unmatched)} /> : statementLinesText(s.lines, s.unmatched)}
                </td>
                <td>
                  <Link href={`/tesoreria/conciliacion/?cuenta=${s.bankAccountId}&extracto=${s.statementId}`}>Conciliar</Link>
                </td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
