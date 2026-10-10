"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { DeliveryMail } from "@/components/DocumentMail";
import { QrScan } from "@/components/QrScan";
import { SalesHistory } from "@/components/SalesUx4";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, NoPermission, ReasonAction, StatusBadge, useFieldErrors } from "@/components/ui";
import { formatQuantity, isDecimal, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { licenseWarning, vehicleName } from "@/lib/fleet";
import { DELIVERY_TERMS, formatDateTime, statusLabel } from "@/lib/labels";
import { sha256Hex } from "@/lib/ledger";
import { parseRackQr } from "@/lib/lab";
import { cancellable, nextDeliveryStep } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { canSeeAccounting, deliveryLotLabel } from "@/lib/ux4bSales";

type Delivery = Schemas["DeliveryDetail"];

// VS3-10a (E-VS3-10-5, E-VS3-04-1…15): a delivery and the dispatcher's next step. Weigh tickets and PODs are identified by the
// SHA-256 of their file, computed here; the file is not uploaded (E-VS3-6).
// UX3-02 (E-UX3-8 (a)): the SHA-256 is no longer an editable field — choosing the file computes it and the form says so.

interface EvidenceValue {
  ref: string;
  sha256: string;
  fileName: string;
}

const NO_EVIDENCE: EvidenceValue = { ref: "", sha256: "", fileName: "" };

/** Per-field messages of an evidence (reference and file), or nothing when it is valid. */
function evidenceErrors(value: EvidenceValue): { ref?: string; sha256?: string } {
  return {
    ref: value.ref.trim() === "" ? "Escriba la referencia (se propone el nombre del archivo)." : undefined,
    sha256: /^[0-9a-fA-F]{64}$/.test(value.sha256) ? undefined : "Elija el archivo: el sistema calcula su huella al elegirlo.",
  };
}

/** A file picker that computes the file's SHA-256 (hidden) and proposes its name as the reference. */
function Evidence({ label, value, onChange, errors }: { label: string; value: EvidenceValue; onChange: (v: EvidenceValue) => void; errors?: { ref?: string; sha256?: string } }) {
  return (
    <>
      <Field label={label} required error={errors?.sha256} hint="El archivo no se guarda en el sistema; conserve el original.">
        <input
          type="file"
          aria-label={label}
          onChange={async (e) => {
            const file = e.target.files?.[0];
            if (file) {
              onChange({ ref: value.ref.trim() === "" || value.ref === value.fileName ? file.name : value.ref, sha256: await sha256Hex(file), fileName: file.name });
            } else {
              onChange({ ...value, sha256: "", fileName: "" });
            }
          }}
        />
      </Field>
      <Field label="Referencia" required error={errors?.ref}>
        <input value={value.ref} onChange={(e) => onChange({ ...value, ref: e.target.value })} />
      </Field>
      {value.sha256 ? (
        <p className="evidence-verified" data-testid="evidence-verified" title={`SHA-256 ${value.sha256}`}>
          <span className="badge tone-done">✓</span> Huella del archivo verificada: {value.fileName}
        </p>
      ) : null}
    </>
  );
}

function StartLoading({ delivery, onDone }: { delivery: Delivery; onDone: () => void }) {
  const { companyId } = useSession();
  const id = delivery.header.deliveryId;
  const start = useCommand(`start-loading:${id}`, "/api/v1/companies/{companyId}/sales/start-loading", `Conduce ${delivery.header.deliveryNo}: carga iniciada.`);
  const own = delivery.header.deliveryTermCode === "DELIVERED_OWN_TRANSPORT";
  const [form, setForm] = useState({ vehicleId: "", driverId: "", plate: "", driverName: "" });
  const fe = useFieldErrors<"vehicleId" | "driverId" | "plate" | "driverName">();
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
    return <LoadingIndicator error={error} />;
  }
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const valid = own
          ? fe.check({ vehicleId: !form.vehicleId && "Elija el camión.", driverId: !form.driverId && "Elija el chofer." })
          : fe.check({ plate: form.plate.trim() === "" && "Indique la placa del vehículo del cliente.", driverName: form.driverName.trim() === "" && "Indique el nombre del chofer del cliente." });
        if (!valid) {
          return;
        }
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
          <Field label="Camión" required error={fe.errors.vehicleId}>
            <select value={form.vehicleId} onChange={(e) => setForm({ ...form, vehicleId: e.target.value })}>
              <option value="">Seleccione…</option>
              {data.vehicles.map((v) => (
                <option key={v.vehicleId} value={v.vehicleId}>
                  {vehicleName(v)} ({formatQuantity(v.capacityKg)} kg)
                </option>
              ))}
            </select>
          </Field>
          <Field label="Chofer" required error={fe.errors.driverId}>
            <select value={form.driverId} onChange={(e) => setForm({ ...form, driverId: e.target.value })}>
              <option value="">Seleccione…</option>
              {data.drivers.map((d) => (
                <option key={d.driverId} value={d.driverId}>
                  {d.fullName}
                </option>
              ))}
            </select>
          </Field>
          {licenseWarning(data.drivers.find((d) => d.driverId === form.driverId)?.daysToLicenseExpiry) ? (
            <p className="notice" data-testid="license-warning">
              {licenseWarning(data.drivers.find((d) => d.driverId === form.driverId)?.daysToLicenseExpiry)}. Puede despachar; actualice la fecha en Maestros › Vehículos y choferes cuando el
              chofer renueve la licencia.
            </p>
          ) : null}
        </>
      ) : (
        <>
          <Field label="Placa del cliente" required error={fe.errors.plate}>
            <input value={form.plate} onChange={(e) => setForm({ ...form, plate: e.target.value })} />
          </Field>
          <Field label="Chofer del cliente" required error={fe.errors.driverName}>
            <input value={form.driverName} onChange={(e) => setForm({ ...form, driverName: e.target.value })} />
          </Field>
        </>
      )}
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={start.busy}>
          Iniciar carga
        </button>
      </div>
      <ErrorBox error={start.error} />
    </form>
  );
}

