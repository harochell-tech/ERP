"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type CommandResponse } from "@/api/client";
import { ErrorBox, Field, Loading, Money, NoPermission } from "@/components/ui";
import { isDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

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
  const importStatement = useCommand("import-statement", "/api/v1/companies/{companyId}/treasury/import-bank-statement");
  const { data: banks } = useLoad(() => query("/api/v1/companies/{companyId}/treasury/bank-accounts", { path: { companyId } }), [companyId]);
  const active = (banks?.items ?? []).filter((b) => b.status === "ACTIVE");
  const [bankAccountId, setBankAccountId] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [periodFrom, setPeriodFrom] = useState("");
  const [periodTo, setPeriodTo] = useState("");
  const [opening, setOpening] = useState("");
  const [closing, setClosing] = useState("");
  const [result, setResult] = useState<{ statementId: string; summary: ImportResult } | null>(null);
  const bank = bankAccountId || active[0]?.bankAccountId || "";
  const balance = (value: string) => (value.trim() === "" ? null : normalizeInput(value));
  const badBalance = [opening, closing].some((v) => v.trim() !== "" && !isDecimal(normalizeInput(v), 2));
  const tooLarge = file !== null && file.size > MAX_FILE_BYTES;

  return (
    <form
      className="card"
      onSubmit={async (e) => {
        e.preventDefault();
        if (!file) {
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
      <Field label="Cuenta bancaria">
        <select value={bank} onChange={(e) => setBankAccountId(e.target.value)} required>
          {active.map((b) => (
            <option key={b.bankAccountId} value={b.bankAccountId}>
              {b.bankCode} {b.accountNumber}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Archivo del banco">
        <input type="file" accept=".csv,text/csv,text/plain" onChange={(e) => setFile(e.target.files?.[0] ?? null)} required />
      </Field>
      <div>
        <p className="muted">Solo si el formato del banco no trae período o saldos:</p>
        <Field label="Desde">
          <input type="date" value={periodFrom} onChange={(e) => setPeriodFrom(e.target.value)} />
        </Field>
        <Field label="Hasta">
          <input type="date" value={periodTo} onChange={(e) => setPeriodTo(e.target.value)} />
        </Field>
        <Field label="Saldo inicial">
          <input className="mono" value={opening} onChange={(e) => setOpening(e.target.value)} />
        </Field>
        <Field label="Saldo final">
          <input className="mono" value={closing} onChange={(e) => setClosing(e.target.value)} />
        </Field>
      </div>
      <div className="actions">
        <button type="submit" className="primary" disabled={importStatement.busy || !file || bank === "" || badBalance || tooLarge}>
          Importar
        </button>
        {tooLarge ? <span className="muted">El archivo supera 5 MB.</span> : null}
        {badBalance ? <span className="muted">Saldos con máximo 2 decimales.</span> : null}
      </div>
      <ErrorBox error={importStatement.error} />
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
  if (!can("bank:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Extractos bancarios</h1>
      {can("bank_statement:import") ? <ImportForm onDone={reload} /> : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">Todavía no hay extractos importados.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Cuenta</th>
              <th>Período</th>
              <th className="num">Saldo inicial</th>
              <th className="num">Saldo final</th>
              <th>Archivo</th>
              <th>Importado</th>
              <th className="num">Sin conciliar</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((s) => (
              <tr key={s.statementId}>
                <td className="mono">
                  {s.bankCode} {s.accountNumber}
                </td>
                <td>
                  {formatDate(s.periodFrom)} – {formatDate(s.periodTo)}
                </td>
                <td className="num">
                  <Money value={s.openingBalance} />
                </td>
                <td className="num">
                  <Money value={s.closingBalance} />
                </td>
                <td>{s.fileName}</td>
                <td>
                  {formatDateTime(s.importedAt)} <span className="muted">{s.importedBy ?? ""}</span>
                </td>
                <td className="num">
                  {s.unmatched} / {s.lines}
                </td>
                <td>
                  <Link href={`/tesoreria/conciliacion/?cuenta=${s.bankAccountId}&extracto=${s.statementId}`}>Conciliar</Link>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
