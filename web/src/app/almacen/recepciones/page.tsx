"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { AccountingStatus, Loading, NoPermission } from "@/components/ui";
import { formatDateTime, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

export default function Receipts() {
  const { companyId, can, plantFor } = useSession();
  const plantId = plantFor("goods_receipt:read");
  const { data, error } = useLoad(
    can("goods_receipt:read") ? () => query("/api/v1/companies/{companyId}/procurement/goods-receipts", { path: { companyId }, query: { plantId, limit: 200 } }) : null,
    [companyId, plantId],
  );

  if (!can("goods_receipt:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Recepciones</h1>
      <p className="muted">Para recibir, abra una orden aprobada desde Órdenes de compra.</p>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay recepciones.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Número</th>
              <th>Orden</th>
              <th>Ubicación</th>
              <th>Fecha y hora</th>
              <th>Estado</th>
              <th>Contabilidad</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((gr) => (
              <tr key={gr.goodsReceiptId}>
                <td>
                  <Link href={`/almacen/recepcion/?id=${gr.goodsReceiptId}`}>{gr.grNo}</Link>
                </td>
                <td>{gr.poNo}</td>
                <td>{gr.locationCode}</td>
                <td>{formatDateTime(gr.occurredAt)}</td>
                <td>{statusLabel(gr.documentStatus)}</td>
                <td>
                  <AccountingStatus status={gr.accountingStatus} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
