"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, Money, NoPermission } from "@/components/ui";
import { formatDecimal, formatQuantity } from "@/lib/decimal";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10b (E-VS3-10-6, E-VS3-05-3): delivered lines not yet invoiced, by customer; Facturación picks the lines of one customer and
// creates a DRAFT invoice (invoice:create). The amounts shown are the server's. FIS1-05 (E-FIS1-05-8): when the customer has ACTIVE
// CONFOTUR authorizations, one may be chosen and the invoice is an e-CF 44 without ITBIS; the server checks that it covers every line.

/** The customer's ACTIVE authorizations, each with what its scope still has available (the server's amounts). */
function AuthorizationChoice({ partyId, value, onChange }: { partyId: string; value: string; onChange: (authorizationId: string) => void }) {
  const { companyId } = useSession();
  const { data } = useLoad(async () => {
    const list = await query("/api/v1/companies/{companyId}/tax/fiscal-authorizations", { path: { companyId }, query: { partyId, status: "ACTIVE" } });
    return Promise.all(list.items.map((a) => query("/api/v1/companies/{companyId}/tax/fiscal-authorizations/{authorizationId}", { path: { companyId, authorizationId: a.authorizationId } })));
  }, [companyId, partyId]);
  if (data === null || data.length === 0) {
    return null;
  }
  return (
    <Field label="Autorización fiscal (e-CF 44)">
      <select aria-label="Autorización fiscal (e-CF 44)" value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Ninguna — con ITBIS</option>
        {data.map((a) => (
          <option key={a.header.authorizationId} value={a.header.authorizationId}>
            {a.header.certificateNo} · disponible {a.lines.map((l) => `${l.itemCode} ${formatQuantity(l.qtyAvailable)} ${l.uom} / ${formatDecimal(l.netAvailable)}`).join(" · ")}
          </option>
        ))}
      </select>
    </Field>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const create = useCommand("create-invoice", "/api/v1/companies/{companyId}/sales/create-invoice-from-deliveries", (_r, doc) => (doc ? `Factura ${doc} creada en borrador.` : "Factura creada en borrador."));
  const [picked, setPicked] = useState<Record<string, boolean>>({});
  const [authorizations, setAuthorizations] = useState<Record<string, string>>({});
  const { data, error } = useLoad(can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/billable-deliveries", { path: { companyId } }) : null, [companyId]);
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const customers = [...new Map(data.items.map((l) => [l.partyId, l.customerName])).entries()];
  return (
    <>
      <h1>Por facturar</h1>
      {customers.length === 0 ? <EmptyState title="No hay entregas pendientes de facturar." steps={[{ href: "/facturacion/facturas/?filtro=draft", label: "Ver facturas en borrador" }, { href: "/despacho/tablero/", label: "Ver el tablero de despacho" }]}><p>Una entrega aparece aquí cuando el cliente la recibe (o sale por el portón si la retira en planta).</p></EmptyState> : null}
      {customers.map(([partyId, name]) => {
        const lines = data.items.filter((l) => l.partyId === partyId);
        const chosen = lines.filter((l) => picked[l.deliveryLineId]).map((l) => l.deliveryLineId);
        return (
          <section key={partyId}>
            <h2>{name}</h2>
            <div className="table-wrap"><table>
              <thead>
                <tr>
                  <th />
                  <th>Conduce</th>
                  <th>Pedido</th>
                  <th>Producto</th>
                  <th className="num">Por facturar</th>
                  <th className="num">Precio (RD$)</th>
                  <th className="num">Neto (RD$)</th>
                </tr>
              </thead>
              <tbody>
                {lines.map((l) => (
                  <tr key={l.deliveryLineId}>
                    <td>
                      <input
                        type="checkbox"
                        aria-label={`Facturar ${l.deliveryNo} ${l.itemCode}`}
                        checked={picked[l.deliveryLineId] ?? false}
                        onChange={(e) => setPicked({ ...picked, [l.deliveryLineId]: e.target.checked })}
                      />
                    </td>
                    <td className="mono">{l.deliveryNo}</td>
                    <td className="mono">{l.orderNo}</td>
                    <td className="wrap">{l.itemCode}</td>
                    <td className="num">
                      {formatQuantity(l.qtyBillable)} {l.uom}
                    </td>
                    <td className="num">
                      <Money value={l.unitPrice} />
                    </td>
                    <td className="num">
                      <Money value={l.billableNet} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table></div>
            {can("invoice:create") ? <AuthorizationChoice partyId={partyId} value={authorizations[partyId] ?? ""} onChange={(id) => setAuthorizations({ ...authorizations, [partyId]: id })} /> : null}
            {can("invoice:create") ? (
              <div className="actions">
              <button
                type="button"
                className="primary"
                disabled={create.busy || chosen.length === 0}
                onClick={async () => {
                  const response = await create.run({ partyId, deliveryLineIds: chosen, fiscalAuthorizationId: authorizations[partyId] || null });
                  if (response) {
                    router.push(`/facturacion/factura/?id=${response.resultRef}`);
                  }
                }}
              >
                Crear factura con {chosen.length} línea(s)
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
