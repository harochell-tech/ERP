"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { ErrorBox, Field, Loading, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatQuantity, isDecimal, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { DELIVERY_TERMS, formatDateTime, statusLabel } from "@/lib/labels";
import { sha256Hex } from "@/lib/ledger";
import { cancellable, nextDeliveryStep } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Delivery = Schemas["DeliveryDetail"];

// VS3-10a (E-VS3-10-5, E-VS3-04-1…15): a delivery and the dispatcher's next step. Weigh tickets and PODs are identified by the
// SHA-256 of their file, computed here; the file is not uploaded (E-VS3-6).

/** A file picker that fills a reference and its SHA-256. */
function Evidence({ label, value, onChange }: { label: string; value: { ref: string; sha256: string }; onChange: (v: { ref: string; sha256: string }) => void }) {
  return (
    <>
      <Field label={label}>
        <input
          type="file"
          aria-label={label}
          onChange={async (e) => {
            const file = e.target.files?.[0];
            if (file) {
              onChange({ ref: file.name, sha256: await sha256Hex(file) });
            }
          }}
        />
      </Field>
      <Field label="Referencia">
        <input value={value.ref} onChange={(e) => onChange({ ...value, ref: e.target.value })} required />
      </Field>
      <Field label="SHA-256">
        <input value={value.sha256} onChange={(e) => onChange({ ...value, sha256: e.target.value })} required pattern="[0-9a-fA-F]{64}" size={66} />
      </Field>
    </>
  );
}

function StartLoading({ delivery, onDone }: { delivery: Delivery; onDone: () => void }) {
  const { companyId } = useSession();
  const id = delivery.header.deliveryId;
  const start = useCommand(`start-loading:${id}`, "/api/v1/companies/{companyId}/sales/start-loading");
  const own = delivery.header.deliveryTermCode === "DELIVERED_OWN_TRANSPORT";
  const [form, setForm] = useState({ vehicleId: "", driverId: "", plate: "", driverName: "" });
  const { data, error } = useLoad(
    own
      ? async () => {
          const [vehicles, drivers] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/vehicles", { path: { companyId }, query: { status: "ACTIVE" } }),
            query("/api/v1/companies/{companyId}/sales/drivers", { path: { companyId }, query: { status: "ACTIVE" } }),
          ]);
          return { vehicles: vehicles.items, drivers: drivers.items };
        }
      : null,
    [companyId, own],
  );
  if (own && data === null) {
    return <Loading error={error} />;
  }
  return (
    <form
      className="inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const body = own
          ? { deliveryId: id, expectedVersion: delivery.header.version, vehicleId: form.vehicleId, driverId: form.driverId, customerVehiclePlate: null, customerDriverName: null }
          : { deliveryId: id, expectedVersion: delivery.header.version, vehicleId: null, driverId: null, customerVehiclePlate: form.plate.trim(), customerDriverName: form.driverName.trim() };
        if (await start.run(body)) {
          onDone();
        }
      }}
    >
      {own && data ? (
        <>
          <Field label="Camión">
            <select value={form.vehicleId} onChange={(e) => setForm({ ...form, vehicleId: e.target.value })} required>
              <option value="">—</option>
              {data.vehicles.map((v) => (
                <option key={v.vehicleId} value={v.vehicleId}>
                  {v.plate} ({formatQuantity(v.capacityKg)} kg)
                </option>
              ))}
            </select>
          </Field>
          <Field label="Chofer">
            <select value={form.driverId} onChange={(e) => setForm({ ...form, driverId: e.target.value })} required>
              <option value="">—</option>
              {data.drivers.map((d) => (
                <option key={d.driverId} value={d.driverId}>
                  {d.fullName}
                </option>
              ))}
            </select>
          </Field>
        </>
      ) : (
        <>
          <Field label="Placa del cliente">
            <input value={form.plate} onChange={(e) => setForm({ ...form, plate: e.target.value })} required />
          </Field>
          <Field label="Chofer del cliente">
            <input value={form.driverName} onChange={(e) => setForm({ ...form, driverName: e.target.value })} required />
          </Field>
        </>
      )}
      <button type="submit" className="primary" disabled={start.busy}>
        Iniciar carga
      </button>
      <ErrorBox error={start.error} />
    </form>
  );
}

