"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { bytesToBase64 } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { OPENING_HEADER, openingButtonLabel, openingMissing, openingTemplateCsv } from "@/lib/ux4a-contabilidad";

// VS3-10a (E-VS3-02b-1…10): opening stock of finished goods from a CSV (planta, ubicacion, producto, cantidad, documento) at the
// approved standard cost. The Controller prepares the batch; the Aprobador de políticas posts it (OPEN-INV) on its page.
// UX4-02 (A-18): each column explained, a CSV template to download, and the button says what is still missing.

type Plants = { code: string; name?: string | null; locations: { code: string }[] }[];

/** UX4-02 (A-18): downloads a CSV with the expected header and one example line (built in the browser, nothing is sent). */
function downloadTemplate(plants: Plants | null) {
  const plant = plants?.[0];
  const blob = new Blob([openingTemplateCsv(plant?.code, plant?.locations[0]?.code)], { type: "text/csv;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = "plantilla-apertura.csv";
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

/** UX4-02 (A-18): the file's columns in words, with the plants and locations the company has (when the reader may list them). */
function FileHelp({ plants }: { plants: Plants | null }) {
  return (
    <div className="card" data-testid="opening-help">
      <strong>Cómo preparar el archivo</strong>
      <p className="muted">
        Un archivo CSV (separado por comas, punto decimal) con la primera fila <span className="mono">{OPENING_HEADER}</span> y una fila por
        cada producto contado:
      </p>
      <ul>
        <li>
          <strong>planta</strong>: el código de la planta.
        </li>
        <li>
          <strong>ubicacion</strong>: el código de la ubicación dentro de esa planta donde está hoy el producto (el patio o almacén donde se
          contó).
        </li>
        <li>
          <strong>producto</strong>: el código del producto terminado (debe tener costo estándar aprobado en esa planta).
        </li>
        <li>
          <strong>cantidad</strong>: unidades contadas, mayor que cero (hasta 6 decimales, con punto).
        </li>
        <li>
          <strong>documento</strong>: la referencia del conteo, distinta en cada fila.
        </li>
      </ul>
      {plants && plants.length > 0 ? (
        <p className="muted">
          Códigos válidos:{" "}
          {plants.map((p) => `${p.code}${p.name ? ` (${p.name})` : ""} — ubicaciones ${p.locations.map((l) => l.code).join(", ") || "ninguna"}`).join("; ")}.
        </p>
      ) : null}
      <div className="actions">
        <button type="button" onClick={() => downloadTemplate(plants)}>
          Descargar plantilla CSV
        </button>
      </div>
    </div>
  );
}

function PrepareBatch({ onDone }: { onDone: (batchId: string) => void }) {
  const prepare = useCommand("prepare-opening-inventory", "/api/v1/companies/{companyId}/sales/prepare-opening-inventory");
  const [file, setFile] = useState<File | null>(null);
  const [cutover, setCutover] = useState("");
  const fe = useFieldErrors<"file" | "cutover">();
  return (
    <form
      className="inline-form"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (!fe.check({ file: !file && "Elija el archivo CSV de apertura.", cutover: !cutover && "Indique la fecha de corte." }) || !file) {
          return;
        }
        const response = await prepare.run(
          { fileName: file.name, contentBase64: bytesToBase64(await file.arrayBuffer()), cutoverDate: cutover },
          undefined,
          `Apertura ${file.name} preparada; falta contabilizarla.`,
        );
        if (response) {
          onDone(response.resultRef);
        }
      }}
    >
      <Field label="Archivo CSV" required error={fe.errors.file}>
        <input type="file" accept=".csv,text/csv" aria-label="Archivo de apertura" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
      </Field>
      <Field label="Fecha de corte" required error={fe.errors.cutover}>
        <input type="date" aria-label="Fecha de corte" value={cutover} onChange={(e) => setCutover(e.target.value)} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={prepare.busy || openingMissing(file !== null, cutover).length > 0}>
          {openingButtonLabel(openingMissing(file !== null, cutover))}
        </button>
      </div>
      <ErrorBox error={prepare.error} />
    </form>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const { data: plants } = useLoad(
    can("sales:read") ? async () => (await query("/api/v1/companies/{companyId}/sales/plants", { path: { companyId } })).items : null,
    [companyId],
  );
  const { data, error } = useLoad(
    can("configuration:read") ? () => query("/api/v1/companies/{companyId}/sales/opening-batches", { path: { companyId } }) : null,
    [companyId],
  );
  if (!can("configuration:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Apertura de inventario de producto terminado</h1>
      <p className="muted">El inventario inicial de producto terminado se carga desde un archivo CSV; el valor sale del costo estándar aprobado.</p>
      {can("opening_inventory:prepare") ? <FileHelp plants={plants} /> : null}
      {can("opening_inventory:prepare") ? <PrepareBatch onDone={(batchId) => router.push(`/contabilidad/apertura-lote/?id=${batchId}`)} /> : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay aperturas.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Archivo</th>
              <th>Fecha de corte</th>
              <th className="num">Líneas</th>
              <th className="num">Valor (RD$)</th>
              <th>Estado</th>
              <th>Preparó</th>
              <th>Contabilizó</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((b) => (
              <tr key={b.batchId}>
                <td>
                  <Link href={`/contabilidad/apertura-lote/?id=${b.batchId}`}>{b.fileName}</Link>
                </td>
                <td>{formatDate(b.cutoverDate)}</td>
                <td className="num">{b.lines}</td>
                <td className="num">
                  <Money value={b.total} />
                </td>
                <td>
                  <StatusBadge status={b.status} />
                </td>
                <td className="wrap">{b.preparedBy ?? "—"}</td>
                <td className="wrap">{b.postedBy ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
