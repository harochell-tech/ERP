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

// VS3-10a (E-VS3-02b-1…10): opening stock of finished goods from a CSV (planta, ubicacion, producto, cantidad, documento) at the
// approved standard cost. The Controller prepares the batch; the Aprobador de políticas posts it (OPEN-INV) on its page.

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
        <input type="date" value={cutover} onChange={(e) => setCutover(e.target.value)} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={prepare.busy}>
          Preparar apertura
        </button>
      </div>
      <ErrorBox error={prepare.error} />
    </form>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const router = useRouter();
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
      <p className="muted">Columnas del archivo: planta, ubicacion, producto, cantidad, documento. El valor sale del costo estándar aprobado.</p>
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
