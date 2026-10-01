// IMP-02 (E-IMP-1…11): the bulk load of suppliers and customers — what each row of the file would do, in words, and the e-mail
// lists of a party (E-IMP-6). Pure, unit-tested in tests/unit/partyImport.test.ts.
import type { Schemas } from "@/api/client";

export type ImportRow = Schemas["PartyImportRow"];
export type ImportPreview = Schemas["PartyImportPreview"];
export type ImportKind = "suppliers" | "customers";

/** E-IMP-01-1: the file travels base64, at most 5 MB. */
export const MAX_IMPORT_BYTES = 5 * 1024 * 1024;

/** E-IMP-01-3: a party keeps at most ten e-mails. */
export const MAX_EMAILS = 10;

const EMAIL = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

const OUTCOMES: Record<string, string> = {
  CREATE: "Se crea",
  LINK: "Proveedor que pasa a ser también cliente",
  EXISTS: "Ya existe",
  DUPLICATE: "Repetido en el archivo",
  REJECTED: "No se carga",
};

const REASONS: Record<string, string> = {
  NAME_INVALID: "La razón social falta o pasa de 200 caracteres.",
  ID_MISSING: "No tiene RNC ni cédula.",
  ID_INVALID: "El ID fiscal no tiene 9 dígitos (RNC) ni 11 (cédula).",
  PHONE_INVALID: "El teléfono pasa de 30 caracteres.",
  EMAIL_INVALID: "Un correo no tiene formato válido, o hay más de diez.",
  TERMS_INVALID: "El término de pago no es «Contado» ni «N días» (hasta 365).",
  CREDIT_LIMIT_INVALID: "El límite de crédito no es un monto de cero o más, con hasta 2 decimales.",
  DUPLICATE_IN_FILE: "El mismo RNC aparece antes en el archivo.",
  ALREADY_SUPPLIER: "Ya es proveedor.",
  ALREADY_CUSTOMER: "Ya es cliente.",
  CUSTOMER_ONLY: "Existe solo como cliente; esta carga no lo convierte en proveedor.",
};

export function outcomeLabel(outcome: string): string {
  return OUTCOMES[outcome] ?? outcome;
}

/** Why a row is not loaded, in words; the server's own text when the code is new to the screen. */
export function reasonText(row: Pick<ImportRow, "reasonCode" | "reason">): string {
  return row.reasonCode ? (REASONS[row.reasonCode] ?? row.reason ?? row.reasonCode) : "";
}

/** The badge tone of a row: what loads reads as good, what exists as neutral, what is left out as a warning. */
export function outcomeStatus(outcome: string): string {
  return outcome === "CREATE" || outcome === "LINK" ? "ACTIVE" : outcome === "REJECTED" ? "REJECTED" : "DRAFT";
}

export function termsText(days: number | null | undefined): string {
  return days === null || days === undefined ? "—" : days === 0 ? "Contado" : `${days} días`;
}

/** How many rows the import will write. */
export function rowsToLoad(preview: Pick<ImportPreview, "toCreate" | "toLink">): number {
  return preview.toCreate + preview.toLink;
}

/** One sentence for the preview and for the result ("se cargarán" before, "se cargaron" after). */
export function importSummary(preview: ImportPreview, kind: ImportKind, done: boolean): string {
  const noun = kind === "suppliers" ? "proveedores" : "clientes";
  const parts = [`${preview.toCreate} ${noun} nuevos`];
  if (preview.toLink > 0) {
    parts.push(`${preview.toLink} proveedores que pasan a ser también clientes`);
  }
  const left = [
    preview.existing > 0 && `${preview.existing} ya existen`,
    preview.duplicates > 0 && `${preview.duplicates} repetidos en el archivo`,
    preview.rejected > 0 && `${preview.rejected} no se pueden cargar`,
  ].filter((x): x is string => typeof x === "string");
  const verb = done ? "Se cargaron" : "Se cargarán";
  return `${verb} ${parts.join(" y ")} de ${preview.rows} filas${left.length > 0 ? `; ${left.join(", ")}` : ""}.`;
}

/** The e-mails typed in one box, one per line or separated by commas or semicolons; repeated ones once (ignoring case). */
export function parseEmails(text: string): string[] {
  const seen = new Set<string>();
  const list: string[] = [];
  for (const raw of text.split(/[\n;,]/)) {
    const email = raw.trim();
    if (email !== "" && !seen.has(email.toLowerCase())) {
      seen.add(email.toLowerCase());
      list.push(email);
    }
  }
  return list;
}

/** The message for the field, or false when the list can be saved. */
export function emailsProblem(list: string[]): string | false {
  const bad = list.find((email) => !EMAIL.test(email) || email.length > 200);
  if (bad) {
    return `«${bad}» no es un correo válido.`;
  }
  return list.length > MAX_EMAILS ? `Se guardan hasta ${MAX_EMAILS} correos.` : false;
}

export function fileProblem(file: { name: string; size: number } | null): string | false {
  if (file === null) {
    return "Elija el archivo exportado de ADM Cloud.";
  }
  if (!/\.(xlsx|csv)$/i.test(file.name)) {
    return "El archivo debe ser .xlsx o .csv.";
  }
  return file.size > MAX_IMPORT_BYTES ? "El archivo supera 5 MB." : false;
}

export interface BatchItem {
  outcome: string;
  code?: string | null;
}

/** "3 de 5 …; 2 omitidos: <reason> (2)" — what a batch command did, with the reasons of what it skipped. */
export function batchSummary(done: number, items: BatchItem[], what: string, messages: Readonly<Record<string, string>>): string {
  const skipped = items.filter((item) => item.outcome !== "DONE");
  if (skipped.length === 0) {
    return `${done} ${what}.`;
  }
  const reasons = new Map<string, number>();
  for (const item of skipped) {
    const text = (item.code && messages[item.code]) || item.code || "sin motivo";
    reasons.set(text, (reasons.get(text) ?? 0) + 1);
  }
  const detail = [...reasons].map(([text, count]) => `${text.replace(/\.$/, "")} (${count})`).join("; ");
  return `${done} de ${items.length} ${what}; ${skipped.length} omitidos: ${detail}.`;
}

