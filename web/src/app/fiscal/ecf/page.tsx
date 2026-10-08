"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { EcfContingencyNotice, EcfStatusBadge } from "@/components/EcfGateway";
import { Field, Loading, Money, NoPermission } from "@/components/ui";
import { ECF_DOCUMENT_STATUSES, ECF_TYPES, ecfStatusLabel, sourceHref } from "@/lib/ecf";
import { formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// VS4-04 (E-VS4-04-2): Fiscal › e-CF — every e-CF sent through Alanube, newest first, by status or by e-NCF, document or customer.
// The ones needing attention are resolved from their detail.

const PAGE = 50;

function Inbox() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const params = useSearchParams();
  const status = params.get("estado") ?? "";
  const search = params.get("buscar") ?? "";
  const [typed, setTyped] = useState(search);
  const [offset, setOffset] = useState(0);
  const allowed = can("sales:read");
  const { data, error } = useLoad(
    allowed ? () => query("/api/v1/companies/{companyId}/ecf/documents", { path: { companyId }, query: { status, search, limit: PAGE, offset } }) : null,
    [companyId, status, search, offset],
  );
  if (!allowed) {
    return <NoPermission />;
  }
  const go = (next: { estado?: string; buscar?: string }) => {
    const sp = new URLSearchParams();
    const estado = next.estado ?? status;
    const buscar = next.buscar ?? search;
    if (estado) {
      sp.set("estado", estado);
    }
    if (buscar) {
      sp.set("buscar", buscar);
    }
    setOffset(0);
    const text = sp.toString();
    router.push(text ? `/fiscal/ecf/?${text}` : "/fiscal/ecf/");
  };
  return (
    <>
      <h1>e-CF</h1>
      <p className="muted">
        Los comprobantes electrónicos enviados por Alanube. Los que requieren atención se resuelven desde su detalle; los rechazados se reenvían o se anulan desde su factura o nota de
        crédito.
      </p>
      <EcfContingencyNotice />
      <form
        className="inline-form"
        onSubmit={(e) => {
          e.preventDefault();
          go({ buscar: typed.trim() });
        }}
      >
        <Field label="Estado">
          <select aria-label="Estado" value={status} onChange={(e) => go({ estado: e.target.value })}>
            <option value="">Todos</option>
            {ECF_DOCUMENT_STATUSES.map((s) => (
              <option key={s} value={s}>
                {ecfStatusLabel(s)}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Buscar">
          <input value={typed} placeholder="e-NCF, factura o cliente" onChange={(e) => setTyped(e.target.value)} />
        </Field>
        <button type="submit">Buscar</button>
      </form>
      {data === null ? (
        <Loading error={error} />
      ) : (
        <>
          <div className="table-wrap">
            <table data-testid="ecf-inbox">
              <thead>
                <tr>
                  <th>e-NCF</th>
                  <th>Tipo</th>
                  <th>Documento</th>
                  <th>Cliente</th>
                  <th className="num">Total (RD$)</th>
                  <th>Estado</th>
                  <th>Motivo</th>
                  <th>Enviado</th>
                </tr>
              </thead>
              <tbody>
                {data.items.length === 0 ? (
                  <tr>
                    <td colSpan={8} className="muted">
                      No hay e-CF con estos filtros.
                    </td>
                  </tr>
                ) : null}
                {data.items.map((d) => (
                  <tr key={d.documentId} data-testid={`ecf:${d.encf}`}>
                    <td className="mono">
                      <Link href={`/fiscal/ecf/detalle/?id=${d.documentId}`}>{d.encf}</Link>
                      {d.attemptNo > 1 ? <span className="muted"> (intento {d.attemptNo})</span> : null}
                    </td>
                    <td>{ECF_TYPES[d.ecfType] ?? d.ecfType}</td>
                    <td>{d.sourceNo ? <Link href={sourceHref(d.sourceKind, d.sourceId)}>{d.sourceNo}</Link> : "—"}</td>
                    <td className="wrap">{d.partyName ?? "—"}</td>
                    <td className="num">
                      <Money value={d.total} />
                    </td>
                    <td>
                      <EcfStatusBadge status={d.status} />
                    </td>
                    <td className="wrap">{d.reason ?? ""}</td>
                    <td>{formatDateTime(d.createdAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="actions">
            <span className="muted">
              {data.total === 0 ? "0" : `${offset + 1}–${offset + data.items.length}`} de {data.total}
            </span>
            <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>
              Anteriores
            </button>
            <button type="button" disabled={offset + data.items.length >= data.total} onClick={() => setOffset(offset + PAGE)}>
              Siguientes
            </button>
          </div>
        </>
      )}
    </>
  );
}

export default function Page() {
  return (
    <Suspense fallback={<Loading />}>
      <Inbox />
    </Suspense>
  );
}
