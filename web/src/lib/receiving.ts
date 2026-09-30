// UX3-02 (E-UX3-5): receiving from the server's open quantities. Pure, unit-tested; no arithmetic (the open and maximum
// quantities are the server's, PostGoodsReceipt's own rule).
import { formatQuantity, isPositiveDecimal } from "./decimal";

/** The quantity a receipt line starts with: the open quantity when there is one, else empty (nothing left to receive). */
export function prefillQuantity(openQuantity: string | null | undefined): string {
  return openQuantity && isPositiveDecimal(openQuantity, 6) ? formatQuantity(openQuantity) : "";
}

/** "Recibir" is offered while some line can still take a quantity. */
export function canReceiveMore(lines: readonly { maxReceivable: string }[]): boolean {
  return lines.some((l) => isPositiveDecimal(l.maxReceivable, 6));
}
