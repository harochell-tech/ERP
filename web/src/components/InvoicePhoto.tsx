"use client";

import { useEffect, useState } from "react";
import { Field } from "@/components/ui";

// OCR1-04 (E-OCR1-04-1…4): a photo, scan or PDF of a supplier invoice sent to be read by AI. A photo is reduced on the phone (longer
// side 2400 px, JPEG) so the text stays legible; a PDF goes as it is. When the AI could not read the RNC or the NCF, the person types
// them and the same file is sent again (the server does not read it twice).

/** Whether this server reads invoice photos by AI (the button shows only then). */
export function useOcrEnabled(): boolean {
  const [enabled, setEnabled] = useState(false);
  useEffect(() => {
    let live = true;
    fetch("/api/v1/environment", { credentials: "same-origin" })
      .then((r) => (r.ok ? r.json() : null))
      .then((body: { ocrEnabled?: boolean } | null) => {
        if (live && body?.ocrEnabled) {
          setEnabled(true);
        }
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, []);
  return enabled;
}

const MAX_SIDE = 2400;
const MAX_BYTES = 10 * 1024 * 1024;

async function reduce(file: File): Promise<Blob> {
  const bitmap = await createImageBitmap(file);
  const scale = Math.min(1, MAX_SIDE / Math.max(bitmap.width, bitmap.height));
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(bitmap.width * scale);
  canvas.height = Math.round(bitmap.height * scale);
  canvas.getContext("2d")?.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  return new Promise((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error("No se pudo preparar la foto."))), "image/jpeg", 0.9));
}

async function base64(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return btoa(binary);
}

/** The file ready to send (base64), or a problem in Spanish. */
export async function prepareInvoiceFile(file: File): Promise<{ content: string } | { problem: string }> {
  try {
    const blob = file.type === "application/pdf" ? file : await reduce(file);
    if (blob.size > MAX_BYTES) {
      return { problem: "El archivo pasa de 10 MB." };
    }
    return { content: await base64(blob) };
  } catch {
    return { problem: "No se pudo preparar el archivo: envíe una foto (JPG o PNG) o un PDF." };
  }
}

/** E-OCR1-04-2: the RNC and the NCF the AI could not read, typed by the person. */
export function MissingKeys({
  needs,
  busy,
  onSend,
}: {
  needs: readonly string[];
  busy: boolean;
  onSend: (rnc: string | null, ncf: string | null) => void;
}) {
  const [rnc, setRnc] = useState("");
  const [ncf, setNcf] = useState("");
  return (
    <form
      className="card"
      data-testid="ocr-missing"
      onSubmit={(e) => {
        e.preventDefault();
        onSend(needs.includes("issuerRnc") ? rnc.trim() : null, needs.includes("fiscalNumber") ? ncf.trim().toUpperCase() : null);
      }}
    >
      <p>La IA no pudo leer {needs.length === 2 ? "el RNC ni el NCF" : needs.includes("issuerRnc") ? "el RNC del proveedor" : "el NCF"}. Escríbalo tal como está en la factura:</p>
      {needs.includes("issuerRnc") ? (
        <Field label="RNC o cédula del proveedor" required>
          <input aria-label="RNC del proveedor" inputMode="numeric" value={rnc} onChange={(e) => setRnc(e.target.value)} />
        </Field>
      ) : null}
      {needs.includes("fiscalNumber") ? (
        <Field label="NCF" required>
          <input aria-label="NCF de la factura" placeholder="B0100000001" value={ncf} onChange={(e) => setNcf(e.target.value)} />
        </Field>
      ) : null}
      <div className="actions">
        <button type="submit" className="primary" disabled={busy}>
          Continuar
        </button>
      </div>
    </form>
  );
}