function ConfirmLoaded({ delivery, onDone }: { delivery: Delivery; onDone: () => void }) {
  const { companyId } = useSession();
  const id = delivery.header.deliveryId;
  const confirm = useCommand(`confirm-loaded:${id}`, "/api/v1/companies/{companyId}/sales/confirm-loaded");
  const [sources, setSources] = useState<Record<string, string>>({});
  const { data, error } = useLoad(() => query("/api/v1/companies/{companyId}/sales/plants", { path: { companyId } }), [companyId]);
  if (data === null) {
    return <Loading error={error} />;
  }
  const locations = data.items.find((p) => p.code === delivery.header.plantCode)?.locations ?? [];
  return (
    <form
      onSubmit={async (e) => {
        e.preventDefault();
        const lines = delivery.lines.map((l) => ({ deliveryLineId: l.deliveryLineId, sourceLocationId: sources[l.deliveryLineId] ?? locations[0]?.locationId ?? "" }));
        if (await confirm.run({ deliveryId: id, expectedVersion: delivery.header.version, lines })) {
          onDone();
        }
      }}
    >
      <table>
        <thead>
          <tr>
            <th>Producto</th>
            <th className="num">Cantidad</th>
            <th>Sale de</th>
          </tr>
        </thead>
        <tbody>
          {delivery.lines.map((l) => (
            <tr key={l.deliveryLineId}>
              <td>
                {l.itemCode} — {l.itemDescription}
              </td>
              <td className="num">
                {formatQuantity(l.qtyPlanned)} {l.uom}
              </td>
              <td>
                <select aria-label={`Ubicación ${l.itemCode}`} value={sources[l.deliveryLineId] ?? locations[0]?.locationId ?? ""} onChange={(e) => setSources({ ...sources, [l.deliveryLineId]: e.target.value })}>
                  {locations.map((loc) => (
                    <option key={loc.locationId} value={loc.locationId}>
                      {loc.code}
                    </option>
                  ))}
                </select>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <button type="submit" className="primary" disabled={confirm.busy}>
        Confirmar carga
      </button>
      <ErrorBox error={confirm.error} />
    </form>
  );
}

function GateOut({ delivery, onDone }: { delivery: Delivery; onDone: () => void }) {
  const id = delivery.header.deliveryId;
  const gate = useCommand(`gate-out:${id}`, "/api/v1/companies/{companyId}/sales/record-gate-out");
  const [gross, setGross] = useState("");
  const [tare, setTare] = useState("");
  const [ticket, setTicket] = useState({ ref: "", sha256: "" });
  const [invalid, setInvalid] = useState<string | null>(null);
  return (
    <form
      onSubmit={async (e) => {
        e.preventDefault();
        const grossKg = normalizeInput(gross);
        const tareKg = normalizeInput(tare);
        if (!isPositiveDecimal(grossKg, 6) || !isDecimal(tareKg, 6) || tareKg.startsWith("-")) {
          setInvalid("Indique el peso bruto y la tara en kg.");
          return;
        }
        setInvalid(null);
        if (await gate.run({ deliveryId: id, expectedVersion: delivery.header.version, grossKg, tareKg, weighTicketRef: ticket.ref.trim(), weighTicketSha256: ticket.sha256.trim() })) {
          onDone();
        }
      }}
    >
      <Field label="Peso bruto (kg)">
        <input inputMode="decimal" value={gross} onChange={(e) => setGross(e.target.value)} />
      </Field>
      <Field label="Tara (kg)">
        <input inputMode="decimal" value={tare} onChange={(e) => setTare(e.target.value)} />
      </Field>
      <Evidence label="Ticket de báscula" value={ticket} onChange={setTicket} />
      <button type="submit" className="primary" disabled={gate.busy}>
        Registrar pesada y salida
      </button>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={gate.error} />
    </form>
  );
}

function Pod({ delivery, onDone }: { delivery: Delivery; onDone: () => void }) {
  const id = delivery.header.deliveryId;
  const pod = useCommand(`pod:${id}`, "/api/v1/companies/{companyId}/sales/record-pod");
  const [receiver, setReceiver] = useState("");
  const [receivedAt, setReceivedAt] = useState("");
  const [evidence, setEvidence] = useState({ ref: "", sha256: "" });
  const [received, setReceived] = useState<Record<string, string>>(() => Object.fromEntries(delivery.lines.map((l) => [l.deliveryLineId, l.qtyIssued])));
  const [returned, setReturned] = useState<Record<string, string>>({});
  const [exception, setException] = useState("");
  const [invalid, setInvalid] = useState<string | null>(null);
  return (
    <form
      onSubmit={async (e) => {
        e.preventDefault();
        const lines = delivery.lines.map((l) => ({
          deliveryLineId: l.deliveryLineId,
          qtyReceived: normalizeInput(received[l.deliveryLineId] ?? "0"),
          qtyReturned: normalizeInput(returned[l.deliveryLineId] ?? "0") || "0",
        }));
        if (!receivedAt || lines.some((l) => !isDecimal(l.qtyReceived, 6) || !isDecimal(l.qtyReturned, 6))) {
          setInvalid("Indique fecha y hora de recepción y las cantidades recibidas y devueltas.");
          return;
        }
        setInvalid(null);
        if (
          await pod.run({
            deliveryId: id,
            expectedVersion: delivery.header.version,
            receivedByName: receiver.trim(),
            receivedAt: new Date(receivedAt).toISOString(),
            evidenceRef: evidence.ref.trim(),
            evidenceSha256: evidence.sha256.trim(),
            lines,
            exceptionReason: exception.trim() === "" ? null : exception.trim(),
          })
        ) {
          onDone();
        }
      }}
    >
      <Field label="Recibió (nombre)">
        <input value={receiver} onChange={(e) => setReceiver(e.target.value)} required />
      </Field>
      <Field label="Fecha y hora de recepción">
        <input type="datetime-local" value={receivedAt} onChange={(e) => setReceivedAt(e.target.value)} required />
      </Field>
      <Evidence label="Evidencia del POD (foto o firma)" value={evidence} onChange={setEvidence} />
      <table>
        <thead>
          <tr>
            <th>Producto</th>
            <th className="num">Despachado</th>
            <th className="num">Recibido</th>
            <th className="num">Devuelto</th>
          </tr>
        </thead>
        <tbody>
          {delivery.lines.map((l) => (
            <tr key={l.deliveryLineId}>
              <td>
                {l.itemCode} — {l.itemDescription}
              </td>
              <td className="num">{formatQuantity(l.qtyIssued)}</td>
              <td className="num">
                <input aria-label={`Recibido ${l.itemCode}`} inputMode="decimal" value={received[l.deliveryLineId] ?? ""} onChange={(e) => setReceived({ ...received, [l.deliveryLineId]: e.target.value })} />
              </td>
              <td className="num">
                <input aria-label={`Devuelto ${l.itemCode}`} inputMode="decimal" value={returned[l.deliveryLineId] ?? ""} onChange={(e) => setReturned({ ...returned, [l.deliveryLineId]: e.target.value })} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <Field label="Motivo de la excepción (si hubo faltante o devolución)">
        <input value={exception} onChange={(e) => setException(e.target.value)} />
      </Field>
      <button type="submit" className="primary" disabled={pod.busy}>
        Registrar entrega (POD)
      </button>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={pod.error} />
    </form>
  );
}

function DeliveryDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/deliveries/{deliveryId}", { path: { companyId, deliveryId: id } }) : null,
    [companyId, id],
  );
  const returnTrip = useCommand(`return-trip:${id}`, "/api/v1/companies/{companyId}/sales/record-return-trip");
  const cancel = useCommand(`cancel-delivery:${id}`, "/api/v1/companies/{companyId}/sales/cancel-delivery");
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const h = data.header;
  const step = can("delivery:manage") ? nextDeliveryStep(h.status) : null;
  const target = { deliveryId: h.deliveryId, expectedVersion: h.version };
  return (
    <>
      <p>
        <Link href="/despacho/tablero/">← Tablero</Link>
      </p>
      <h1>
        Conduce {h.deliveryNo} <StatusBadge status={h.status} testId="delivery-status" />
      </h1>
      <p>
        Pedido <Link href={`/ventas/pedido/?id=${h.salesOrderId}`}>{h.orderNo}</Link> · {h.customerName} · planta {h.plantCode} ·{" "}
        {DELIVERY_TERMS[h.deliveryTermCode] ?? h.deliveryTermCode}
      </p>
      <p className="muted">
        {data.vehiclePlate ? `Camión ${data.vehiclePlate} · chofer ${data.driverName ?? "—"}` : null}
        {data.customerVehiclePlate ? `Placa del cliente ${data.customerVehiclePlate} · chofer ${data.customerDriverName ?? "—"}` : null}
        {data.grossKg ? ` · bruto ${formatQuantity(data.grossKg)} kg, tara ${formatQuantity(data.tareKg)} kg, ticket ${data.weighTicketRef}` : null}
        {h.gateOutAt ? ` · salió ${formatDateTime(h.gateOutAt)}` : null}
      </p>
      {data.exceptionReason ? <p className="muted">Excepción: {data.exceptionReason}</p> : null}
      {data.cancelReason ? <p className="muted">Cancelado: {data.cancelReason}</p> : null}

      {step === "START_LOADING" ? <StartLoading delivery={data} onDone={reload} /> : null}
      {step === "CONFIRM_LOADED" ? <ConfirmLoaded delivery={data} onDone={reload} /> : null}
      {step === "GATE_OUT" ? <GateOut delivery={data} onDone={reload} /> : null}
      {step === "POD" ? <Pod delivery={data} onDone={reload} /> : null}
      {can("delivery:manage") ? (
        <div className="actions">
          {h.status === "IN_TRANSIT" ? (
            <ReasonAction label="Rechazo total: viaje de regreso" busy={returnTrip.busy} onConfirm={async (reason) => (await returnTrip.run({ ...target, reason })) && reload()} />
          ) : null}
          {cancellable(h.status) ? <ReasonAction label="Cancelar conduce" busy={cancel.busy} onConfirm={async (reason) => (await cancel.run({ ...target, reason })) && reload()} /> : null}
          <ErrorBox error={returnTrip.error ?? cancel.error} />
        </div>
      ) : null}

      <h2>Líneas</h2>
      <table>
        <thead>
          <tr>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Planificado</th>
            <th>Sale de</th>
            <th className="num">Despachado</th>
            <th className="num">Entregado</th>
            <th className="num">Devuelto</th>
            <th className="num">Pérdida</th>
            <th className="num">Facturado</th>
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => (
            <tr key={l.deliveryLineId}>
              <td>
                {l.itemCode} — {l.itemDescription}
                {l.lots.length > 0 ? <div className="muted">{l.lots.map((lot) => `${lot.lotCode}: ${formatQuantity(lot.baseQuantity)}`).join(" · ")}</div> : null}
              </td>
              <td>{l.uom}</td>
              <td className="num">{formatQuantity(l.qtyPlanned)}</td>
              <td>{l.sourceLocationCode ?? "—"}</td>
              <td className="num">{formatQuantity(l.qtyIssued)}</td>
              <td className="num">{formatQuantity(l.qtyDelivered)}</td>
              <td className="num">{formatQuantity(l.qtyReturned)}</td>
              <td className="num">{formatQuantity(l.qtyLost)}</td>
              <td className="num">{formatQuantity(l.qtyInvoiced)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {data.pod ? (
        <>
          <h2>Entrega (POD)</h2>
          <p>
            Recibió {data.pod.receivedByName} el {formatDateTime(data.pod.receivedAt)} · evidencia {data.pod.evidenceRef} ({data.pod.evidenceSha256.slice(0, 12)}…)
          </p>
        </>
      ) : null}
      {data.assessments.length > 0 ? (
        <>
          <h2>Transferencia de control</h2>
          <ul>
            {data.assessments.map((a, i) => (
              <li key={i}>
                {a.triggerPoint === "GATE_OUT" ? "Portón" : "POD"}: {statusLabel(a.result)} · {formatDateTime(a.assessedAt)}
              </li>
            ))}
          </ul>
        </>
      ) : null}
      <History history={data.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <DeliveryDetail />
    </Suspense>
  );
}
