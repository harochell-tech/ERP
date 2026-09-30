"use client";

import Link from "next/link";
import {
  cloneElement,
  Fragment,
  isValidElement,
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type ReactElement,
  type ReactNode,
} from "react";
import { describeError } from "@/lib/errors";
import { formatDecimal } from "@/lib/decimal";
import { statusLabel, statusTone } from "@/lib/labels";
import { useSession } from "@/lib/session";

export function ErrorBox({ error }: { error: unknown }) {
  if (error === null || error === undefined) {
    return null;
  }
  const described = describeError(error);
  return (
    <div role="alert" className="error">
      <strong>{described.message}</strong>
      {described.detail ? (
        <details className="technical">
          <summary>Detalle técnico</summary>
          <div className="mono">{described.detail}</div>
        </details>
      ) : null}
      {described.correlationId ? <div className="muted">Referencia: {described.correlationId}</div> : null}
    </div>
  );
}

export function Loading({ error, children }: { error?: unknown; children?: ReactNode }) {
  if (error) {
    return <ErrorBox error={error} />;
  }
  return <p className="muted">{children ?? "Cargando…"}</p>;
}

/**
 * E-11 / E-PR18b-5: accounting status as the API reports it, never changed from the UI. The link to the journals (Explain)
 * appears only for users with audit:read.
 */
export function AccountingStatus({ status, eventId }: { status: string; eventId?: string | null }) {
  const { can } = useSession();
  return (
    <span data-testid="accounting-status">
      {statusLabel(status)}
      {eventId && can("audit:read") ? (
        <>
          {" "}
          (<Link href={`/auditoria/asientos/?evento=${eventId}`}>ver asientos</Link>)
        </>
      ) : null}
    </span>
  );
}

type AriaProps = { "aria-required"?: boolean; "aria-invalid"?: boolean; "aria-describedby"?: string };

/** ARIA of an input whose message (error or hint) has id `messageId` — for inputs outside a Field (e.g. a line-editor cell). */
export function fieldAria(error: string | null | undefined, messageId: string, required = false): AriaProps {
  return {
    "aria-required": required || undefined,
    "aria-invalid": error ? true : undefined,
    "aria-describedby": error ? messageId : undefined,
  };
}

/** The message under an input (E-UX1-01-6); nothing without an error. */
export function FieldMessage({ id, error }: { id: string; error: string | null | undefined }) {
  return error ? (
    <span id={id} className="field-error">
      {error}
    </span>
  ) : null;
}

/**
 * E-UX1-01-6: a labelled input. `required` marks it (asterisk + aria-required); `error` shows under the input and is tied to it
 * with aria-describedby; `hint` is a short help text. The single child element receives the ARIA attributes.
 */
export function Field({
  label,
  children,
  required = false,
  error,
  hint,
  wide = false,
}: {
  label: string;
  children: ReactNode;
  required?: boolean;
  error?: string | null;
  hint?: ReactNode;
  wide?: boolean;
}) {
  const id = useId();
  const messageId = `${id}-message`;
  const described = error || hint ? messageId : undefined;
  const child = isValidElement(children) && children.type !== Fragment
    ? cloneElement(children as ReactElement<AriaProps>, {
        "aria-required": required || undefined,
        "aria-invalid": error ? true : undefined,
        "aria-describedby": described,
      })
    : children;
  return (
    <div className={`field${error ? " has-error" : ""}${wide ? " wide" : ""}`}>
      <label>
        {/* The asterisk is CSS content (.is-required::after), so the label's text and accessible name stay the plain label. */}
        <span className={`field-label${required ? " is-required" : ""}`}>{label}</span>
        {child}
      </label>
      {error ? (
        <span id={messageId} className="field-error">
          {error}
        </span>
      ) : hint ? (
        <span id={messageId} className="field-hint">
          {hint}
        </span>
      ) : null}
    </div>
  );
}

/**
 * UX2-02 (E-UX2-1): a text input followed by its unit ("%", "días"). Inside a Field it receives the Field's ARIA attributes and
 * passes them to the input, so the label, the message and getByLabel keep working.
 */
export function SuffixInput({
  suffix,
  value,
  onChange,
  placeholder,
  inputMode = "decimal",
  ...aria
}: {
  suffix: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  inputMode?: "decimal" | "numeric" | "text";
} & AriaProps) {
  return (
    <span className="input-suffix">
      <input value={value} inputMode={inputMode} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} {...aria} />
      {suffix ? <span aria-hidden="true">{suffix}</span> : null}
    </span>
  );
}