type Scan = { lotId: string; rackNo: number | null; code: string | null };

/**
 * LAB1-03 (E-LAB1-03-11/12): the racks scanned for a line, in the order scanned — their lots leave first at the gate-out; the rest is FIFO.
 * The server checks each lot has stock in the line's location and is not blocked.
 */
function RackScans({ itemCode, scans, onChange }: { itemCode: string; scans: Scan[]; onChange: (scans: Scan[]) => void }) {
  const [open, setOpen] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  return (
    <div data-testid={`rack-scans:${itemCode}`}>
      {scans.length > 0 ? (
        <ul className="plain-list">
          {scans.map((s, i) => (
            <li key={`${s.lotId}:${s.rackNo ?? 0}`} data-testid="rack-scan">
              <span className="mono">{s.code ?? `lote ${s.lotId.slice(-6)}`}</span>
              {s.rackNo ? ` · rack ${s.rackNo}` : ""}{" "}
              <button type="button" className="link" onClick={() => onChange(scans.filter((_, j) => j !== i))}>
                Quitar
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <div className="muted">Sin escanear: sale por FIFO.</div>
      )}
      <button type="button" onClick={() => setOpen((o) => !o)}>
        {open ? "Cerrar el escáner" : "Escanear rack"}
      </button>
      {open ? (
        <QrScan
          busy={false}
          testId={`rack-qr:${itemCode}`}
          placeholder="Enlace de la etiqueta del rack"
          onLink={(link) => {
            const read = parseRackQr(link);
            if (!read) {
              setProblem("Ese QR no es la etiqueta de un rack.");
              return;
            }
            setProblem(null);
            setOpen(false);
            if (!scans.some((s) => s.lotId === read.lotId && s.rackNo === read.rackNo)) {
              onChange([...scans, read]);
            }
          }}
        />
      ) : null}
      {problem ? (
        <p className="error" role="alert">
          {problem}
        </p>
      ) : null}
    </div>
  );
}

function ConfirmLoaded({ delivery, onDone }: { delivery: Delivery; onDone: () => void }) {
  const { companyId, plantName } = useSession();
  const id = delivery.header.deliveryId;
  const confirm = useCommand(`confirm-loaded:${id}`, "/api/v1/companies/{companyId}/sales/confirm-loaded", `Conduce ${delivery.header.deliveryNo}: carga confirmada.`);
  const [sources, setSources] = useState<Record<string, string>>({});
  const [scans, setScans] = useState<Record<string, Scan[]>>({});
  const fe = useFieldErrors();
  const { data, error } = useLoad(() => query("/api/v1/companies/{companyId}/sales/plants", { path: { companyId } }), [companyId]);
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const locations = data.items.find((p) => p.code === delivery.header.plantCode)?.locations ?? [];
  return (
    <form
      className="card"
      onSubmit={async (e) => {
        e.preventDefault();
        const lines = delivery.lines.map((l) => ({
          deliveryLineId: l.deliveryLineId,
          sourceLocationId: sources[l.deliveryLineId] ?? locations[0]?.locationId ?? "",
          scans: (scans[l.deliveryLineId] ?? []).map((s) => ({ lotId: s.lotId, rackNo: s.rackNo })),
        }));
        const missing = Object.fromEntries(lines.filter((l) => !l.sourceLocationId).map((l) => [l.deliveryLineId, `No hay ubicación de existencias en la planta ${plantName(delivery.header.plantCode)}.`]));
        if (!fe.check(missing)) {
          return;
        }
        if (await confirm.run({ deliveryId: id, expectedVersion: delivery.header.version, lines })) {
          onDone();
        }
      }}
    >
      <LineTable>
        <thead>
          <tr>
            <th>Producto</th>
            <th className="num">Cantidad</th>
            <th>Ubicación de salida</th>
            <th>Racks escaneados</th>
          </tr>
        </thead>
        <tbody>
          {delivery.lines.map((l) => (
            <tr key={l.deliveryLineId}>
              <td className="wrap">
                {l.itemCode} — {l.itemDescription}
              </td>
              <td className="num">
                {formatQuantity(l.qtyPlanned)} {l.uom}
              </td>
              <td>
                <select
                  aria-label={`Ubicación de salida ${l.itemCode}`}
                  value={sources[l.deliveryLineId] ?? locations[0]?.locationId ?? ""}
                  onChange={(e) => setSources({ ...sources, [l.deliveryLineId]: e.target.value })}
                  {...fieldAria(fe.errors[l.deliveryLineId], `source-${l.deliveryLineId}`, true)}
                >
                  {locations.map((loc) => (
                    <option key={loc.locationId} value={loc.locationId}>
                      {loc.code}
                    </option>
                  ))}
                </select>
                <FieldMessage id={`source-${l.deliveryLineId}`} error={fe.errors[l.deliveryLineId]} />
              </td>
              <td>
                <RackScans itemCode={l.itemCode} scans={scans[l.deliveryLineId] ?? []} onChange={(next) => setScans({ ...scans, [l.deliveryLineId]: next })} />
              </td>
            </tr>
          ))}
        </tbody>
      </LineTable>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={confirm.busy}>
          Confirmar carga
        </button>
      </div>
      <ErrorBox error={confirm.error} />
    </form>
  );
}

function GateOut({ delivery, onDone }: { delivery: Delivery; onDone: () => void }) {
  const id = delivery.header.deliveryId;
  const gate = useCommand(`gate-out:${id}`, "/api/v1/companies/{companyId}/sales/record-gate-out", `Conduce ${delivery.header.deliveryNo}: pesada y salida registradas.`);
  const [gross, setGross] = useState("");
  const [tare, setTare] = useState("");
  const [ticket, setTicket] = useState<EvidenceValue>(NO_EVIDENCE);
  const fe = useFieldErrors<"gross" | "tare" | "ref" | "sha256">();
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const grossKg = normalizeInput(gross);
        const tareKg = normalizeInput(tare);
        if (
          !fe.check({
            gross: !isPositiveDecimal(grossKg, 6) && "Indique el peso bruto en kg (mayor que cero).",
            tare: (!isDecimal(tareKg, 6) || tareKg.startsWith("-")) && "Indique la tara en kg (cero o más).",
            ...evidenceErrors(ticket),
          })
        ) {
          return;
        }
        if (await gate.run({ deliveryId: id, expectedVersion: delivery.header.version, grossKg, tareKg, weighTicketRef: ticket.ref.trim(), weighTicketSha256: ticket.sha256 })) {
          onDone();
        }
      }}
    >
      <Field label="Peso bruto (kg)" required error={fe.errors.gross}>
        <input inputMode="decimal" value={gross} onChange={(e) => setGross(e.target.value)} />
      </Field>
      <Field label="Tara (kg)" required error={fe.errors.tare}>
        <input inputMode="decimal" value={tare} onChange={(e) => setTare(e.target.value)} />
      </Field>
      <Evidence label="Ticket de báscula" value={ticket} onChange={setTicket} errors={{ ref: fe.errors.ref, sha256: fe.errors.sha256 }} />
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={gate.busy}>
          Registrar pesada y salida
        </button>
      </div>
      <ErrorBox error={gate.error} />
    </form>
  );
}

