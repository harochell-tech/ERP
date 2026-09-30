// UX3-02 (E-UX3-7, E-UX3-10): the diagonal watermark of printed documents, by the server's status. Pure, unit-tested.

/** E-UX3-10: a quote prints "BORRADOR" until it is sent, "VENCIDA" once past its validity, "PERDIDA" or "CANCELADA" when closed. */
export function quoteWatermark(status: string, expired: boolean): string | null {
  switch (status) {
    case "DRAFT":
    case "PENDING_APPROVAL":
      return "BORRADOR";
    case "LOST":
      return "PERDIDA";
    case "CANCELLED":
      return "CANCELADA";
    case "CONVERTED":
      return null;
    default:
      return expired ? "VENCIDA" : null;
  }
}

/** E-UX3-7: a delivery note prints "BORRADOR – NO DESPACHADO" until the truck has gone out of the gate. */
export function deliveryWatermark(gateOutAt: string | null | undefined): string | null {
  return gateOutAt ? null : "BORRADOR – NO DESPACHADO";
}
