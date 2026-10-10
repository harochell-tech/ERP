import type { Schemas } from "@/api/client";

// OCR1-03 (E-OCR1-03-1/9, E-OCR-6): words for captured supplier documents — where they came from, what was flagged, the answer to the DGII.

export type SupplierDocument = Schemas["SupplierDocumentDetail"];
export type SupplierDocumentRow = Schemas["SupplierDocumentSummary"];

export const DOCUMENT_TABS: readonly { status: string | null; label: string }[] = [
  { status: "CAPTURED", label: "Pendientes" },
  { status: "REGISTERED", label: "Registrados" },
  { status: "DISCARDED", label: "Descartados" },
  { status: null, label: "Todos" },
];

const STATUS: Readonly<Record<string, string>> = { CAPTURED: "Pendiente", REGISTERED: "Registrado", DISCARDED: "Descartado" };

export function documentStatusLabel(status: string): string {
  return STATUS[status] ?? status;
}

const TYPES: Readonly<Record<string, string>> = {
  "31": "Crédito fiscal",
  "32": "Consumo",
  "33": "Nota de débito",
  "34": "Nota de crédito",
  "41": "Compras",
  "43": "Gastos menores",
  "44": "Régimen especial",
  "45": "Gubernamental",
  "01": "Crédito fiscal",
  "02": "Consumo",
  "03": "Nota de débito",
  "04": "Nota de crédito",
  "11": "Comprobante de compras",
  "13": "Gastos menores",
  "14": "Régimen especial",
  "15": "Gubernamental",
};

export function documentTypeLabel(ecfType: string): string {
  return TYPES[ecfType] ?? `Tipo ${ecfType}`;
}

/** Where the document came from: the supplier's XML through Alanube, a QR read, photos. */
export function sourcesLabel(row: { hasXml: boolean; qrScanned: boolean; images: number }): string {
  const parts = [row.hasXml ? "XML" : null, row.qrScanned ? "QR" : null, row.images > 0 ? (row.images === 1 ? "Foto" : `${row.images} fotos`) : null].filter(Boolean);
  return parts.length > 0 ? parts.join(" · ") : "—";
}

/** E-OCR-3: the answer before the DGII, with whether Alanube already took it. */
export function responseLabel(response: string, sent: boolean, providerId: string | null | undefined = "x"): string {
  if (!providerId) {
    return "—";
  }
  switch (response) {
    case "ACCEPTED":
      return sent ? "Aceptado" : "Aceptado (por enviar)";
    case "REJECTED":
      return sent ? "Rechazado" : "Rechazado (por enviar)";
    default:
      return "Sin responder";
  }
}

const CHECKS: Readonly<Record<string, string>> = {
  LINES_DO_NOT_ADD_UP: "Las líneas más el ITBIS no suman el total del comprobante.",
  RNC_NOT_IN_REGISTRY: "El RNC del emisor no está en el padrón de la DGII cargado en el sistema.",
  QR_TOTAL_DIFFERS: "El total del XML no coincide con el total del QR de la factura impresa.",
  SUPPLIER_NOT_IN_CORE: "El emisor no es un proveedor activo en el sistema: créelo antes de pasar a factura.",
  NOT_RECEIVED: "Alanube no recibió este e-CF (XML inválido): no se puede pasar a factura.",
  NOTE_NOT_REGISTERED: "Nota de crédito o de débito del proveedor: sin registro en el sistema todavía (se le avisa al contador).",
  RESPONSE_UNSENT: "La respuesta a la DGII lleva más de 24 horas sin llegar a Alanube.",
};

export function checkLabel(code: string): string {
  return CHECKS[code] ?? code;
}

const FIELDS: Readonly<Record<string, string>> = {
  issuer_rnc: "RNC del emisor",
  issuer_name: "Nombre del emisor",
  buyer_rnc: "RNC del comprador",
  fiscal_number: "NCF",
  doc_date: "Fecha",
  total_amount: "Total",
  itbis_amount: "ITBIS",
  lines: "Líneas",
};

export function aiFieldLabel(field: string): string {
  return FIELDS[field] ?? field;
}

/** «Pasar a factura» opens the expense or the inventory form with the document's data. */
export function invoiceFormHref(kind: "expense" | "inventory", documentId: string): string {
  return `${kind === "expense" ? "/cxp/facturas/gasto/" : "/cxp/facturas/nueva/"}?documento=${documentId}`;
}

/** A quantity or price from a read line, without the trailing zeros the database keeps ("10.000000" → "10"). */
export function trimDecimal(value: string): string {
  return value.includes(".") ? value.replace(/0+$/, "").replace(/\.$/, "") : value;
}