/** A UTC instant as the value of a datetime-local field (the browser's time zone). */
function localInput(utc: string): string {
  const d = new Date(utc);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function Pod({ delivery, onDone }: { delivery: Delivery; onDone: () => void }) {
  const id = delivery.header.deliveryId;
  const pod = useCommand(`pod:${id}`, "/api/v1/companies/{companyId}/sales/record-pod", `Conduce ${delivery.header.deliveryNo}: entrega al cliente registrada.`);
  // ENT1-03 (E-ENT1-01-10): after the driver reported differences, the form starts from what he confirmed — receiver, time and photo.
  const driver = delivery.driverConfirmation?.outcome === "DIFFERENCES" ? delivery.driverConfirmation : null;
  const [receiver, setReceiver] = useState(driver?.receiverName ?? "");
  const [receivedAt, setReceivedAt] = useState(driver ? localInput(driver.confirmedAt) : "");
  const [evidence, setEvidence] = useState<EvidenceValue>(
    driver ? { ref: `Foto del chofer ${delivery.header.deliveryNo}`, sha256: driver.evidenceSha256, fileName: "foto del chofer" } : NO_EVIDENCE,
  );
  const [received, setReceived] = useState<Record<string, string>>(() => Object.fromEntries(delivery.lines.map((l) => [l.deliveryLineId, l.qtyIssued])));
  const [returned, setReturned] = useState<Record<string, string>>({});
  const [exception, setException] = useState("");
  const fe = useFieldErrors();
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const lines = delivery.lines.map((l) => ({
          deliveryLineId: l.deliveryLineId,
          qtyReceived: normalizeInput(received[l.deliveryLineId] ?? "0"),
          qtyReturned: normalizeInput(returned[l.deliveryLineId] ?? "0") || "0",
        }));
        const lineErrors: Record<string, string | undefined> = {};
        for (const l of lines) {
          if (!isDecimal(l.qtyReceived, 6) || l.qtyReceived.startsWith("-")) {
            lineErrors[`received-${l.deliveryLineId}`] = "Cantidad recibida no válida (cero o más).";
          }
          if (!isDecimal(l.qtyReturned, 6) || l.qtyReturned.startsWith("-")) {
            lineErrors[`returned-${l.deliveryLineId}`] = "Cantidad devuelta no válida (cero o más).";
          }
        }
        if (
          !fe.check({
            receiver: receiver.trim() === "" && "Indique quién recibió.",
            receivedAt: !receivedAt && "Indique la fecha y hora de recepción.",
            ...evidenceErrors(evidence),
            ...lineErrors,
          })
        ) {
          return;
        }
        if (
          await pod.run({
            deliveryId: id,
            expectedVersion: delivery.header.version,
            receivedByName: receiver.trim(),
            receivedAt: new Date(receivedAt).toISOString(),
            evidenceRef: evidence.ref.trim(),
            evidenceSha256: evidence.sha256,
            lines,
            exceptionReason: exception.trim() === "" ? null : exception.trim(),
          })
        ) {
          onDone();
        }
      }}
    >
      <Field label="Recibió (nombre)" required error={fe.errors.receiver}>
        <input value={receiver} onChange={(e) => setReceiver(e.target.value)} />
      </Field>
      <Field label="Fecha y hora de recepción" required error={fe.errors.receivedAt}>
        <input type="datetime-local" value={receivedAt} onChange={(e) => setReceivedAt(e.target.value)} />
      </Field>
      {driver ? (
        <p className="notice" data-testid="pod-from-driver">
          El chofer reportó diferencias: «{driver.note}». Escriba lo recibido y lo devuelto; la foto del chofer es la constancia.
        </p>
      ) : (
        <Evidence label="Constancia de entrega firmada (foto o firma)" value={evidence} onChange={setEvidence} errors={{ ref: fe.errors.ref, sha256: fe.errors.sha256 }} />
      )}
      <LineTable>
        <thead>
          <tr>
            <th>Producto</th>
            <th className="num">Despachado</th>
            <th className="num">Recibido</th>
            <th className="num">Devuelto</th>
          </tr>
        </thead>
        <tbody>
          {delivery.lines.map((l) => {
            const receivedError = fe.errors[`received-${l.deliveryLineId}`];
            const returnedError = fe.errors[`returned-${l.deliveryLineId}`];
            return (
              <tr key={l.deliveryLineId}>
                <td className="wrap">
                  {l.itemCode} — {l.itemDescription}
                </td>
                <td className="num">{formatQuantity(l.qtyIssued)}</td>
                <td className="num">
                  <input
                    aria-label={`Recibido ${l.itemCode}`}
                    inputMode="decimal"
                    value={received[l.deliveryLineId] ?? ""}
                    onChange={(e) => setReceived({ ...received, [l.deliveryLineId]: e.target.value })}
                    {...fieldAria(receivedError, `received-${l.deliveryLineId}`, true)}
                  />
                  <FieldMessage id={`received-${l.deliveryLineId}`} error={receivedError} />
                </td>
                <td className="num">
                  <input
                    aria-label={`Devuelto ${l.itemCode}`}
                    inputMode="decimal"
                    value={returned[l.deliveryLineId] ?? ""}
                    onChange={(e) => setReturned({ ...returned, [l.deliveryLineId]: e.target.value })}
                    {...fieldAria(returnedError, `returned-${l.deliveryLineId}`)}
                  />
                  <FieldMessage id={`returned-${l.deliveryLineId}`} error={returnedError} />
                </td>
              </tr>
            );
          })}
        </tbody>
      </LineTable>
      <Field label="Motivo de la excepción (si hubo faltante o devolución)" wide>
        <input value={exception} onChange={(e) => setException(e.target.value)} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={pod.busy}>
          Registrar entrega al cliente
        </button>
      </div>
      <ErrorBox error={pod.error} />
    </form>
  );
}


