"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { Field, Money, NoPermission } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// REC-03 (E-REC-03-3/4): the invoices issued in a period, one row per line with net, ITBIS and total; the commercial credit notes of the
// period in negative; totals per product and per customer and the net sale; the same as CSV for Excel.

function firstOfMonth(day: string): string {
  return `${day.slice(0, 8)}01`;
}

type Total = { key: string; name: string; net: string; itbis: string; total: string };

function TotalsTable({ title, rows, testId }: { title: string; rows: Total[]; testId: string }) {
  return (
    <>
      <h2>{title}</h2>
      <div className="table-wrap">
        <table data-testid={testId}>
          <thead>
            <tr>
              <th />
              <th className="num">Neto</th>
              <th className="num">ITBIS</th>
              <th className="num">Total</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((t) => (
              <tr key={`${t.key}:${t.name}`}>
                <td className="wrap">{t.key && t.key !== t.name ? `${t.name} (${t.key})` : t.name}</td>
                <td className="num">
                  <Money value={t.net} />
                </td>
                <td className="num">
                  <Money value={t.itbis} />
                </td>
                <td className="num">
                  <Money value={t.total} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const today = todayInDominicanRepublic();
  const [from, setFrom] = useState(firstOfMonth(today));
  const [to, setTo] = useState(today);
  const { data, error } = useLoad(
    can("sales:read") && from && to ? () => query("/api/v1/companies/{companyId}/sales/sales-report", { path: { companyId }, query: { from, to } }) : null,
    [companyId, from, to],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  const csvHref = `/api/v1/companies/${companyId}/sales/sales-report?from=${from}&to=${to}&format=csv`;
  return (
    <>
      <h1>Reporte de ventas</h1>
      <p className="muted">Las facturas emitidas en el período (sin las anuladas), con las notas de crédito en negativo: el total es la venta neta.</p>
      <div className="filters">
        <Field label="Desde">
          <input type="date" aria-label="Desde" value={from} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label="Hasta">
          <input type="date" aria-label="Hasta" value={to} onChange={(e) => setTo(e.target.value)} />
        </Field>
        <a className="button" href={csvHref} download data-testid="sales-report-csv">
          Descargar CSV (Excel)
        </a>
      </div>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : data.rows.length === 0 ? (
        <EmptyState title="No hay facturas ni notas de crédito en el período." />
      ) : (
        <>
          <TotalsTable title="Resumen" rows={[data.invoices, data.creditNotes, data.netSales]} testId="sales-report-summary" />
          <TotalsTable title="Totales por producto" rows={data.productTotals} testId="sales-report-products" />
          <TotalsTable title="Totales por cliente" rows={data.customerTotals} testId="sales-report-customers" />
          <h2>Detalle</h2>
          <div className="table-wrap">
            <table data-testid="sales-report-rows">
              <thead>
                <tr>
                  <th>Fecha</th>
                  <th>Documento</th>
                  <th>e-NCF</th>
                  <th>Cliente</th>
                  <th>Producto</th>
                  <th className="num">Cantidad</th>
                  <th className="num">Neto</th>
                  <th className="num">ITBIS</th>
                  <th className="num">Total</th>
                </tr>
              </thead>
              <tbody>
                {data.rows.map((r, i) => (
                  <tr key={`${r.documentNo}:${i}`}>
                    <td>{formatDate(r.date)}</td>
                    <td className="mono">
                      {r.kind === "CREDIT_NOTE" ? "NC " : ""}
                      {r.documentNo}
                    </td>
                    <td className="mono">{r.encf ?? "—"}</td>
                    <td className="wrap">{r.customerName}</td>
                    <td>
                      {r.itemCode}
                      {r.lineKind === "FREIGHT" ? " (flete)" : ""}
                    </td>
                    <td className="num">{r.quantity ? `${formatQuantity(r.quantity)} ${r.uom ?? ""}` : "—"}</td>
                    <td className="num">
                      <Money value={r.net} />
                    </td>
                    <td className="num">
                      <Money value={r.itbis} />
                    </td>
                    <td className="num">
                      <Money value={r.total} />
                    </td>
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
