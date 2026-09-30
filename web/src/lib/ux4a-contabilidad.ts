// UX4-02 (A-10…A-18): pure helpers of the accounting screens (Contabilidad). No arithmetic on amounts: zero tests and comparisons
// are done on the decimal text. Unit-tested in tests/unit/ux4a-contabilidad.test.ts.

/** Whether a decimal string is zero ("0", "0.00", "-0.00"); an empty or malformed value is not. */
export function isZeroDecimal(value: string | null | undefined): boolean {
  return value !== null && value !== undefined && /^-?\d+(\.\d+)?$/.test(value.trim()) && !/[1-9]/.test(value);
}

/** A-11: a trial-balance "Saldo deudor" / "Saldo acreedor" cell: the server's value, blank (null → "—") when zero. */
export function balanceCell(value: string | null | undefined): string | null {
  return value === null || value === undefined || isZeroDecimal(value) ? null : value;
}

/** A-12: the accounts whose code or name contains the text (case and accents ignored); the chosen account always stays listed. */
export function filterAccounts<A extends { accountId: string; code: string; name: string }>(accounts: readonly A[], text: string, selectedId = ""): A[] {
  const fold = (s: string) => s.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
  const needle = fold(text.trim());
  if (needle === "") {
    return [...accounts];
  }
  return accounts.filter((a) => a.accountId === selectedId || fold(`${a.code} ${a.name}`).includes(needle));
}

/** A-14: SHA-256 shown short ("3f2a9c1b…e8d4"); the full value goes in a tooltip. */
export function shortHash(hash: string | null | undefined): string {
  if (!hash) {
    return "—";
  }
  return hash.length <= 16 ? hash : `${hash.slice(0, 8)}…${hash.slice(-4)}`;
}

/** A-14: the role that approves manual journals (`manual_journal:approve`, migration 0033: CONTROLLER only). */
export const MANUAL_JOURNAL_APPROVER = "Controller";

/** A-14: "Debe aprobarlo" shows while the adjustment still waits for its approval. */
export function approverPending(status: string): boolean {
  return status === "DRAFT" || status === "PENDING_APPROVAL";
}

/** A-15: whether a yyyy-MM-dd date falls in a yyyy-MM month; every date when no month is chosen. */
export function inMonth(isoDate: string, month: string): boolean {
  return month === "" || isoDate.slice(0, 7) === month;
}

/** A-16: what a control account is, in plain words. */
export const CONTROL_ACCOUNT_HELP =
  "Una cuenta de control (inventario, cuentas por pagar, bancos…) se mueve solo con los documentos del sistema (recepciones, facturas, pagos) y no admite ajustes manuales.";

/** A-17: what happens after a structure version is prepared, and who approves it. */
export const STRUCTURE_NEXT_STEP =
  "La versión queda en borrador. La aprueba el Aprobador de políticas contables (una persona distinta de quien la prepara); desde la fecha indicada reemplaza a la versión activa en los estados financieros.";

/** A-17: the date label of a structure version by status: a draft "regirá desde", the active one "vigente desde". */
export function effectiveFromLabel(status: string): string {
  switch (status) {
    case "DRAFT":
      return "Regirá desde (al aprobarse)";
    case "ACTIVE":
      return "Vigente desde";
    default:
      return "Rigió desde";
  }
}

/** A-17: a structure line in a select, named by its concept: "Activo (A)"; the bare code when it has no concept yet. */
export function lineOptionLabel(code: string, caption: string): string {
  return caption.trim() === "" ? code : `${caption.trim()} (${code})`;
}

interface StructureLine {
  lineCode: string;
  caption: string;
  parentLineCode: string;
  sign: number;
}

/**
 * A-17: whether an edited structure equals the version it was copied from — the same lines in the same order (code, concept,
 * parent, sign; spaces trimmed) and every account on the same line — so saving it would only repeat that version.
 */
export function sameStructure(
  original: { lines: readonly StructureLine[]; placement: Readonly<Record<string, string>> },
  current: { lines: readonly StructureLine[]; placement: Readonly<Record<string, string>> },
): boolean {
  const line = (l: StructureLine) => [l.lineCode.trim(), l.caption.trim(), l.parentLineCode.trim(), String(l.sign)].join("\u0000");
  if (original.lines.length !== current.lines.length || original.lines.some((l, i) => line(l) !== line(current.lines[i] as StructureLine))) {
    return false;
  }
  const placed = (p: Readonly<Record<string, string>>) =>
    Object.entries(p)
      .filter(([, code]) => code.trim() !== "")
      .map(([id, code]) => `${id}=${code.trim()}`)
      .sort()
      .join("|");
  return placed(original.placement) === placed(current.placement);
}

/** A-18: the header the opening-inventory file must carry (OpeningHandlers.Parse). */
export const OPENING_HEADER = "planta,ubicacion,producto,cantidad,documento";

/** A-18: a CSV template: the header and one example line (codes of the company when known, placeholders otherwise). */
export function openingTemplateCsv(plantCode?: string | null, locationCode?: string | null): string {
  return `${OPENING_HEADER}\r\n${plantCode || "CODIGO-PLANTA"},${locationCode || "CODIGO-UBICACION"},CODIGO-PRODUCTO,1000,CONTEO-0001\r\n`;
}

/** A-18: what the opening form still lacks, for its button; empty when it can be sent. */
export function openingMissing(hasFile: boolean, cutover: string): string[] {
  return [hasFile ? null : "el archivo CSV", cutover ? null : "la fecha de corte"].filter((m): m is string => m !== null);
}

/** A-18: the button text: "Preparar apertura", or what is missing ("Falta el archivo CSV y la fecha de corte"). */
export function openingButtonLabel(missing: readonly string[]): string {
  return missing.length === 0 ? "Preparar apertura" : `${missing.length > 1 ? "Faltan" : "Falta"} ${missing.join(" y ")}`;
}