const LINK_STATE: Record<string, string> = {
  ACTIVE: "Activo: el chofer puede confirmar",
  LOCKED: "Bloqueado: 5 PIN equivocados",
  EXPIRED: "Vencido",
  CONFIRMED: "Usado: el chofer confirmó",
  ANNULLED: "Anulado",
};

/** ENT1-03 (E-ENT-1…5): the driver's link and what the driver confirmed, with the photo or signature from the private store. */
function DriverBlock({ delivery, onDone }: { delivery: Delivery; onDone: () => void }) {
  const { companyId, can } = useSession();
  const id = delivery.header.deliveryId;
  const link = delivery.driverLink;
  const confirmation = delivery.driverConfirmation;
  const reopen = useCommand(`reopen-link:${id}`, "/api/v1/companies/{companyId}/sales/reopen-delivery-link", "Enlace reabierto: reimprima el conduce para el chofer.");
  const [photo, setPhoto] = useState<string | null>(null);
  const [photoError, setPhotoError] = useState<unknown>(null);
  if (!link && !confirmation) {
    return null;
  }
  return (
    <section className="card" data-testid="driver-block">
      <h2 style={{ marginTop: 0 }}>Confirmación del chofer</h2>
      {link ? (
        <p data-testid="driver-link-state">
          Enlace del QR: {LINK_STATE[link.status] ?? link.status} · vence {formatDateTime(link.expiresAt)}
          {link.failedAttempts > 0 ? ` · ${link.failedAttempts} PIN equivocados` : ""}
        </p>
      ) : null}
      {confirmation ? (
        <>
          <p data-testid="driver-confirmation">
            {confirmation.outcome === "FULL" ? "Recibido completo" : "Hubo diferencias"} · recibió {confirmation.receiverName}
            {confirmation.receiverNationalId ? ` (cédula ${confirmation.receiverNationalId})` : ""} el {formatDateTime(confirmation.confirmedAt)}
            {confirmation.note ? ` · «${confirmation.note}»` : ""}
            {confirmation.latitude && confirmation.longitude ? (
              <>
                {" · "}
                <a href={`https://www.google.com/maps?q=${confirmation.latitude},${confirmation.longitude}`} target="_blank" rel="noreferrer">
                  ubicación
                </a>
              </>
            ) : null}
            {confirmation.completedByPod ? " · entrega al cliente registrada" : ""}
          </p>
          {photo ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img className="photo-preview" src={photo} alt="Constancia del chofer" data-testid="driver-evidence" style={{ maxWidth: 360 }} />
          ) : (
            <button
              type="button"
              onClick={async () => {
                try {
                  const file = await query("/api/v1/companies/{companyId}/sales/deliveries/{deliveryId}/driver-evidence", { path: { companyId, deliveryId: id } });
                  setPhoto(`data:${file.contentType};base64,${file.contentBase64}`);
                } catch (e) {
                  setPhotoError(e);
                }
              }}
            >
              Ver {confirmation.evidenceKind === "PHOTO" ? "foto" : "firma"}
            </button>
          )}
          <ErrorBox error={photoError} />
        </>
      ) : null}
      {link && can("delivery_link:reopen") && delivery.header.status === "IN_TRANSIT" && (link.status === "LOCKED" || link.status === "EXPIRED" || link.status === "ACTIVE") ? (
        <div className="actions">
          <button type="button" disabled={reopen.busy} onClick={async () => (await reopen.run({ deliveryId: id })) && onDone()}>
            {link.status === "ACTIVE" ? "Reemplazar QR (conduce perdido)" : "Reabrir enlace"}
          </button>
          <ErrorBox error={reopen.error} />
        </div>
      ) : null}
    </section>
  );
}