type Found<K extends string> =Partial<Record<K, string | false | null | undefined>>;

/**
 * E-UX1-01-6: per-field validation. `check({ field: message-or-falsy, … })` keeps the messages, focuses the first invalid input
 * (the first `aria-invalid` in the page after render) and returns true when there is none.
 */
export function useFieldErrors<K extends string = string>() {
  const [errors, setErrors] = useState<Partial<Record<K, string>>>({});
  const [focusRequest, setFocusRequest] = useState(0);

  useEffect(() => {
    if (focusRequest === 0) {
      return;
    }
    document.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus();
  }, [focusRequest]);

  const check = useCallback((found: Found<K>): boolean => {
    const next: Partial<Record<K, string>> = {};
    for (const [key, message] of Object.entries(found) as [K, string | false | null | undefined][]) {
      if (message) {
        next[key] = message;
      }
    }
    setErrors(next);
    if (Object.keys(next).length > 0) {
      setFocusRequest((n) => n + 1);
      return false;
    }
    return true;
  }, []);

  const clear = useCallback(() => setErrors({}), []);
  return { errors, check, clear };
}

/**
 * E-UX1-01-8: an accessible confirmation (native modal dialog: focus stays inside, Escape or a tap outside cancels, focus returns
 * to the button that opened it). It states the consequence and warns when the server will ask to re-authenticate.
 */
export function ConfirmDialog({
  open,
  title,
  children,
  confirmLabel,
  danger = false,
  stepUp = false,
  busy = false,
  confirmDisabled = false,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  title: string;
  children?: ReactNode;
  confirmLabel: string;
  danger?: boolean;
  stepUp?: boolean;
  busy?: boolean;
  confirmDisabled?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const id = useId();

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) {
      return;
    }
    if (open && !dialog.open) {
      if (typeof dialog.showModal === "function") {
        dialog.showModal();
      } else {
        dialog.setAttribute("open", "");
      }
    } else if (!open && dialog.open) {
      dialog.close();
    }
  }, [open]);

  return (
    <dialog
      ref={ref}
      className="confirm-dialog"
      aria-labelledby={`${id}-title`}
      aria-describedby={`${id}-body`}
      onCancel={(e) => {
        e.preventDefault();
        onCancel();
      }}
      onClick={(e) => {
        if (e.target === ref.current) {
          onCancel();
        }
      }}
    >
      {open ? (
        <form
          method="dialog"
          onSubmit={(e) => {
            e.preventDefault();
            if (!busy && !confirmDisabled) {
              onConfirm();
            }
          }}
        >
          <h2 id={`${id}-title`}>{title}</h2>
          <div id={`${id}-body`} className="confirm-body">
            {children}
            {stepUp ? <p className="notice">Esta acción requiere autenticación reciente: si su última autenticación no es reciente, al confirmar el sistema le pedirá entrar de nuevo con su cuenta y luego deberá pulsar otra vez.</p> : null}
          </div>
          <div className="dialog-actions">
            <button type="button" autoFocus onClick={onCancel}>
              Cancelar
            </button>
            <button type="submit" className={danger ? "danger-solid" : "primary"} disabled={busy || confirmDisabled}>
              {confirmLabel}
            </button>
          </div>
        </form>
      ) : null}
    </dialog>
  );
}

/**
 * E-UX1-01-8: a button whose action runs only after the confirmation dialog. The dialog's button reads "Confirmar: <label>".
 */
export function ConfirmAction({
  label,
  title,
  consequence,
  onConfirm,
  busy = false,
  disabled = false,
  danger = false,
  stepUp = false,
  className,
  testId,
}: {
  label: string;
  title?: string;
  consequence: ReactNode;
  onConfirm: () => void;
  busy?: boolean;
  disabled?: boolean;
  danger?: boolean;
  stepUp?: boolean;
  className?: string;
  testId?: string;
}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button type="button" className={className ?? (danger ? "danger" : undefined)} disabled={busy || disabled} data-testid={testId} onClick={() => setOpen(true)}>
        {label}
      </button>
      <ConfirmDialog
        open={open}
        title={title ?? `¿${label}?`}
        confirmLabel={`Confirmar: ${label}`}
        danger={danger}
        stepUp={stepUp}
        busy={busy}
        onCancel={() => setOpen(false)}
        onConfirm={() => {
          setOpen(false);
          onConfirm();
        }}
      >
        <p>{consequence}</p>
      </ConfirmDialog>
    </>
  );
}

