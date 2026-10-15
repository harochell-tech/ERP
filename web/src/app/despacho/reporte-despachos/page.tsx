"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { Field, NoPermission } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { formatDateTime, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// REC-03 (E-REC-03-1/2): the deliveries that left in a period, one row per line and lot, with truck and driver; totals per product and
// per customer; the same as CSV for Excel.

function firstOfMonth(day: string): string {
  return `${day.slice(0, 8)}01`;
}

export default function Page() {
  const { companyId, can } = useSession();
  const today = todayInDominicanRepublic();
  const [from, setFrom] = useState(firstOfMonth(today));
  const [to, setTo] = useState(today);
  const { data, error } = useLoad(
    can("sales:read") && from && to ? () => query("/api/v1/companies/{companyId}/sales/dispatch-report", { path: { companyId }, query: { from, to } }) : null,
    [companyId, from, to],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  const csvHref = `/api/v1/companies/${companyId}/sales/dispatch-report?from=${from}&to=${to}&format=csv`;
  return (
    <>
      <h1>Reporte de despachos</h1>
      <p className="muted">Los conduces que salieron en el período, por fecha de salida: cliente, producto, lote, camión y chofer.</p>
      <div className="filters">
        <Field label="Desde">
          <input type="date" aria-label="Desde" value={from} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label="Hasta">
          <input type="date" aria-label="Hasta" value={to} onChange={(e) => setTo(e.target.value)} />
        </Field>
        <a className="button" href={csvHref} download data-testid="dispatch-report-csv">
          Descargar CSV (Excel)
        </a>
      </div>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : data.rows.length === 0 ? (
        <EmptyState title="No salió ningún conduce en el período." />
      ) : (
        <>
          <h2>Totales por producto</h2>
          <div className="table-wrap">
            <table data-testid="dispatch-report-products">
              <thead>
                <tr>
                  <th>Producto</th>
                  <th className="num">Cantidad</th>
                  <th>Unidad</th>
                  <th className="num">Conduces</th>
                </tr>
              </thead>
              <tbody>
                {data.productTotals.map((t) => (
                  <tr key={`${t.itemCode}:${t.uom}`}>
                    <td>
                      {t.itemCode} — {t.itemDescription}
                    </td>
                    <td className="num">{formatQuantity(t.quantity)}</td>
                    <td>{t.uom}</td>
                    <td className="num">{t.deliveries}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <h2>Totales por cliente</h2>
          <div className="table-wrap">
            <table data-testid="dispatch-report-customers">
              <thead>
                <tr>
                  <th>Cliente</th>
                  <th className="num">Conduces</th>
                </tr>
              </thead>
              <tbody>
                {data.customerTotals.map((t) => (
                  <tr key={t.customerName}>
                    <td className="wrap">{t.customerName}</td>
                    <td className="num">{t.deliveries}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <h2>Detalle</h2>
          <div className="table-wrap">
            <table data-testid="dispatch-report-rows">
              <thead>
                <tr>
                  <th>Salida</th>
                  <th>Conduce</th>
                  <th>Cliente</th>
                  <th>Producto</th>
                  <th>Lote</th>
                  <th className="num">Cantidad</th>
                  <th>Camión</th>
                  <th>Chofer</th>
                  <th>Entrega</th>
                </tr>
              </thead>
              <tbody>
                {data.rows.map((r, i) => (
                  <tr key={`${r.deliveryNo}:${r.lotCode}:${i}`}>
                    <td>{formatDateTime(r.gateOutAt)}</td>
                    <td className="mono">{r.deliveryNo}</td>
                    <td className="wrap">{r.customerName}</td>
                    <td>{r.itemCode}</td>
                    <td className="mono">{r.lotCode}</td>
                    <td className="num">
                      {formatQuantity(r.quantity)} {r.uom}
                    </td>
                    <td>{r.vehicleFleetCode ? `${r.vehicleFleetCode} · ${r.vehiclePlate ?? ""}` : (r.vehiclePlate ?? "—")}</td>
                    <td>{r.driverName ?? "—"}</td>
                    <td>{r.deliveryTerm === "PICKUP_AT_PLANT" ? "Retiro en planta" : "Entrega en obra"}</td>
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
