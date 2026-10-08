"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { EcfQr, EcfStatusBadge } from "@/components/EcfGateway";
import { ConfirmDialog, ErrorBox, Field, Loading, Money, NoPermission, useFieldErrors } from "@/components/ui";
import { CALL_OPERATIONS, ECF_TYPES, downloadBase64, sourceHref } from "@/lib/ecf";
import { formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS4-04 (E-VS4-04-2/3): one e-CF — its answer, every call to Alanube (never the token), the signed XML and PDF, the other attempts
// of its document, and, when it needs attention, the two ways to resolve it (ecf:resolve, step-up).

type Resolve = { resolution: "IN_ALANUBE" | "NOT_ISSUED"; providerId: string; note: string };

function ResolveForm({ documentId, version, onDone }: { documentId: string; version: number; onDone: () => void }) {
  const resolve = useCommand<"/api/v1/companies/{companyId}/ecf/resolve-ecf-document", Resolve>(
    `resolve-ecf:${documentId}`,
    "/api/v1/companies/{companyId}/ecf/resolve-ecf-document",
  );
  const [v, setV] = useState<Resolve>(resolve.restored ?? { resolution: "IN_ALANUBE", providerId: "", note: "" });
  const [confirming, setConfirming] = useState(false);
  const fe = useFieldErrors<"providerId" | "note">();
  const inAlanube = v.resolution === "IN_ALANUBE";
  return (
    <section className="card" data-testid="ecf-resolve">
      <h2 style={{ marginTop: 0 }}>Resolver</h2>
      <form
        noValidate
        onSubmit={(e) => {
          e.preventDefault();
          if (
            fe.check({
              providerId: inAlanube && !/^[A-Za-z0-9]{1,40}$/.test(v.providerId.trim()) && "Copie el id del documento tal como lo muestra el portal de Alanube.",
              note: (v.note.trim().length < 10 || v.note.trim().length > 300) && "Explique lo que verificó (10 a 300 caracteres).",
            })
          ) {
            setConfirming(true);
          }
        }}
      >
        <fieldset>
          <legend>¿Qué encontró en el portal de Alanube?</legend>
          <label>
            <input type="radio" name="resolution" checked={inAlanube} onChange={() => setV({ ...v, resolution: "IN_ALANUBE" })} /> Está en Alanube: volver a consultar su estado con su
            id
          </label>
          <label>
            <input type="radio" name="resolution" checked={!inAlanube} onChange={() => setV({ ...v, resolution: "NOT_ISSUED" })} /> No se emitió: cerrar este intento para reenviar o
            anular el documento
          </label>
        </fieldset>
        {inAlanube ? (
          <Field label="Id del documento en Alanube" required error={fe.errors.providerId}>
            <input className="mono" value={v.providerId} onChange={(e) => setV({ ...v, providerId: e.target.value })} />
          </Field>
        ) : null}
        <Field label="Qué verificó" required error={fe.errors.note} wide>
          <textarea value={v.note} maxLength={300} rows={2} onChange={(e) => setV({ ...v, note: e.target.value })} />
        </Field>
        <div className="actions form-actions">
          <button type="submit" className="primary" disabled={resolve.busy}>
            Resolver
          </button>
        </div>
        <ErrorBox error={resolve.error} />
      </form>
      <ConfirmDialog
        open={confirming}
        title="¿Resolver este e-CF?"
        confirmLabel="Confirmar: Resolver"
        stepUp
        busy={resolve.busy}
        onCancel={() => setConfirming(false)}
        onConfirm={async () => {
          setConfirming(false);
          const body = { documentId, expectedVersion: version, resolution: v.resolution, providerId: inAlanube ? v.providerId.trim() : null, note: v.note.trim() };
          if (await resolve.run(body, v, inAlanube ? "El e-CF vuelve a consultarse en Alanube." : "Intento cerrado: el documento queda para reenviar o anular.")) {
            onDone();
          }
        }}
      >
        <p>
          {inAlanube
            ? "El sistema seguirá este e-CF en Alanube con el id indicado hasta la respuesta de la DGII."
            : "El intento queda rechazado y su e-NCF anotado para anularlo ante la DGII al cerrar el rango; la factura o nota de crédito queda para reenviar con otro número o anular."}
        </p>
      </ConfirmDialog>
    </section>
  );
}

function Detail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const allowed = can("sales:read");
  const { data, error, reload } = useLoad(
    allowed && id ? () => query("/api/v1/companies/{companyId}/ecf/documents/{documentId}", { path: { companyId, documentId: id } }) : null,
    [companyId, id],
  );
  const [downloadError, setDownloadError] = useState<unknown>(null);
  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const d = data.document;
  const download = async (fileId: string) => {
    setDownloadError(null);
    try {
      const file = await query("/api/v1/companies/{companyId}/ecf/files/{fileId}", { path: { companyId, fileId } });
      downloadBase64(file.fileName, file.contentBase64, file.kind === "PDF" ? "application/pdf" : "application/xml");
    } catch (e) {
      setDownloadError(e);
    }
  };
  return (
    <>
      <p>
        <Link href="/fiscal/ecf/">← e-CF</Link>
      </p>
      <h1>
        e-CF <span className="mono">{d.encf}</span> <EcfStatusBadge status={d.status} testId="ecf-detail-status" />
      </h1>
      <div className="ecf-stamp">
        <dl className="facts">
          <dt>Tipo</dt>
          <dd>{ECF_TYPES[d.ecfType] ?? d.ecfType}</dd>
          <dt>Documento</dt>
          <dd>{d.sourceNo ? <Link href={sourceHref(d.sourceKind, d.sourceId)}>{d.sourceNo}</Link> : "—"}</dd>
          <dt>Cliente</dt>
          <dd>{d.partyName ?? "—"}</dd>
          <dt>Total</dt>
          <dd>
            <Money value={d.total} currency />
          </dd>
          <dt>Intento</dt>
          <dd>{d.attemptNo}</dd>
          <dt>Puesto en cola</dt>
          <dd>{formatDateTime(d.createdAt)}</dd>
          {d.reason ? (
            <>
              <dt>Motivo</dt>
              <dd data-testid="ecf-detail-reason">{d.reason}</dd>
            </>
          ) : null}
          {data.securityCode ? (
            <>
              <dt>Código de seguridad</dt>
              <dd className="mono">{data.securityCode}</dd>
              <dt>Fecha de firma</dt>
              <dd>{formatDateTime(data.signatureDate)}</dd>
            </>
          ) : null}
          <dt>Id en Alanube</dt>
          <dd className="mono">{data.providerId ?? "—"}</dd>
          {data.nextPollAt ? (
            <>
              <dt>Próxima consulta</dt>
              <dd>{formatDateTime(data.nextPollAt)}</dd>
            </>
          ) : null}
        </dl>
        <EcfQr url={data.stampUrl} />
      </div>
      {d.status === "REQUIRES_ACTION" && can("ecf:resolve") ? <ResolveForm documentId={d.documentId} version={d.version} onDone={reload} /> : null}

      <h2>Archivos firmados</h2>
      {data.files.length === 0 ? (
        <p className="muted">Todavía no hay archivos: se descargan de Alanube cuando la DGII acepta el e-CF.</p>
      ) : (
        <div className="actions">
          {data.files.map((f) => (
            <button key={f.fileId} type="button" onClick={() => download(f.fileId)} data-testid={`ecf-file:${f.kind}`}>
              Descargar {f.kind}
            </button>
          ))}
        </div>
      )}
      <ErrorBox error={downloadError} />

      <h2>Llamadas a Alanube</h2>
      <div className="table-wrap">
        <table data-testid="ecf-calls">
          <thead>
            <tr>
              <th>Cuándo</th>
              <th>Qué</th>
              <th>Modo</th>
              <th>Resultado</th>
              <th>Código</th>
              <th>Mensaje</th>
              <th className="num">ms</th>
            </tr>
          </thead>
          <tbody>
            {data.calls.map((c, i) => (
              <tr key={`${c.calledAt}:${i}`}>
                <td>{formatDateTime(c.calledAt)}</td>
                <td>{CALL_OPERATIONS[c.operation] ?? c.operation}</td>
                <td>{c.mode}</td>
                <td>
                  {c.outcome}
                  {c.httpStatus ? ` (${c.httpStatus})` : ""}
                </td>
                <td className="mono">{c.providerCode ?? ""}</td>
                <td className="wrap">{c.message ?? ""}</td>
                <td className="num">{c.durationMs}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {data.otherAttempts.length > 0 ? (
        <>
          <h2>Otros intentos del documento</h2>
          <ul>
            {data.otherAttempts.map((o) => (
              <li key={o.documentId}>
                <Link href={`/fiscal/ecf/detalle/?id=${o.documentId}`}>{o.encf}</Link> — intento {o.attemptNo} — <EcfStatusBadge status={o.status} />
              </li>
            ))}
          </ul>
        </>
      ) : null}
      {data.governmentResponse ? (
        <details>
          <summary>Respuesta de la DGII (técnica)</summary>
          <pre className="mono wrap">{data.governmentResponse}</pre>
        </details>
      ) : null}
    </>
  );
}

export default function Page() {
  return (
    <Suspense fallback={<Loading />}>
      <Detail />
    </Suspense>
  );
}
