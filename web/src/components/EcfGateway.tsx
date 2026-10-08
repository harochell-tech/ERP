"use client";

import Link from "next/link";
import { query, type Schemas } from "@/api/client";
import { ecfStatusLabel, ecfStatusTone, stampQr } from "@/lib/ecf";
import { formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS4-04 (E-VS4-04-4/6): the e-CF of an invoice or credit note (e-NCF, security code, signature date, the DGII stamp's QR) and the
// contingency notice. The QR is drawn here from the stamp URL; nothing is sent to another service.

export type EcfStamp = Schemas["EcfStampView"];

export function EcfStatusBadge({ status, testId }: { status: string; testId?: string }) {
  return (
    <span className={`badge tone-${ecfStatusTone(status)}`} data-testid={testId}>
      {ecfStatusLabel(status)}
    </span>
  );
}

export function EcfQr({ url, size = 132 }: { url: string | null | undefined; size?: number }) {
  const qr = stampQr(url);
  if (!qr) {
    return null;
  }
  const cells: string[] = [];
  for (let row = 0; row < qr.size; row++) {
    for (let col = 0; col < qr.size; col++) {
      if (qr.dark(row, col)) {
        cells.push(`M${col + 4} ${row + 4}h1v1h-1z`);
      }
    }
  }
  const box = qr.size + 8;
  return (
    <svg width={size} height={size} viewBox={`0 0 ${box} ${box}`} role="img" aria-label="Código QR del timbre de la DGII" data-testid="ecf-qr" shapeRendering="crispEdges">
      <rect width={box} height={box} fill="#fff" />
      <path d={cells.join("")} fill="#000" />
    </svg>
  );
}

/** The document's e-CF: accepted — e-NCF, security code, signature date and QR; otherwise its status and, when there is one, the reason. */
export function EcfStampBlock({ ecf }: { ecf: EcfStamp | null | undefined }) {
  const { can } = useSession();
  if (!ecf) {
    return null;
  }
  const accepted = ecf.status === "ACCEPTED" || ecf.status === "ACCEPTED_CONDITIONAL";
  return (
    <section className="card ecf-stamp" data-testid="ecf-stamp">
      <div>
        <h2 style={{ marginTop: 0 }}>
          e-CF <span className="mono">{ecf.encf}</span> <EcfStatusBadge status={ecf.status} testId="ecf-status" />
        </h2>
        {accepted ? (
          <dl className="facts">
            <dt>Código de seguridad</dt>
            <dd className="mono" data-testid="ecf-security-code">
              {ecf.securityCode}
            </dd>
            <dt>Fecha de firma</dt>
            <dd>{formatDateTime(ecf.signatureDate)}</dd>
          </dl>
        ) : ecf.reason ? (
          <p data-testid="ecf-reason">{ecf.reason}</p>
        ) : (
          <p className="muted">El e-CF va camino a la DGII; esta página se actualiza al recargarla.</p>
        )}
        {ecf.attemptNo > 1 ? <p className="muted">Intento {ecf.attemptNo}: los números de los intentos anteriores quedaron consumidos.</p> : null}
        {can("sales:read") ? <Link href={`/fiscal/ecf/detalle/?id=${ecf.documentId}`}>Ver el e-CF en la bandeja fiscal</Link> : null}
      </div>
      {accepted ? <EcfQr url={ecf.stampUrl} /> : null}
    </section>
  );
}

/** E-VS4-04-6: a red notice while the gateway is in contingency; the alert counts come from the same query. */
export function EcfContingencyNotice() {
  const { companyId, can } = useSession();
  const allowed = can("sales:read");
  const { data } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/ecf/alerts", { path: { companyId } }) : null, [companyId, allowed]);
  if (!data?.inContingency) {
    return null;
  }
  return (
    <div className="notice error" role="alert" data-testid="ecf-contingency">
      <strong>Alanube no responde desde {formatDateTime(data.failingSince)}.</strong> Los e-CF esperan en contingencia y se envían solos cuando vuelva; el despacho
      sigue. No emita estas facturas por otro medio (E-VS4-04-5). <Link href="/fiscal/ecf/?estado=CONTINGENCY">Ver los e-CF en espera</Link>
    </div>
  );
}
