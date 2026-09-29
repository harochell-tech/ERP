"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Loading, Money, NoPermission } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10b (E-VS3-10-6, E-VS3-05-3): delivered lines not yet invoiced, by customer; Facturación picks the lines of one customer and
// creates a DRAFT invoice (invoice:create). The amounts shown are the server's.

export default function Page() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const create = useCommand("create-invoice", "/api/v1/companies/{companyId}/sales/create-invoice-from-deliveries");
  const [picked, setPicked] = useState<Record<string, boolean>>({});
  const { data, error } = useLoad(can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/billable-deliveries", { path: { companyId } }) : null, [companyId]);
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const customers = [...new Map(data.items.map((l) => [l.partyId, l.customerName])).entries()];
  return (
    <>
      <h1>Por facturar</h1>
      {customers.length === 0 ? <p className="muted">No hay entregas pendientes de facturar.</p> : null}
      {customers.map(([partyId, name]) => {
        const lines = data.items.filter((l) => l.partyId === partyId);
        const chosen = lines.filter((l) => picked[l.deliveryLineId]).map((l) => l.deliveryLineId);
        return (
          <section key={partyId}>
            <h2>{name}</h2>
            <table>
              <thead>
                <tr>
                  <th />
                  <th>Conduce</th>
                  <th>Pedido</th>
                  <th>Producto</th>
                  <th className="num">Por facturar</th>
                  <th className="num">Precio</th>
                  <th className="num">Neto</th>
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
                    <td>{l.itemCode}</td>
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
            </table>
            {can("invoice:create") ? (
              <button
                type="button"
                className="primary"
                disabled={create.busy || chosen.length === 0}
                onClick={async () => {
                  const response = await create.run({ partyId, deliveryLineIds: chosen });
                  if (response) {
                    router.push(`/facturacion/factura/?id=${response.resultRef}`);
                  }
                }}
              >
                Crear factura con {chosen.length} línea(s)
              </button>
            ) : null}
          </section>
        );
      })}
      <ErrorBox error={create.error} />
    </>
  );
}
