// VS3-10a: pure helpers of the sales and dispatch screens (unit-tested; no money or quantity arithmetic).

/** A file's bytes as base64, for commands that carry a small file (the opening CSV, E-VS3-02b-3). */
export function bytesToBase64(bytes: ArrayBuffer): string {
  let binary = "";
  const view = new Uint8Array(bytes);
  for (let i = 0; i < view.length; i += 0x8000) {
    binary += String.fromCharCode(...view.subarray(i, i + 0x8000));
  }
  return btoa(binary);
}

export type DeliveryStep = "START_LOADING" | "CONFIRM_LOADED" | "GATE_OUT" | "POD" | null;

/** E-VS3-04-14: the dispatcher's next step for a delivery in each status; a pickup is delivered at the gate. */
export function nextDeliveryStep(status: string): DeliveryStep {
  switch (status) {
    case "PLANNED":
      return "START_LOADING";
    case "LOADING":
      return "CONFIRM_LOADED";
    case "LOADED":
      return "GATE_OUT";
    case "IN_TRANSIT":
      return "POD";
    default:
      return null;
  }
}

/** Board columns of the dispatch screen (E-VS3-10-5), in the order the work flows. */
export const BOARD_COLUMNS: readonly { status: string; title: string }[] = [
  { status: "PLANNED", title: "Planificados" },
  { status: "LOADING", title: "Cargando" },
  { status: "LOADED", title: "Cargados" },
  { status: "IN_TRANSIT", title: "En tránsito" },
];

/** A delivery can still be cancelled (reason required) before it leaves through the gate. */
export function cancellable(status: string): boolean {
  return status === "PLANNED" || status === "LOADING" || status === "LOADED";
}

/** Order statuses where the Vendedor may still cancel (the server decides; this only hides the button). */
export function orderCancellable(status: string): boolean {
  return status === "DRAFT" || status === "PENDING_CREDIT" || status === "CONFIRMED";
}

/** Orders that dispatch can plan deliveries for. */
export function orderDispatchable(status: string): boolean {
  return status === "CONFIRMED" || status === "PARTIALLY_DELIVERED";
}

/** E-VS3-11: how the customer paid. */
export const METHODS: Readonly<Record<string, string>> = {
  TRANSFER: "Transferencia",
  CHEQUE: "Cheque",
  CASH: "Efectivo",
};

/** The live applications of a receipt grouped by the command that made them (an unapply undoes one whole group, E-VS3-07-6). */
export function applicationGroups<T extends { eventId: string; live: boolean; reversesApplicationId?: string | null }>(applications: readonly T[]): { eventId: string; items: T[] }[] {
  const groups = new Map<string, T[]>();
  for (const a of applications) {
    if (a.live && !a.reversesApplicationId) {
      groups.set(a.eventId, [...(groups.get(a.eventId) ?? []), a]);
    }
  }
  return [...groups.entries()].map(([eventId, items]) => ({ eventId, items }));
}

/** UX3-02 (E-UX3-9): the three reasons of a commercial credit note (E-VS3-06), in Spanish. */
export const CREDIT_NOTE_REASONS: Readonly<Record<string, string>> = {
  DESCUENTO: "Descuento",
  ERROR_DE_PRECIO: "Error de precio",
  OTRO: "Otro",
};

export function creditNoteReasonLabel(category: string): string {
  return CREDIT_NOTE_REASONS[category] ?? category;
}
