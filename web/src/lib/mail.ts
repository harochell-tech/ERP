// MAIL-03 (E-MAIL-5, 7, E-MAIL-01-4, 9, 10): documents by e-mail, in words. Pure, unit-tested in tests/unit/mail.test.ts.
import type { Schemas } from "@/api/client";

export type MailItem = Schemas["DocumentMailView"];

export const MAX_RECIPIENTS = 10;

const ADDRESS = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

const STATUS: Record<string, string> = { QUEUED: "En cola", SENT: "Enviado", FAILED: "Fallido" };

export function mailStatusLabel(status: string): string {
  return STATUS[status] ?? status;
}

/** The badge tone of a sending. */
export function mailStatusTone(status: string): string {
  return status === "SENT" ? "ACTIVE" : status === "FAILED" ? "REJECTED" : "PENDING_VERIFICATION";
}

/** Addresses typed in one box: separated by commas, semicolons, spaces or new lines; lower-cased, without repetitions. */
export function parseAddresses(text: string): string[] {
  return [...new Set(text.split(/[\s,;]+/).map((a) => a.trim().toLowerCase()).filter((a) => a !== ""))];
}

/** The ticked saved e-mails, then the typed ones; each address once. */
export function recipientsOf(saved: readonly string[], unticked: ReadonlySet<string>, typed: string): string[] {
  return [...new Set([...saved.filter((a) => !unticked.has(a)).map((a) => a.toLowerCase()), ...parseAddresses(typed)])];
}

/** What is wrong with the recipients, or null. */
export function recipientsError(recipients: readonly string[]): string | null {
  if (recipients.length === 0) {
    return "Marque o escriba al menos un correo.";
  }
  if (recipients.length > MAX_RECIPIENTS) {
    return `Un correo lleva como máximo ${MAX_RECIPIENTS} destinatarios.`;
  }
  const invalid = recipients.filter((a) => !ADDRESS.test(a) || a.length > 200);
  return invalid.length > 0 ? `Revise ${invalid.length === 1 ? "esta dirección" : "estas direcciones"}: ${invalid.join(", ")}.` : null;
}

/** E-MAIL-01-4: what the deployment does with mail, for the sender to know before pressing. Null when it goes to the customer. */
export function mailModeNotice(mode: string | null): string | null {
  switch (mode) {
    case "OFF":
      return "El envío de correos no está activado en este ambiente.";
    case "REDIRECT":
      return "Ambiente de prueba: el correo no llega al cliente. Se redirige al buzón interno, con los destinatarios indicados en el asunto.";
    default:
      return null;
  }
}

/** Where a sending ended up, in words. */
export function deliveryText(item: Pick<MailItem, "status" | "deliveryMode" | "deliveredTo" | "attempts" | "lastError">): string {
  if (item.status === "SENT") {
    return item.deliveryMode === "REDIRECT" ? `Redirigido a ${(item.deliveredTo ?? []).join(", ")} (no llegó al cliente)` : "Entregado al servidor de correo";
  }
  if (item.status === "FAILED") {
    return `No se pudo enviar tras ${item.attempts} intento${item.attempts === 1 ? "" : "s"}: ${item.lastError ?? "error desconocido"}`;
  }
  return item.attempts > 0 ? `Reintentando (${item.attempts} intento${item.attempts === 1 ? "" : "s"}): ${item.lastError ?? ""}`.trim() : "Saldrá en unos segundos";
}

/** E-MAIL-01-7: why a quote cannot be e-mailed yet, saying what to do first; null when it can. */
export function quoteMailBlocked(status: string, expired: boolean): string | null {
  switch (status) {
    case "DRAFT":
      return "Primero pulse «Marcar enviada al cliente»: una cotización en borrador no se envía por correo.";
    case "PENDING_APPROVAL":
      return "Falta la aprobación de precios; después márquela como enviada al cliente.";
    case "CONVERTED":
      return null;
    case "SENT":
      return expired ? "La cotización está vencida: cópiela con una nueva vigencia para enviarla." : null;
    default:
      return "Una cotización perdida o cancelada no se envía por correo.";
  }
}

