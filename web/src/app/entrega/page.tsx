"use client";

import { useSearchParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useRef, useState } from "react";

// ENT1-03 (E-ENT-1…6, E-ENT1-01-2/3/6/8/9): the driver's page, opened from the QR of the delivery note. No sign-in: the link carries
// the company, the delivery, its generation and Core's HMAC. The driver types his PIN, then who received, a photo (or a signature on
// the screen) and whether everything arrived. Without signal the confirmation waits on the phone and goes when the signal returns.

const API = "/api/v1/public/deliveries";

interface Line {
  ItemCode: string;
  Description: string;
  Quantity: string;
  Uom: string;
}

interface View {
  State: string;
  DeliveryNo: string | null;
  Customer: string | null;
  Site: string | null;
  Driver: string | null;
  Vehicle: string | null;
  Lines: Line[];
  RecordedOutcome: string | null;
}

interface Pending {
  key: string;
  url: string;
  fields: Record<string, string>;
  evidence: Blob;
  fileName: string;
}

const STATE_TEXT: Record<string, string> = {
  INVALID: "Este enlace no es válido. Pida a Despacho que reimprima el conduce.",
  EXPIRED: "Este enlace venció. Pida a Despacho que lo reabra.",
  LOCKED: "Se equivocaron demasiadas veces con el PIN. Pida a Despacho que reabra el enlace.",
  CONFIRMED: "Esta entrega ya fue confirmada.",
  ANNULLED: "Este enlace ya no se usa.",
  RECORDED_BY_DISPATCH: "Esta entrega ya fue registrada por Despacho.",
  THROTTLED: "Demasiados intentos desde este teléfono. Espere una hora o llame a Despacho.",
  NO_PIN: "Usted todavía no tiene PIN. Llame a Despacho.",
  DELIVERED: "Entrega confirmada. Gracias.",
  DIFFERENCES_REPORTED: "Diferencias enviadas a Despacho. Gracias.",
};

