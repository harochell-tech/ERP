import qrcode from "qrcode-generator";

// VS4-04 (E-VS4-04-1…6): the e-CF gateway's words on screen — e-CF types, the statuses of an e-CF attempt, the QR of the DGII stamp.

export const ECF_TYPES: Readonly<Record<string, string>> = {
  "31": "31 · Crédito fiscal",
  "32": "32 · Consumo",
  "34": "34 · Nota de crédito",
  "44": "44 · Régimen especial",
};

export const ECF_DOCUMENT_STATUSES = ["REQUIRES_ACTION", "REJECTED", "CONTINGENCY", "UNKNOWN_OUTCOME", "SUBMITTED", "PENDING", "ACCEPTED", "ACCEPTED_CONDITIONAL"] as const;

const DOCUMENT_STATUS: Readonly<Record<string, { label: string; tone: string }>> = {
  PENDING: { label: "En cola", tone: "progress" },
  SUBMITTED: { label: "Enviado, esperando a la DGII", tone: "progress" },
  UNKNOWN_OUTCOME: { label: "Sin respuesta, se consulta", tone: "attention" },
  CONTINGENCY: { label: "En contingencia", tone: "error" },
  ACCEPTED: { label: "Aceptado", tone: "done" },
  ACCEPTED_CONDITIONAL: { label: "Aceptado con observaciones", tone: "done" },
  REJECTED: { label: "Rechazado", tone: "error" },
  REQUIRES_ACTION: { label: "Requiere atención", tone: "attention" },
};

export function ecfStatusLabel(status: string): string {
  return DOCUMENT_STATUS[status]?.label ?? status;
}

export function ecfStatusTone(status: string): string {
  return DOCUMENT_STATUS[status]?.tone ?? "neutral";
}

export const SERIES_STATUSES: Readonly<Record<string, string>> = {
  DRAFT: "Por aprobar",
  ACTIVE: "Vigente",
  CLOSED: "Cerrado",
  CANCELLED: "Anulado",
  DISCARDED: "Descartado",
};

/** The 10 digits of an e-NCF number, from what was typed ("E310000000041", "41" or "0000000041"); null when it is not one. */
export function encfDigits(typed: string, type: string): string | null {
  const text = typed.trim().toUpperCase().replace(new RegExp(`^E${type}`), "");
  return /^\d{1,10}$/.test(text) && Number(text) >= 1 ? text.padStart(10, "0") : null;
}

/** The Alanube call in words. */
export const CALL_OPERATIONS: Readonly<Record<string, string>> = {
  SUBMIT: "Envío",
  QUERY: "Consulta de estado",
  DOWNLOAD: "Descarga de archivo",
  WEBHOOK: "Aviso de Alanube",
  CANCEL: "Anulación",
};

/** The QR of the DGII stamp as dark modules (row-major), drawn as SVG by the page; null without a URL. */
export function stampQr(url: string | null | undefined): { size: number; dark: (row: number, col: number) => boolean } | null {
  if (!url) {
    return null;
  }
  const qr = qrcode(0, "M");
  qr.addData(url, "Byte");
  qr.make();
  return { size: qr.getModuleCount(), dark: (row, col) => qr.isDark(row, col) };
}

/** A base64 file from the server, saved by the browser. */
export function downloadBase64(fileName: string, contentBase64: string, type: string) {
  const bytes = Uint8Array.from(atob(contentBase64), (c) => c.charCodeAt(0));
  const url = URL.createObjectURL(new Blob([bytes], { type }));
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  a.click();
  URL.revokeObjectURL(url);
}

/** The invoice or credit note screen of an e-CF. */
export function sourceHref(kind: string, id: string): string {
  return kind === "CREDIT_NOTE" ? `/facturacion/nota/?id=${id}` : `/facturacion/factura/?id=${id}`;
}
