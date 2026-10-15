"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { Field, NoPermission } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { formatDateTime, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// REC-01 (E-REC-4): the raw material received in a period — supplier, the supplier's delivery note, our truck and driver, material,
// quantity and location — with totals per material, and the same as CSV for Excel.

function firstOfMonth(day: string): string {
  return `${day.slice(0, 8)}01`;
}

export default function Page() {
  const { companyId, can } = useSession();
  const today = todayInDominicanRepublic();
  const [from, setFrom] = useState(firstOfMonth(today));
  const [to, setTo] = useState(today);
  const { data, error } = useLoad(
    can("goods_receipt:read") && from && to ? () => query("/api/v1/companies/{companyId}/procurement/goods-receipt-report", { path: { companyId }, query: { from, to } }) : null,
    [companyId, from, to],
  );
  if (!can("goods_receipt:read")) {
    return <NoPermission />;
  }
  const csvHref = `/api/v1/companies/${companyId}/procurement/goods-receipt-report?from=${from}&to=${to}&format=csv`;
  return (
    <>
      <h1>Reporte de recepciones</h1>
      <p className="muted">La materia prima recibida en el período, con el conduce del proveedor y el camión y chofer que la trajeron.</p>
      <div className="filters">
        <Field label="Desde">
          <input type="date" aria-label="Desde" value={from} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label="Hasta">
          <input type="date" aria-label="Hasta" value={to} onChange={(e) => setTo(e.target.value)} />
        </Field>
        <a className="button" href={csvHref} download data-testid="receipt-report-csv">
          Descargar CSV (Excel)
        </a>
      </div>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : data.rows.length === 0 ? (
        <EmptyState title="No hay recepciones en el período." />
      ) : (
        <>
          <h2>Totales por material</h2>
          <div className="table-wrap">
            <table data-testid="receipt-report-totals">
              <thead>
                <tr>
                  <th>Material</th>
                  <th className="num">Cantidad</th>
                  <th>Unidad</th>
                  <th className="num">Recepciones</th>
                </tr>
              </thead>
              <tbody>
                {data.totals.map((t) => (
                  <tr key={`${t.itemCode}:${t.uom}`}>
                    <td>
                      {t.itemCode} — {t.itemName}
                    </td>
                    <td className="num">{formatQuantity(t.quantity)}</td>
                    <td>{t.uom}</td>
                    <td className="num">{t.receipts}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <h2>Detalle</h2>
          <div className="table-wrap">
            <table data-testid="receipt-report-rows">
              <thead>
                <tr>
                  <th>Fecha</th>
                  <th>Recepción</th>
                  <th>Proveedor</th>
                  <th>Conduce</th>
                  <th>Camión</th>
                  <th>Chofer</th>
                  <th>Material</th>
                  <th className="num">Cantidad</th>
                  <th>Ubicación</th>
                </tr>
              </thead>
              <tbody>
                {data.rows.map((r, i) => (
                  <tr key={`${r.grNo}:${r.itemCode}:${i}`}>
                    <td>{formatDateTime(r.occurredAt)}</td>
                    <td className="mono">{r.grNo}</td>
                    <td className="wrap">{r.supplierName}</td>
                    <td>{r.supplierDeliveryNote ?? "—"}</td>
                    <td>{r.vehiclePlate ? (r.vehicleFleetCode ? `${r.vehicleFleetCode} · ${r.vehiclePlate}` : r.vehiclePlate) : "—"}</td>
                    <td>{r.driverName ?? "—"}</td>
                    <td>{r.itemCode}</td>
                    <td className="num">
                      {formatQuantity(r.quantity)} {r.uom}
                    </td>
                    <td>{r.locationCode}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </>
  );
}
