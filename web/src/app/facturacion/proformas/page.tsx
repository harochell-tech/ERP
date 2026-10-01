"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { authorizationsFor, certificationLabel, certificationStatus, dueText, proformaStatusLabel } from "@/lib/proformas";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// FIS1b-07 (E-FIS1b-6, E-FIS1b-9, E-FIS1b-01-6): the proformas — goods delivered while the customer's DGII certification is in
// process — by customer, with what each still collects and the state of its certification. Facturación picks whole OPEN proformas
// of one customer and creates the invoice: e-CF 44 with the authorization that cites them, or with ITBIS without it.

const PAGE = 200;

export default function Page() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const create = useCommand("create-invoice-from-proformas", "/api/v1/companies/{companyId}/sales/create-invoice-from-proformas", (_r, doc) =>
    doc ? `Factura ${doc} creada en borrador desde proformas.` : "Factura creada en borrador desde proformas.",
  );
  const [status, setStatus] = useState("OPEN");
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set());
  const [authorization, setAuthorization] = useState<Record<string, string>>({});
  const { data, error } = useLoad(
    can("sales:read")
      ? async () => {
          const [proformas, authorizations] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/proformas", { path: { companyId }, query: { status, limit: PAGE } }),
            query("/api/v1/companies/{companyId}/tax/fiscal-authorizations", { path: { companyId }, query: { status: "ACTIVE" } }),
          ]);
          const details = await Promise.all(
            authorizations.items.map((a) => query("/api/v1/companies/{companyId}/tax/fiscal-authorizations/{authorizationId}", { path: { companyId, authorizationId: a.authorizationId } })),
          );
          return { proformas: proformas.items, authorizations: details.filter((a) => a.proformas.length > 0) };
        }
      : null,
    [companyId, status],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const customers = [...new Map(data.proformas.map((f) => [f.partyId, f.customerName])).entries()].sort((a, b) => a[1].localeCompare(b[1], "es"));
  return (
    <>
      <h1>Proformas</h1>
      <p className="muted">
        Una proforma nace con cada entrega de un pedido marcado «exención en trámite»: documenta lo entregado, vence según los términos del cliente y se cobra antes de la factura
        fiscal. Cuando sale la certificación de la DGII se factura como e-CF 44; si no sale, con ITBIS.
      </p>
      <div className="inline-form">
        <Field label="Estado">
          <select
            value={status}
            onChange={(e) => {
              setStatus(e.target.value);
              setPicked(new Set());
            }}
          >
            <option value="OPEN">Abiertas</option>
            <option value="INVOICED">Facturadas</option>
            <option value="VOIDED">Anuladas</option>
          </select>
        </Field>
      </div>
      {customers.length === 0 ? (
        <EmptyState title={status === "OPEN" ? "No hay proformas abiertas." : "No hay proformas en ese estado."}>
          <p>Marque «exención en trámite» al crear el pedido de un cliente que espera su certificación: cada entrega generará su proforma.</p>
        </EmptyState>
      ) : null}
      {customers.map(([partyId, name]) => {
        const proformas = data.proformas.filter((f) => f.partyId === partyId);
        const chosen = proformas.filter((f) => f.status === "OPEN" && picked.has(f.proformaId)).map((f) => f.proformaId);
        const cited = new Map(data.authorizations.filter((a) => a.header.partyId === partyId).map((a) => [a.header.authorizationId, new Set(a.proformas.map((f) => f.proformaId))]));
        const usable = authorizationsFor(chosen, cited);
        const selected = usable.includes(authorization[partyId] ?? "") ? authorization[partyId] : "";
        return (
          <section key={partyId} data-testid={`proformas:${name}`}>
            <h2>{name}</h2>
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th />
                    <th>Proforma</th>
                    <th>Conduce</th>
                    <th>Fecha</th>
                    <th>Vencimiento</th>
                    <th className="num">Neto (RD$)</th>
                    <th className="num">ITBIS (RD$)</th>
                    <th className="num">Cobrado (RD$)</th>
                    <th className="num">Depósito (RD$)</th>
                    <th className="num">Saldo (RD$)</th>
                    <th>Certificación</th>
                    <th>Estado</th>
                  </tr>
                </thead>
                <tbody>
                  {proformas.map((f) => (
                    <tr key={f.proformaId}>
                      <td>
                        {f.status === "OPEN" && can("invoice:create") ? (
                          <input
                            type="checkbox"
                            aria-label={`Facturar ${f.proformaNo}`}
                            checked={picked.has(f.proformaId)}
                            onChange={(e) => {
                              const next = new Set(picked);
                              if (e.target.checked) {
                                next.add(f.proformaId);
                              } else {
                                next.delete(f.proformaId);
                              }
                              setPicked(next);
                            }}
                          />
                        ) : null}
                      </td>
                      <td className="mono">
                        <Link href={`/facturacion/proforma/?id=${f.proformaId}`}>{f.proformaNo}</Link>
                      </td>
                      <td className="mono">{f.deliveryNo}</td>
                      <td>{formatDate(f.proformaDate)}</td>
                      <td>{dueText(f, formatDate(f.dueDate))}</td>
                      <td className="num">
                        <Money value={f.net} />
                      </td>
                      <td className="num">
                        <Money value={f.itbis} />
                      </td>
                      <td className="num">
                        <Money value={f.allocated} />
                      </td>
                      <td className="num">
                        <Money value={f.deposit} />
                      </td>
                      <td className="num">
                        <Money value={f.balance} />
                      </td>
                      <td>
                        <StatusBadge status={certificationStatus(f.certification)} label={certificationLabel(f.certification)} />
                      </td>
                      <td>
                        <StatusBadge status={f.status === "OPEN" ? "DRAFT" : f.status === "INVOICED" ? "ACTIVE" : "REJECTED"} label={proformaStatusLabel(f.status)} />
                        {f.invoiceNo ? <span className="mono"> {f.invoiceNo}</span> : null}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            {can("invoice:create") && status === "OPEN" ? (
              <div className="inline-form">
                <Field
                  label="Comprobante"
                  hint={chosen.length > 0 && usable.length === 0 ? "Ninguna autorización vigente cita todas las proformas marcadas: se factura con ITBIS." : undefined}
                >
                  <select aria-label={`Comprobante de ${name}`} value={selected} onChange={(e) => setAuthorization({ ...authorization, [partyId]: e.target.value })}>
                    <option value="">Con ITBIS (e-CF 31 / 32)</option>
                    {data.authorizations
                      .filter((a) => usable.includes(a.header.authorizationId))
                      .map((a) => (
                        <option key={a.header.authorizationId} value={a.header.authorizationId}>
                          Exenta e-CF 44 · certificación {a.header.certificateNo}
                        </option>
                      ))}
                  </select>
                </Field>
                <button
                  type="button"
                  className="primary"
                  disabled={create.busy || chosen.length === 0}
                  onClick={async () => {
                    const response = await create.run({ partyId, proformaIds: chosen, fiscalAuthorizationId: selected || null });
                    if (response) {
                      router.push(`/facturacion/factura/?id=${response.resultRef}`);
                    }
                  }}
                >
                  Crear factura con {chosen.length} proforma(s)
                </button>
              </div>
            ) : null}
          </section>
        );
      })}
      <ErrorBox error={create.error} />
    </>
  );
}
