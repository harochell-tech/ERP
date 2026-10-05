"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, Fragment } from "react";
import { query } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { NoPermission } from "@/components/ui";
import { Watermark } from "@/components/Watermark";
import { formatQuantity } from "@/lib/decimal";
import { DELIVERY_TERMS, formatDate, formatDateTime, statusLabel } from "@/lib/labels";
import { deliveryWatermark } from "@/lib/print";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// UX3-02 (E-UX3-7): the printable delivery note (conduce), letter size, from GetDeliveryPrint — issuer, customer, site, plant,
// numbers and dates, vehicle and driver, weights (the net is the server's), lines with their lots and the signature boxes. Until the
// truck has gone out of the gate it carries the watermark "BORRADOR – NO DESPACHADO". The browser prints it; no PDF is generated.

function DeliveryPrint() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/deliveries/{deliveryId}/print", { path: { companyId, deliveryId: id } }) : null,
    [companyId, id],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const plate = data.vehiclePlate ?? data.customerVehiclePlate;
  const driver = data.driverName ?? data.customerDriverName;
  return (
    <div className="proforma conduce">
      <Watermark text={deliveryWatermark(data.gateOutAt)} />
      <div className="actions no-print">
        <Link href={`/despacho/conduce/?id=${id}`}>← Conduce {data.deliveryNo}</Link>
        <button type="button" className="primary" onClick={() => window.print()}>
          Imprimir
        </button>
      </div>
      <header className="print-head">
        <div>
          <strong>{data.issuerName}</strong>
          <div>
            RNC <span className="mono">{data.issuerRnc}</span>
          </div>
          <div>
            Planta {data.plantName ? `${data.plantName} (${data.plantCode})` : data.plantCode}
          </div>
        </div>
        <div className="print-number">
          <h1>Conduce {data.deliveryNo}</h1>
          <div>
            Pedido <span className="mono">{data.orderNo}</span> del {formatDate(data.orderDate)}
          </div>
          <div className="no-print muted">Estado: {statusLabel(data.status)}</div>
        </div>
      </header>
      <dl className="facts">
        <dt>Cliente</dt>
        <dd data-testid="conduce-customer">
          {data.customerName} · RNC <span className="mono">{data.customerRnc}</span>
        </dd>
        <dt>Entrega</dt>
        <dd>
          {DELIVERY_TERMS[data.deliveryTermCode] ?? data.deliveryTermCode}
          {data.siteAddress ? ` · obra: ${data.siteAddress}` : ""}
        </dd>
        <dt>Planificado el</dt>
        <dd>{formatDate(data.plannedOn)}</dd>
        <dt>Salida por portería</dt>
        <dd data-testid="conduce-gate-out">{data.gateOutAt ? formatDateTime(data.gateOutAt) : "Todavía no ha salido"}</dd>
        <dt>Vehículo y chofer</dt>
        <dd>
          {data.vehicleFleetCode ? <strong data-testid="conduce-ficha">Ficha {data.vehicleFleetCode} · </strong> : null}
          {plate ? `Placa ${plate}` : "—"}
          {driver ? ` · ${driver}` : ""}
          {data.customerVehiclePlate ? " (del cliente)" : ""}
        </dd>
        <dt>Pesada</dt>
        <dd>
          {data.grossKg ? (
            <>
              Bruto {formatQuantity(data.grossKg)} kg · tara {formatQuantity(data.tareKg)} kg · neto <span data-testid="conduce-net">{formatQuantity(data.netKg)}</span> kg
              {data.weighTicketRef ? ` · ticket ${data.weighTicketRef}` : ""}
            </>
          ) : (
            "Sin pesar"
          )}
        </dd>
      </dl>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th className="num">#</th>
              <th>Producto</th>
              <th>Unidad</th>
              <th className="num">Planificado</th>
              <th className="num">Despachado</th>
              <th className="num">Entregado</th>
              <th>Lotes</th>
            </tr>
          </thead>
          <tbody>
            {data.lines.map((l) => (
              <Fragment key={l.lineNo}>
                <tr>
                  <td className="num">{l.lineNo}</td>
                  <td className="wrap">
                    {l.itemCode} — {l.itemDescription}
                  </td>
                  <td>{l.uom}</td>
                  <td className="num">{formatQuantity(l.qtyPlanned)}</td>
                  <td className="num">{formatQuantity(l.qtyIssued)}</td>
                  <td className="num">{formatQuantity(l.qtyDelivered)}</td>
                  <td className="wrap">
                    {l.lots.length === 0 ? "—" : l.lots.map((lot) => `${lot.lotCode} (${lot.sourceLocationCode}): ${formatQuantity(lot.baseQuantity)}`).join(" · ")}
                  </td>
                </tr>
                {l.freight ? (
                  // PRS-05 (E-PRS-04-7): the line's freight, same quantities, no price.
                  <tr data-testid={`conduce-freight:${l.lineNo}`}>
                    <td />
                    <td className="wrap">{l.freight}</td>
                    <td>{l.uom}</td>
                    <td className="num">{formatQuantity(l.qtyPlanned)}</td>
                    <td className="num">{formatQuantity(l.qtyIssued)}</td>
                    <td className="num">{formatQuantity(l.qtyDelivered)}</td>
                    <td>—</td>
                  </tr>
                ) : null}
              </Fragment>
            ))}
          </tbody>
        </table>
      </div>
      {data.receivedByName ? (
        <p>
          Recibido por {data.receivedByName} el {formatDateTime(data.receivedAt)}.
        </p>
      ) : null}
      <div className="signature">
        <div>
          <div className="signature-box" />
          Despachado por
        </div>
        <div>
          <div className="signature-box" />
          Recibido por (nombre, cédula, firma)
        </div>
      </div>
    </div>
  );
}

export default function Page() {
  return (
    <Suspense>
      <DeliveryPrint />
    </Suspense>
  );
}