/**
 * A button that asks for a mandatory reason before running (reject, cancel, reverse…); `minLength` when the rule asks for more.
 * E-UX1-01-8: the reason is asked in the confirmation dialog, with the consequence when given.
 */
export function ReasonAction({
  label,
  busy,
  onConfirm,
  minLength = 1,
  consequence,
  title,
  stepUp = false,
}: {
  label: string;
  busy: boolean;
  onConfirm: (reason: string) => void;
  minLength?: number;
  consequence?: ReactNode;
  title?: string;
  stepUp?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState("");
  const id = useId();
  return (
    <>
      <button type="button" className="danger" disabled={busy} onClick={() => setOpen(true)}>
        {label}
      </button>
      <ConfirmDialog
        open={open}
        title={title ?? `${label}`}
        confirmLabel={`Confirmar: ${label}`}
        danger
        stepUp={stepUp}
        busy={busy}
        confirmDisabled={reason.trim().length < minLength}
        onCancel={() => setOpen(false)}
        onConfirm={() => {
          setOpen(false);
          onConfirm(reason.trim());
        }}
      >
        {consequence ? <p>{consequence}</p> : null}
        <div className="field wide">
          <label>
            <span className="field-label is-required">
              Motivo
            </span>
            <textarea
              aria-label={`Motivo: ${label}`}
              aria-required
              aria-describedby={`${id}-hint`}
              rows={3}
              placeholder={minLength > 1 ? `Motivo (mínimo ${minLength} caracteres)` : "Motivo"}
              value={reason}
              onChange={(e) => setReason(e.target.value)}
            />
          </label>
          <span id={`${id}-hint`} className="field-hint">
            {minLength > 1 ? `Mínimo ${minLength} caracteres.` : "Obligatorio."}
          </span>
        </div>
      </ConfirmDialog>
    </>
  );
}

export function NoPermission() {
  return <p className="muted">No tiene permiso para ver esta página.</p>;
}

/** E-UI-5: a status as a coloured label (the colour never stands alone). */
export function StatusBadge({ status, label, testId }: { status: string | null | undefined; label?: string; testId?: string }) {
  return (
    <span className={`badge tone-${statusTone(status)}`} data-testid={testId}>
      {label ?? statusLabel(status)}
    </span>
  );
}

/**
 * A money amount as the server sent it (a decimal string), grouped, 2 decimals, never computed in the browser. E-UX1-01-5:
 * `currency` prefixes "RD$ " (single-value cards); table columns say "(RD$)" in their header instead.
 */
export function Money({ value, testId, currency = false }: { value: string | null | undefined; testId?: string; currency?: boolean }) {
  return (
    <span className="mono">
      {currency ? <span className="currency">RD$ </span> : null}
      <span data-testid={testId}>{formatDecimal(value)}</span>
    </span>
  );
}

/** E-UX1-01-4: a plant (by id or code) as "Name (CODE)". */
export function PlantName({ plant, fallback }: { plant: string | null | undefined; fallback?: string }) {
  const { plantName } = useSession();
  return <>{plantName(plant, fallback)}</>;
}

/**
 * E-UX1-01-2: a line editor. It scrolls inside its box on a desktop and becomes stacked cards below 700 px, each cell labelled
 * with its column header (copied from the table head after each render).
 */
export function LineTable({ children, className, testId }: { children: ReactNode; className?: string; testId?: string }) {
  const ref = useRef<HTMLTableElement>(null);
  useLayoutEffect(() => {
    const table = ref.current;
    if (!table) {
      return;
    }
    const heads = [...table.querySelectorAll("thead th")].map((th) => th.textContent?.trim() ?? "");
    table.querySelectorAll("tbody tr, tfoot tr").forEach((row) => {
      [...row.children].forEach((cell, index) => {
        const head = heads[index];
        if (head) {
          cell.setAttribute("data-label", head);
        } else {
          cell.removeAttribute("data-label");
        }
      });
    });
  });
  return (
    <div className="table-wrap">
      <table ref={ref} className={`line-editor${className ? ` ${className}` : ""}`} data-testid={testId}>
        {children}
      </table>
    </div>
  );
}

/** E-UX1-01-2: several row buttons folded under one "Acciones" button below 700 px (inline on a desktop). */
export function RowActions({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <div className={`row-actions${open ? " open" : ""}`}>
      <button type="button" className="row-actions-toggle" aria-expanded={open} onClick={() => setOpen(!open)}>
        Acciones
      </button>
      <div className="row-actions-list">{children}</div>
    </div>
  );
}
