"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { ConfirmAction, ErrorBox, Field, Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { AUTHORIZATION_STATUSES, expiredCount } from "@/lib/authorizations";
import { formatDate, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { expiryText, expiryTone } from "@/lib/ux4a-auditoria";
import { allCustomers } from "@/lib/paging";

// FIS1-05 (E-FIS1-05-2): CONFOTUR fiscal authorizations by status and customer (sales:read). Crédito and Facturación register them
// (fiscal_authorization:register); the Especialista fiscal expires those past their validity (fiscal_authorization:suspend, the same
// command the daily process runs). The net amounts are the server's.
// UX4-02 (G-26): "Vence en N días" from the server's daysToExpiry; the expiry button says what it does in plain words.

function ExpireButton({ onDone }: { onDone: () => void }) {
  const expire = useCommand("expire-fiscal-authorizations", "/api/v1/companies/{companyId}/tax/expire-fiscal-authorizations", (r) => {
    const count = expiredCount(r.result);
    return count === 0 ? "No había autorizaciones vencidas." : `Se vencieron ${count} autorización(es) fiscal(es).`;
  });
  const [message, setMessage] = useState<string | null>(null);
  return (
    <span className="inline-form">
      <ConfirmAction
        label="Marcar como vencidas las que pasaron su fecha"
        title="¿Marcar como vencidas las autorizaciones cuya vigencia terminó?"
        consequence="Las autorizaciones cuya fecha «Vigente hasta» ya pasó quedan en estado Vencido y dejan de permitir facturas exentas (e-CF 44). Las vigentes no cambian. No se deshace; el proceso diario hace lo mismo cada madrugada, este botón solo lo adelanta."
        busy={expire.busy}
        onConfirm={async () => {
          setMessage(null);
          const response = await expire.run({});
          if (response) {
            const count = expiredCount(response.result);
            setMessage(count === 0 ? "No había autorizaciones vencidas." : `Se vencieron ${count} autorización(es).`);
            onDone();
          }
        }}
      />
      {message ? (
        <span className="notice" data-testid="expire-result">
          {message}
        </span>
      ) : null}
      <ErrorBox error={expire.error} />
    </span>
  );
}

/** The expiry count matters while the authorization may still be used or activated. */
function showsExpiry(status: string): boolean {
  return status === "ACTIVE" || status === "SUSPENDED" || status === "PENDING_VERIFICATION" || status === "DRAFT";
}

function Authorizations() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const params = useSearchParams();
  const status = params.get("estado") ?? "";
  const partyId = params.get("cliente") ?? "";
  const allowed = can("sales:read");
  const { data, error, reload } = useLoad(
    allowed ? () => query("/api/v1/companies/{companyId}/tax/fiscal-authorizations", { path: { companyId }, query: { status, partyId } }) : null,
    [companyId, status, partyId],
  );
  const customers = useLoad(allowed ? () => allCustomers(companyId) : null, [companyId]);
  if (!allowed) {
    return <NoPermission />;
  }
  const go = (next: { estado?: string; cliente?: string }) => {
    const search = new URLSearchParams();
    const estado = next.estado ?? status;
    const cliente = next.cliente ?? partyId;
    if (estado) {
      search.set("estado", estado);
    }
    if (cliente) {
      search.set("cliente", cliente);
    }
    const text = search.toString();
    router.push(text ? `/fiscal/autorizaciones/?${text}` : "/fiscal/autorizaciones/");
  };
  return (
    <>
      <h1>Autorizaciones fiscales</h1>
      <p className="muted">Certificados de exención de ITBIS (CONFOTUR) de los clientes; una autorización activa permite facturar sus productos con e-CF 44, sin ITBIS.</p>
      <div className="actions">
        {can("fiscal_authorization:register") ? (
          <Link className="button primary" href="/fiscal/autorizaciones/nueva/">
            Registrar autorización
          </Link>
        ) : null}
        {can("fiscal_authorization:suspend") ? <ExpireButton onDone={reload} /> : null}
      </div>
      <div>
        <Field label="Estado">
          <select aria-label="Estado" value={status} onChange={(e) => go({ estado: e.target.value })}>
            <option value="">Todos</option>
            {AUTHORIZATION_STATUSES.map((s) => (
              <option key={s} value={s}>
                {statusLabel(s)}
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
      </div>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay autorizaciones.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Certificado</th>
              <th>Cliente</th>
              <th>Proyecto</th>
              <th>Vigente hasta</th>
              <th>Estado</th>
              <th className="num">Neto autorizado (RD$)</th>
              <th className="num">Neto consumido (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((a) => (
              <tr key={a.authorizationId}>
                <td className="mono">
                  <Link href={`/fiscal/autorizacion/?id=${a.authorizationId}`}>{a.certificateNo}</Link>
                </td>
                <td>
                  {a.customerName} <span className="muted">({a.customerRnc})</span>
                </td>
                <td className="wrap">{a.projectName}</td>
                <td>
                  {formatDate(a.validUntil)}
                  {showsExpiry(a.status) && expiryText(a.daysToExpiry) ? (
                    <>
                      <br />
                      <span className={`badge tone-${expiryTone(a.daysToExpiry)}`} data-testid="expiry">
                        {expiryText(a.daysToExpiry)}
                      </span>
                    </>
                  ) : null}
                </td>
                <td>
                  <StatusBadge status={a.status} />
                </td>
                <td className="num">
                  <Money value={a.netAuthorized} />
                </td>
                <td className="num">
                  <Money value={a.netConsumed} />
                </td>
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
      <Authorizations />
    </Suspense>
  );
}
