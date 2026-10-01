"use client";

import { useState } from "react";
import { ErrorBox, Field, StatusBadge, useFieldErrors } from "@/components/ui";
import {
  fileProblem,
  importSummary,
  outcomeLabel,
  outcomeStatus,
  reasonText,
  rowsToLoad,
  termsText,
  type ImportKind,
  type ImportPreview,
} from "@/lib/partyImport";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { previewQuery } from "@/lib/ux4a";

const PATHS = {
  suppliers: {
    preview: "/api/v1/companies/{companyId}/master-data/suppliers/import-preview",
    command: "/api/v1/companies/{companyId}/master-data/import-suppliers",
  },
  customers: {
    preview: "/api/v1/companies/{companyId}/sales/customers/import-preview",
    command: "/api/v1/companies/{companyId}/sales/import-customers",
  },
} as const;

interface Upload {
  fileName: string;
  contentBase64: string;
}

function toBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ""));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

function Rows({ preview, kind }: { preview: ImportPreview; kind: ImportKind }) {
  const [only, setOnly] = useState("");
  const shown = preview.items.filter((row) => only === "" || row.outcome === only);
  const outcomes = [...new Set(preview.items.map((row) => row.outcome))];
  return (
    <>
      <div className="inline-form">
        <Field label="Mostrar">
          <select value={only} onChange={(e) => setOnly(e.target.value)}>
            <option value="">Todas las filas ({preview.rows})</option>
            {outcomes.map((outcome) => (
              <option key={outcome} value={outcome}>
                {outcomeLabel(outcome)} ({preview.items.filter((row) => row.outcome === outcome).length})
              </option>
            ))}
          </select>
        </Field>
      </div>
      <div className="table-wrap">
        <table data-testid="import-rows">
          <thead>
            <tr>
              <th className="num">Fila</th>
              <th>ID fiscal</th>
              <th>Nombre en el archivo</th>
              <th>Razón social que se guarda</th>
              <th>Correos</th>
              <th>Término de pago</th>
              {kind === "customers" ? <th className="num">Límite de crédito (RD$)</th> : null}
              <th>Resultado</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((row) => (
              <tr key={row.row}>
                <td className="num">{row.row}</td>
                <td className="mono">{row.rnc ?? "—"}</td>
                <td className="wrap">{row.name}</td>
                <td className="wrap">
                  {row.legalName ?? "—"}
                  {row.nameDiffers ? <div className="muted">Nombre del padrón de la DGII{row.registryStatus && row.registryStatus !== "ACTIVO" ? ` (${row.registryStatus})` : ""}.</div> : null}
                </td>
                <td className="wrap">{row.emails.length > 0 ? row.emails.join(", ") : "—"}</td>
                <td>{termsText(row.paymentTermsDays)}</td>
                {kind === "customers" ? <td className="num mono">{row.creditLimit ?? "—"}</td> : null}
                <td className="wrap">
                  <StatusBadge status={outcomeStatus(row.outcome)} label={outcomeLabel(row.outcome)} />
                  {row.reasonCode ? <div className="muted">{reasonText(row)}</div> : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

/**
 * IMP-02 (E-IMP-1): the ADM Cloud export of suppliers or customers, in two steps — «Revisar archivo» shows what every row would
 * do and writes nothing; «Importar» does exactly that (step-up). The file is kept through the re-authentication.
 */
export function PartyImportPanel({ kind, onDone, onClose }: { kind: ImportKind; onDone: () => void; onClose: () => void }) {
  const { companyId } = useSession();
  const paths = PATHS[kind];
  const run = useCommand<typeof paths.command, Upload>(`import-${kind}`, paths.command, (response) => importSummary(response.result as unknown as ImportPreview, kind, true));
  const [upload, setUpload] = useState<Upload | null>(run.restored);
  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [result, setResult] = useState<ImportPreview | null>(null);
  const [reviewing, setReviewing] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const fe = useFieldErrors<"file">();
  const noun = kind === "suppliers" ? "proveedores" : "clientes";

  async function review(next: Upload) {
    setReviewing(true);
    setError(null);
    setResult(null);
    try {
      setPreview(await previewQuery(paths.preview, companyId, next));
      setUpload(next);
    } catch (caught) {
      setPreview(null);
      setError(caught);
    } finally {
      setReviewing(false);
    }
  }

  return (
    <div className="card" data-testid={`import-${kind}`}>
      <h2 style={{ marginTop: 0 }}>Importar {noun} desde ADM Cloud</h2>
      <p className="muted">
        Archivo .xlsx o .csv con las columnas «Razón Social» e «ID Fiscal»; también se leen «Teléfono 1», «Correo Electrónico» (varios separados por punto y coma)
        {kind === "customers" ? ", «Término de Pago» y, si la agrega, «Límite de Crédito»" : " y «Término de Pago»"}. Todo entra en borrador
        {kind === "customers" ? ", con sus términos de crédito en borrador para que Crédito los complete y el Controller los apruebe." : "."}
      </p>
      <form
        className="inline-form"
        noValidate
        onSubmit={async (e) => {
          e.preventDefault();
          if (!fe.check({ file: fileProblem(file) }) || !file) {
            return;
          }
          await review({ fileName: file.name, contentBase64: await toBase64(file) });
        }}
      >
        <Field label="Archivo" required error={fe.errors.file} hint="Máximo 5 MB.">
          <input type="file" accept=".xlsx,.csv" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
        </Field>
        <button type="submit" disabled={reviewing}>
          Revisar archivo
        </button>
        <button type="button" onClick={onClose}>
          Cerrar
        </button>
      </form>
      {upload && !preview && !result && run.wasRestored ? (
        <p className="notice">
          El archivo {upload.fileName} sigue listo para importar.{" "}
          <button type="button" disabled={reviewing} onClick={() => review(upload)}>
            Revisarlo de nuevo
          </button>
        </p>
      ) : null}
      <ErrorBox error={error ?? run.error} />
      {preview && upload && !result ? (
        <>
          <p className="notice" data-testid="import-summary">
            {importSummary(preview, kind, false)}
            {preview.ignoredColumns.length > 0 ? ` No se usan las columnas: ${preview.ignoredColumns.join(", ")}.` : ""}
          </p>
          <div className="actions form-actions">
            <button
              type="button"
              className="primary"
              disabled={run.busy || rowsToLoad(preview) === 0}
              onClick={async () => {
                const response = await run.run(upload, upload);
                if (response) {
                  setResult(response.result as unknown as ImportPreview);
                  setPreview(null);
                  setUpload(null);
                  onDone();
                }
              }}
            >
              Importar {rowsToLoad(preview)} {noun}
            </button>
          </div>
          <Rows preview={preview} kind={kind} />
        </>
      ) : null}
      {result ? (
        <>
          <p className="notice" data-testid="import-result">
            {importSummary(result, kind, true)}{" "}
            {kind === "suppliers"
              ? "Quedan en borrador: quien activa proveedores los marca en la lista y usa «Activar seleccionados»."
              : "Quedan en borrador con sus términos: el Controller aprueba los términos y Crédito activa los clientes, desde la lista."}
          </p>
          <Rows preview={result} kind={kind} />
        </>
      ) : null}
    </div>
  );
}
