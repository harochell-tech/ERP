"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { QuoteStatusBadge } from "@/components/QuoteStatus";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { Field, Money, NoPermission } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { QUOTE_STATUSES, quoteStatusLabel } from "@/lib/quotes";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// QUO1-04 (E-QUO1-04-2): sales quotations by status, customer and "solo vencidas" (sales:read); the Vendedor creates them
// (quote:manage). A SENT quote past its validity reads "Vencida" (the server's flag); the totals are the server's.

function Quotes() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const params = useSearchParams();
  const status = params.get("estado") ?? "";
  const partyId = params.get("cliente") ?? "";
  const expiredOnly = params.get("vencidas") === "1";
  const allowed = can("sales:read");
  const { data, error } = useLoad(
    allowed
      ? () =>
          query("/api/v1/companies/{companyId}/sales/quotes", {
            path: { companyId },
            query: { status: status || undefined, partyId: partyId || undefined, expiredOnly: expiredOnly ? "true" : undefined, limit: 200 },
          })
      : null,
    [companyId, status, partyId, expiredOnly],
  );
  const customers = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/sales/customers", { path: { companyId }, query: { limit: 200 } }) : null, [companyId]);
  if (!allowed) {
    return <NoPermission />;
  }
  const go = (next: { estado?: string; cliente?: string; vencidas?: boolean }) => {
    const search = new URLSearchParams();
    const estado = next.estado ?? status;
    const cliente = next.cliente ?? partyId;
    const vencidas = next.vencidas ?? expiredOnly;
    if (estado) {
      search.set("estado", estado);
    }
    if (cliente) {
      search.set("cliente", cliente);
    }
    if (vencidas) {
      search.set("vencidas", "1");
    }
    const text = search.toString();
    router.push(text ? `/ventas/cotizaciones/?${text}` : "/ventas/cotizaciones/");
  };
  return (
    <>
      <div className="actions" style={{ justifyContent: "space-between" }}>
        <h1>Cotizaciones</h1>
        {can("quote:manage") ? (
          <Link className="button primary" href="/ventas/cotizaciones/nueva/">
            Nueva cotización
          </Link>
        ) : null}
      </div>
      <p className="muted">
        Una cotización es solo una oferta al cliente: no reserva productos, no afecta la contabilidad ni genera factura. Cuando el cliente la acepta (y
        sigue vigente), se convierte en pedido con los mismos precios.
      </p>
      <div>
        <Field label="Estado">
          <select aria-label="Estado" value={status} onChange={(e) => go({ estado: e.target.value })}>
            <option value="">Todos</option>
            {QUOTE_STATUSES.map((s) => (
              <option key={s} value={s}>
                {quoteStatusLabel(s)}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Cliente">
          <select aria-label="Cliente" value={partyId} onChange={(e) => go({ cliente: e.target.value })}>
            <option value="">Todos</option>
            {(customers.data?.items ?? []).map((c) => (
              <option key={c.partyId} value={c.partyId}>
                {c.legalName} {c.rnc ? `(${c.rnc})` : ""}
              </option>
            ))}
          </select>
        </Field>
        <label className="field">
          <span>Solo vencidas</span>
          <input type="checkbox" aria-label="Solo vencidas" checked={expiredOnly} onChange={(e) => go({ vencidas: e.target.checked })} />
        </label>
      </div>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : data.items.length === 0 ? (
        <EmptyState
          title={status || partyId || expiredOnly ? "No hay cotizaciones con ese filtro." : "Todavía no hay cotizaciones."}
          steps={[(status || partyId || expiredOnly) && { href: "/ventas/cotizaciones/", label: "Quitar los filtros" }, can("quote:manage") && { href: "/ventas/cotizaciones/nueva/", label: "Crear una cotización" }]}
        />
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Número</th>
              <th>Cliente</th>
              <th>Fecha</th>
              <th>Vigente hasta</th>
              <th className="num">Total neto (RD$)</th>
              <th>Estado</th>
              <th>Precios</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((q) => (
              <tr key={q.quoteId}>
                <td className="mono">
                  <Link href={`/ventas/cotizacion/?id=${q.quoteId}`}>{q.quoteNo}</Link>
                </td>
                <td className="wrap">{q.customerName}</td>
                <td>{formatDate(q.quoteDate)}</td>
                <td>{formatDate(q.validUntil)}</td>
                <td className="num">
                  <Money value={q.totalNet} />
                </td>
                <td>
                  <QuoteStatusBadge status={q.status} expired={q.expired} />
                </td>
                <td>{q.specialPrices ? <span className="badge tone-attention">Precio especial</span> : <span className="muted">Lista</span>}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Quotes />
    </Suspense>
  );
}