function DeliveryDetail() {
  const { companyId, can, plantName } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/deliveries/{deliveryId}", { path: { companyId, deliveryId: id } }) : null,
    [companyId, id],
  );
  const returnTrip = useCommand(`return-trip:${id}`, "/api/v1/companies/{companyId}/sales/record-return-trip", () => `Conduce ${data?.header.deliveryNo ?? ""}: viaje de regreso registrado.`);
  const cancel = useCommand(`cancel-delivery:${id}`, "/api/v1/companies/{companyId}/sales/cancel-delivery", () => `Conduce ${data?.header.deliveryNo ?? ""} cancelado.`);
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
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
        Pedido <Link href={`/ventas/pedido/?id=${h.salesOrderId}`}>{h.orderNo}</Link> · {h.customerName} · planta {plantName(h.plantCode)} ·{" "}
        {DELIVERY_TERMS[h.deliveryTermCode] ?? h.deliveryTermCode}
      </p>
      <p className="actions">
        <Link className="button" href={`/despacho/conduce/imprimir/?id=${h.deliveryId}`}>
          Imprimir conduce
        </Link>
      </p>
      <p className="muted">
        {data.vehiclePlate ? `Camión ${vehicleName({ fleetCode: h.fleetCode, plate: data.vehiclePlate })} · chofer ${data.driverName ?? "—"}` : null}
        {data.customerVehiclePlate ? `Placa del cliente ${data.customerVehiclePlate} · chofer ${data.customerDriverName ?? "—"}` : null}
        {data.grossKg ? ` · bruto ${formatQuantity(data.grossKg)} kg, tara ${formatQuantity(data.tareKg)} kg · Evidencia: ${data.weighTicketRef} (huella verificada)` : null}
        {h.gateOutAt ? ` · salió ${formatDateTime(h.gateOutAt)}` : null}
      </p>
      {data.exceptionReason ? <p className="muted">Excepción: {data.exceptionReason}</p> : null}
      {data.cancelReason ? <p className="muted">Cancelado: {data.cancelReason}</p> : null}

      {step === "START_LOADING" ? <StartLoading delivery={data} onDone={reload} /> : null}
      {step === "CONFIRM_LOADED" ? <ConfirmLoaded delivery={data} onDone={reload} /> : null}
      {step === "GATE_OUT" ? <GateOut delivery={data} onDone={reload} /> : null}
      <DriverBlock delivery={data} onDone={reload} />
      {step === "POD" ? <Pod delivery={data} onDone={reload} /> : null}
      {can("delivery:manage") ? (
        <div className="actions">
          {h.status === "IN_TRANSIT" ? (
            <ReasonAction label="Rechazo total: viaje de regreso" consequence="El cliente rechazó toda la carga: el conduce queda devuelto y la mercancía regresa a la planta. No se puede deshacer." busy={returnTrip.busy} onConfirm={async (reason) => (await returnTrip.run({ ...target, reason })) && reload()} />
          ) : null}
          {cancellable(h.status) ? <ReasonAction label="Cancelar conduce" consequence="El conduce queda cancelado y su cantidad vuelve a quedar pendiente en el pedido. No se puede deshacer." busy={cancel.busy} onConfirm={async (reason) => (await cancel.run({ ...target, reason })) && reload()} /> : null}
          <ErrorBox error={returnTrip.error ?? cancel.error} />
        </div>
      ) : null}

      <h2>Líneas</h2>
      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Planificado</th>
            <th>Ubicación de salida</th>
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
                {l.lots.length > 0 ? <div className="muted">{l.lots.map((lot) => deliveryLotLabel(lot, formatQuantity)).join(" · ")}</div> : null}
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
      </table></div>
      {data.pod ? (
        <>
          <h2>Entrega al cliente</h2>
          <p>
            Recibió {data.pod.receivedByName} el {formatDateTime(data.pod.receivedAt)} ·{" "}
            <span data-testid="pod-evidence" title={`SHA-256 ${data.pod.evidenceSha256}`}>
              Evidencia: {data.pod.evidenceRef} (huella verificada)
            </span>
          </p>
        </>
      ) : null}
      {/* E-UX4-11 (V-23): when the goods became the customer's, for the revenue, is the books' matter: Controller, Contador, Auditor. */}
      {data.assessments.length > 0 && canSeeAccounting(can) ? (
        <>
          <h2>Cuándo la mercancía pasó a ser del cliente (contabilidad)</h2>
          <ul data-testid="control-transfer">
            {data.assessments.map((a, i) => (
              <li key={i}>
                {a.triggerPoint === "GATE_OUT" ? "Al salir por el portón" : "Al entregarla al cliente"}: {statusLabel(a.result)} · {formatDateTime(a.assessedAt)}
              </li>
            ))}
          </ul>
        </>
      ) : null}
      <DeliveryMail deliveryId={h.deliveryId} deliveryNo={h.deliveryNo} blocked={h.gateOutAt ? null : "El conduce se envía por correo después de la salida por portería."} />
      <SalesHistory history={data.history} />
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
