"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { AccountingStatus, Loading, NoPermission } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { formatDate, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

export default function Invoices() {
  const { companyId, can } = useSession();
  const { data, error } = useLoad(
    can("supplier_invoice:read") ? () => query("/api/v1/companies/{companyId}/procurement/supplier-invoices", { path: { companyId }, query: { limit: 200 } }) : null,
    [companyId],
  );

  if (!can("supplier_invoice:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Facturas de proveedor</h1>
      {can("supplier_invoice:register") ? (
        <div className="actions">
          <Link className="button" href="/cxp/facturas/nueva/">
            Registrar factura
          </Link>
        </div>
      ) : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay facturas.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>NCF</th>
              <th>Proveedor</th>
              <th>Fecha</th>
              <th>Vence</th>
              <th className="num">Total</th>
              <th>Estado</th>
              <th>Contabilidad</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((si) => (
              <tr key={si.supplierInvoiceId}>
                <td>
                  <Link href={`/cxp/factura/?id=${si.supplierInvoiceId}`}>{si.supplierFiscalNumber}</Link>
                </td>
                <td>{si.supplierName}</td>
                <td>{formatDate(si.docDate)}</td>
                <td>{formatDate(si.dueDate)}</td>
                <td className="num">{formatDecimal(si.totalAmount)}</td>
                <td>{statusLabel(si.documentStatus)}</td>
                <td>
                  <AccountingStatus status={si.accountingStatus} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
