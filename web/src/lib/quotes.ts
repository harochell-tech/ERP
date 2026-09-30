// QUO1-04 (E-QUO1-04-1…10): pure helpers of the sales quotation screens. No money or quantity arithmetic: nets and totals are the
// server's. Comparing two decimal strings (the typed price against the list price) is done digit by digit, never through a
// JavaScript number, so "49.9999" is below "50" and "50.0000" equals "50".

/** The validity a new quote or a copy proposes (the Vendedor changes it); the server only requires today or a later date. */
export const DEFAULT_QUOTE_VALIDITY_DAYS = 15;

/** The six statuses of a quote, in lifecycle order (the list's filter). */
export const QUOTE_STATUSES = ["DRAFT", "PENDING_APPROVAL", "SENT", "CONVERTED", "LOST", "CANCELLED"] as const;

const QUOTE_STATUS_LABELS: Readonly<Record<string, string>> = {
  DRAFT: "Borrador",
  PENDING_APPROVAL: "Pendiente de aprobación",
  SENT: "Enviada",
  CONVERTED: "Convertida en pedido",
  LOST: "Perdida",
  CANCELLED: "Cancelada",
};

/** A quote's status in Spanish (feminine: "la cotización"); a SENT quote past its validity reads "Vencida" (derived, E-QUO1-02). */
export function quoteStatusLabel(status: string, expired = false): string {
  if (status === "SENT" && expired) {
    return "Vencida";
  }
  return QUOTE_STATUS_LABELS[status] ?? status;
}

const DECIMAL = /^(-?)(\d+)(?:\.(\d+))?$/;

/**
 * Compares two decimal strings exactly: -1 when `a < b`, 0 when equal, 1 when `a > b`; null when either is not a decimal. Leading
 * zeros, trailing fraction zeros and "-0" do not matter.
 */
export function compareDecimals(a: string, b: string): -1 | 0 | 1 | null {
  const x = DECIMAL.exec(a.trim());
  const y = DECIMAL.exec(b.trim());
  if (!x || !y) {
    return null;
  }
  const parse = (m: RegExpExecArray) => {
    const integer = (m[2] ?? "0").replace(/^0+/, "");
    const fraction = (m[3] ?? "").replace(/0+$/, "");
    const zero = integer === "" && fraction === "";
    return { negative: m[1] === "-" && !zero, integer, fraction };
  };
  const p = parse(x);
  const q = parse(y);
  if (p.negative !== q.negative) {
    return p.negative ? -1 : 1;
  }
  const sign = p.negative ? -1 : 1;
  let magnitude: -1 | 0 | 1 = 0;
  if (p.integer.length !== q.integer.length) {
    magnitude = p.integer.length < q.integer.length ? -1 : 1;
  } else if (p.integer !== q.integer) {
    magnitude = p.integer < q.integer ? -1 : 1;
  } else {
    const width = Math.max(p.fraction.length, q.fraction.length);
    const f = p.fraction.padEnd(width, "0");
    const g = q.fraction.padEnd(width, "0");
    magnitude = f === g ? 0 : f < g ? -1 : 1;
  }
  return (magnitude * sign) as -1 | 0 | 1;
}

/**
 * E-QUO1-04-3: whether a typed unit price is a special price (below the list price in force), which needs the Aprobador de
 * políticas before the quote is sent. An empty or invalid price is not special (empty means the list price).
 */
export function isSpecialPrice(typed: string, listPrice: string | null | undefined): boolean {
  if (!listPrice || typed.trim() === "") {
    return false;
  }
  return compareDecimals(typed, listPrice) === -1;
}

export type QuoteAction = "EDIT" | "SUBMIT" | "SEND" | "APPROVE" | "RETURN" | "CONVERT" | "LOST" | "CANCEL" | "COPY";

/**
 * The actions a user may be offered on a quote (E-QUO1-04-4). `approvalPending` is "a line below its list price that the current
 * approval does not cover": then the Vendedor submits it for approval instead of sending it. The server refuses anything else anyway.
 */
export function quoteActions(status: string, expired: boolean, approvalPending: boolean, can: (permission: string) => boolean): QuoteAction[] {
  const actions: QuoteAction[] = [];
  const manage = can("quote:manage");
  const approve = can("quote:approve_price");
  if (status === "DRAFT" && manage) {
    actions.push("EDIT", approvalPending ? "SUBMIT" : "SEND");
  }
  if (status === "PENDING_APPROVAL" && approve) {
    actions.push("APPROVE", "RETURN");
  }
  if (status === "SENT" && manage) {
    if (!expired) {
      actions.push("CONVERT");
    }
    actions.push("LOST");
  }
  if ((status === "DRAFT" || status === "SENT") && manage) {
    actions.push("CANCEL");
  }
  if (manage) {
    actions.push("COPY");
  }
  return actions;
}