// E-ENT-6: the confirmations waiting for signal, in the phone's IndexedDB.
function openQueue(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open("rochell-entregas", 1);
    request.onupgradeneeded = () => request.result.createObjectStore("pendientes", { keyPath: "key" });
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function queueOp<T>(mode: IDBTransactionMode, op: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await openQueue();
  return new Promise((resolve, reject) => {
    const request = op(db.transaction("pendientes", mode).objectStore("pendientes"));
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function send(item: Pending): Promise<Response> {
  const form = new FormData();
  for (const [name, value] of Object.entries(item.fields)) {
    form.append(name, value);
  }
  form.append("evidence", item.evidence, item.fileName);
  return fetch(item.url, { method: "POST", body: form, headers: { "X-Rochell-Csrf": "1", "Idempotency-Key": item.key } });
}

/** E-ENT-5: the photo reduced on the phone — longer side 1600 px, JPEG. */
async function reduce(file: File): Promise<Blob> {
  const bitmap = await createImageBitmap(file);
  const scale = Math.min(1, 1600 / Math.max(bitmap.width, bitmap.height));
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(bitmap.width * scale);
  canvas.height = Math.round(bitmap.height * scale);
  canvas.getContext("2d")?.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  return new Promise((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error("No se pudo preparar la foto."))), "image/jpeg", 0.82));
}

function where(): Promise<GeolocationPosition | null> {
  return new Promise((resolve) => {
    if (!("geolocation" in navigator)) {
      resolve(null);
      return;
    }
    navigator.geolocation.getCurrentPosition(resolve, () => resolve(null), { enableHighAccuracy: true, timeout: 8000, maximumAge: 60000 });
  });
}

function SignaturePad({ onChange }: { onChange: (blob: Blob | null) => void }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const drawing = useRef(false);
  const point = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    return [((e.clientX - box.left) * e.currentTarget.width) / box.width, ((e.clientY - box.top) * e.currentTarget.height) / box.height] as const;
  };
  return (
    <>
      <canvas
        ref={ref}
        className="signature-pad"
        width={600}
        height={240}
        aria-label="Firma de quien recibe"
        onPointerDown={(e) => {
          drawing.current = true;
          const ctx = e.currentTarget.getContext("2d");
          const [x, y] = point(e);
          ctx?.beginPath();
          ctx?.moveTo(x, y);
        }}
        onPointerMove={(e) => {
          if (!drawing.current) {
            return;
          }
          const ctx = e.currentTarget.getContext("2d");
          if (ctx) {
            const [x, y] = point(e);
            ctx.lineWidth = 3;
            ctx.lineCap = "round";
            ctx.lineTo(x, y);
            ctx.stroke();
          }
        }}
        onPointerUp={() => {
          drawing.current = false;
          ref.current?.toBlob((b) => onChange(b), "image/png");
        }}
      />
      <button
        type="button"
        onClick={() => {
          const c = ref.current;
          c?.getContext("2d")?.clearRect(0, 0, c.width, c.height);
          onChange(null);
        }}
      >
        Borrar firma
      </button>
    </>
  );
}

function DriverPage() {
  const params = useSearchParams();
  const c = params.get("c") ?? "";
  const d = params.get("d") ?? "";
  const g = params.get("g") ?? "";
  const k = params.get("k") ?? "";
  const base = `${API}/${c}/${d}`;
  const [view, setView] = useState<View | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [pinOk, setPinOk] = useState(false);
  const [pin, setPin] = useState("");
  const [receiver, setReceiver] = useState("");
  const [nationalId, setNationalId] = useState("");
  const [outcome, setOutcome] = useState<"FULL" | "DIFFERENCES">("FULL");
  const [note, setNote] = useState("");
  const [kind, setKind] = useState<"PHOTO" | "SIGNATURE">("PHOTO");
  const [evidence, setEvidence] = useState<Blob | null>(null);
  const [preview, setPreview] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<string | null>(null);
  const [key] = useState(() => crypto.randomUUID());

  const flush = useCallback(async () => {
    try {
      const waiting = await queueOp<Pending[]>("readonly", (s) => s.getAll() as IDBRequest<Pending[]>);
      for (const item of waiting) {
        const response = await send(item);
        if (response.status < 500) {
          await queueOp("readwrite", (s) => s.delete(item.key));
          if (response.ok && item.url.startsWith(base)) {
            const body = (await response.json()) as { state: string };
            setDone(body.state);
          }
        }
      }
    } catch {
      // still without signal: they stay on the phone
    }
  }, [base]);

  useEffect(() => {
    fetch(`${base}?g=${encodeURIComponent(g)}&k=${encodeURIComponent(k)}`)
      .then(async (r) => (r.ok ? setView((await r.json()) as View) : setView({ State: "INVALID", DeliveryNo: null, Customer: null, Site: null, Driver: null, Vehicle: null, Lines: [], RecordedOutcome: null })))
      .catch(() => setNotice("Sin señal: puede llenar la confirmación; se enviará cuando vuelva la señal."));
    const first = window.setTimeout(() => void flush(), 0);
    window.addEventListener("online", flush);
    return () => {
      window.clearTimeout(first);
      window.removeEventListener("online", flush);
    };
  }, [base, g, k, flush]);

  if (done) {
    return (
      <>
        <h1>Conduce {view?.DeliveryNo ?? ""}</h1>
        <p className="notice" data-testid="driver-done">
          {STATE_TEXT[done] ?? done}
        </p>
      </>
    );
  }
  if (view && view.State !== "ACTIVE") {
    return (
      <>
        <h1>{view.DeliveryNo ? `Conduce ${view.DeliveryNo}` : "Confirmar entrega"}</h1>
        <p className="notice" data-testid="driver-state">
          {STATE_TEXT[view.State] ?? view.State}
        </p>
      </>
    );
  }

  return (
    <>
      <h1>Confirmar entrega {view?.DeliveryNo ? `· conduce ${view.DeliveryNo}` : ""}</h1>
      {notice ? <p className="notice">{notice}</p> : null}
      {view ? (
        <dl className="facts" data-testid="driver-delivery">
          <dt>Cliente</dt>
          <dd>{view.Customer}</dd>
          {view.Site ? (
            <>
              <dt>Obra</dt>
              <dd>{view.Site}</dd>
            </>
          ) : null}
          <dt>Chofer</dt>
          <dd>
            {view.Driver}
            {view.Vehicle ? ` · ${view.Vehicle}` : ""}
          </dd>
          <dt>Carga</dt>
          <dd>
            {view.Lines.map((l) => (
              <div key={l.ItemCode}>
                {l.Quantity} {l.Uom} · {l.Description}
              </div>
            ))}
          </dd>
        </dl>
      ) : null}

      {!pinOk ? (
        <form
          className="card"
          onSubmit={async (e) => {
            e.preventDefault();
            if (!/^[0-9]{4}$/.test(pin)) {
              setNotice("El PIN son 4 números.");
              return;
            }
            setBusy(true);
            try {
              const r = await fetch(`${base}/pin`, {
                method: "POST",
                headers: { "Content-Type": "application/json", "X-Rochell-Csrf": "1" },
                body: JSON.stringify({ generation: Number(g), mac: k, pin }),
              });
              const body = (await r.json()) as { state: string; remaining: number };
              if (body.state === "OK") {
                setPinOk(true);
                setNotice(null);
              } else if (body.state === "WRONG_PIN") {
                setNotice(`PIN equivocado. Le quedan ${body.remaining} intentos.`);
              } else {
                setView((v) => (v ? { ...v, State: body.state } : v));
              }
            } catch {
              setPinOk(true); // without signal: the PIN is checked when the confirmation is sent
              setNotice("Sin señal: el PIN se comprobará al enviar.");
            } finally {
              setBusy(false);
            }
          }}
        >
          <label>
            Su PIN de chofer
            <input value={pin} onChange={(e) => setPin(e.target.value.replace(/\D/g, "").slice(0, 4))} inputMode="numeric" autoComplete="off" type="password" data-testid="driver-pin" />
          </label>
          <button type="submit" className="primary" disabled={busy}>
            Continuar
          </button>
        </form>
      ) : (
        <form
          className="card"
          onSubmit={async (e) => {
            e.preventDefault();
            if (receiver.trim() === "" || !evidence || (outcome === "DIFFERENCES" && note.trim() === "") || (nationalId !== "" && !/^[0-9]{11}$/.test(nationalId))) {
              setNotice("Escriba quién recibió, tome la foto o la firma, y si hubo diferencias diga cuáles. La cédula son 11 números.");
              return;
            }
            setBusy(true);
            const position = await where();
            const fields: Record<string, string> = {
              generation: g,
              mac: k,
              pin,
              receiverName: receiver.trim(),
              receiverNationalId: nationalId,
              outcome,
              note: note.trim(),
              phoneAt: new Date().toISOString(),
              evidenceKind: kind,
              ...(position
                ? { latitude: position.coords.latitude.toFixed(6), longitude: position.coords.longitude.toFixed(6), accuracy: position.coords.accuracy.toFixed(2) }
                : {}),
            };
            const item: Pending = { key, url: `${base}/confirm`, fields, evidence, fileName: kind === "PHOTO" ? "foto.jpg" : "firma.png" };
            try {
              const r = await send(item);
              const body = (await r.json()) as { state?: string; remaining?: number; detail?: string };
              if (!r.ok) {
                setNotice(body.detail ?? "No se pudo confirmar. Revise los datos.");
              } else if (body.state === "WRONG_PIN") {
                setPinOk(false);
                setNotice(`PIN equivocado. Le quedan ${body.remaining} intentos.`);
              } else {
                setDone(body.state ?? "DELIVERED");
              }
            } catch {
              await queueOp("readwrite", (s) => s.put(item));
              setDone(null);
              setNotice("Sin señal: la confirmación quedó guardada en este teléfono y se enviará sola cuando vuelva la señal. No cierre esta página si puede.");
            } finally {
              setBusy(false);
            }
          }}
        >
          <label>
            Quién recibió (nombre)
            <input value={receiver} onChange={(e) => setReceiver(e.target.value)} data-testid="driver-receiver" />
          </label>
          <label>
            Cédula de quien recibió (opcional)
            <input value={nationalId} onChange={(e) => setNationalId(e.target.value.replace(/\D/g, "").slice(0, 11))} inputMode="numeric" />
          </label>
          <fieldset className="choice">
            <legend>¿Llegó todo?</legend>
            <label>
              <input type="radio" name="outcome" checked={outcome === "FULL"} onChange={() => setOutcome("FULL")} /> Recibido completo
            </label>
            <label>
              <input type="radio" name="outcome" checked={outcome === "DIFFERENCES"} onChange={() => setOutcome("DIFFERENCES")} /> Hubo diferencias
            </label>
          </fieldset>
          {outcome === "DIFFERENCES" ? (
            <label>
              Qué fue diferente (rotos, faltantes, devueltos)
              <textarea value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000} data-testid="driver-note" />
            </label>
          ) : null}
          <fieldset className="choice">
            <legend>Constancia</legend>
            <label>
              <input type="radio" name="kind" checked={kind === "PHOTO"} onChange={() => (setKind("PHOTO"), setEvidence(null), setPreview(null))} /> Foto de la entrega
            </label>
            <label>
              <input type="radio" name="kind" checked={kind === "SIGNATURE"} onChange={() => (setKind("SIGNATURE"), setEvidence(null), setPreview(null))} /> Firma en pantalla
            </label>
          </fieldset>
          {kind === "PHOTO" ? (
            <label>
              Tomar foto
              <input
                type="file"
                accept="image/*"
                capture="environment"
                data-testid="driver-photo"
                onChange={async (e) => {
                  const file = e.target.files?.[0];
                  if (file) {
                    try {
                      const blob = await reduce(file);
                      setEvidence(blob);
                      setPreview(URL.createObjectURL(blob));
                    } catch {
                      setNotice("No se pudo leer la foto. Tómela de nuevo.");
                    }
                  }
                }}
              />
              {/* A blob of this phone, nothing to optimise. */}
              {/* eslint-disable-next-line @next/next/no-img-element */}
              {preview ? <img className="photo-preview" src={preview} alt="Foto de la entrega" /> : null}
            </label>
          ) : (
            <>
              <SignaturePad onChange={setEvidence} />
              {evidence ? (
                <p className="muted" data-testid="signature-ready">
                  Firma lista.
                </p>
              ) : null}
            </>
          )}
          <button type="submit" className="primary" disabled={busy} data-testid="driver-confirm">
            {busy ? "Enviando…" : "Confirmar entrega"}
          </button>
        </form>
      )}
      {notice ? (
        <p className="notice" role="status" data-testid="driver-notice">
          {notice}
        </p>
      ) : null}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <DriverPage />
    </Suspense>
  );
}
