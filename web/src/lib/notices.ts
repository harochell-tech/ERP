// E-UX1-01-9: the success notice of a command. The caller names the result ("Pedido PV-000012 enviado a crédito."); when it
// only knows the kind of result, the document number comes from the command's result (its "…No" field, e.g. orderNo).
import type { CommandResponse } from "@/api/client";

/** A notice: a fixed text, or one built from the command's response and the document number found in its result. */
export type SuccessMessage = string | ((response: CommandResponse, documentNo: string | null) => string);

/** The first "…No" string of a command result (orderNo, invoiceNo, paymentNo…), or null. */
export function documentNumber(result: unknown): string | null {
  if (result === null || typeof result !== "object" || Array.isArray(result)) {
    return null;
  }
  for (const [key, value] of Object.entries(result as Record<string, unknown>)) {
    if (/[a-z]No$/.test(key) && key !== "lineNo" && typeof value === "string" && value.trim() !== "") {
      return value;
    }
  }
  return null;
}

/** The notice text for a successful command. */
export function successText(message: SuccessMessage | undefined, response: CommandResponse): string {
  const documentNo = documentNumber(response.result);
  if (typeof message === "function") {
    return message(response, documentNo);
  }
  if (typeof message === "string") {
    return message;
  }
  return documentNo ? `Listo: ${documentNo} guardado.` : "Listo: operación completada.";
}
