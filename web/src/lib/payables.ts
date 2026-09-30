// UX3-02 (E-UX3-6): a supplier invoice's payment status (the server's `paymentStatus`) in Spanish, with its tone. Pure, unit-tested.
import type { StatusTone } from "./labels";

const PAYMENT_STATUS: Readonly<Record<string, { label: string; tone: StatusTone }>> = {
  NOT_POSTED: { label: "Sin contabilizar", tone: "neutral" },
  OPEN: { label: "Pendiente de pago", tone: "attention" },
  PARTIAL: { label: "Pagada en parte", tone: "progress" },
  PAID: { label: "Pagada", tone: "done" },
  VOIDED: { label: "Anulada", tone: "neutral" },
  REVERSED: { label: "Reversada", tone: "reversed" },
};

export function paymentStatusLabel(status: string): string {
  return PAYMENT_STATUS[status]?.label ?? status;
}

export function paymentStatusTone(status: string): StatusTone {
  return PAYMENT_STATUS[status]?.tone ?? "neutral";
}
