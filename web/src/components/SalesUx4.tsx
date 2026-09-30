"use client";

import { useEffect, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { formatDecimal } from "@/lib/decimal";
import { describeError } from "@/lib/errors";
import { formatDateTime, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { previewQuery } from "@/lib/ux4b";
import { canSeeAccounting, creditPreviewSummary, previewableLines, splitHistory } from "@/lib/ux4bSales";
import "./ux4bSales.css";

/** V-07: an amount inside a sentence — grouped, 2 decimals, "RD$ ", in the running text's typeface (not monospace). */
export function MoneyText({ value, testId }: { value: string | null | undefined; testId?: string }) {
  return (
    <span className="money-text">
      RD$ <span data-testid={testId}>{formatDecimal(value)}</span>
    </span>
  );
}

function HistoryTable({ rows, title, testId }: { rows: readonly Schemas["StateChange"][]; title: string; testId?: string }) {
  return (
    <>
      <h2>{title}</h2>
      <div className="table-wrap" data-testid={testId}>
        <table>
          <thead>
            <tr>
              <th>Fecha</th>
              <th>De</th>
              <th>A</th>
              <th>Por</th>
              <th>Motivo</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((h, i) => (
              <tr key={i}>
                <td>{formatDateTime(h.at)}</td>
                <td>{statusLabel(h.from)}</td>
                <td>{statusLabel(h.to)}</td>
                <td className="wrap">{h.by ?? "—"}</td>
                <td className="wrap">{h.reason ?? ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

/**
 * V-05 (E-UX4-11): a sales document's history — the commercial changes for everyone, the accounting ones apart and only for who
 * reads the books (configuration:read or ledger:read).
 */
export function SalesHistory({ history, label }: { history: readonly Schemas["StateChange"][]; label?: (status: string | null | undefined) => string }) {
  const { can } = useSession();
  const { commercial, accounting } = splitHistory(history);
  const relabel = (rows: readonly Schemas["StateChange"][]) => (label ? rows.map((h) => ({ ...h, from: h.from ? label(h.from) : h.from, to: label(h.to) })) : rows);
  return (
    <>
      <HistoryTable rows={relabel(commercial)} title="Historial" testId="history-commercial" />
      {canSeeAccounting(can) && accounting.length > 0 ? <HistoryTable rows={accounting} title="Historial contable" testId="history-accounting" /> : null}
    </>
  );
}

type Preview = Schemas["SalesPreview"];

/**
 * V-11 (E-UX4-3): the server's pricing of a draft order or quote while it is typed — asked 400 ms after the last change and only
 * when every line has a product and a quantity. A failure is a gentle note, never a blocking error.
 */
export function useSalesPreview(
  kind: "order" | "quote",
  plantId: string,
  lines: readonly { itemId: string; uom: string; quantity: string; unitPrice?: string }[],
): { preview: Preview | null; pending: boolean; problem: string | null } {
  const { companyId } = useSession();
  const [state, setState] = useState<{ preview: Preview | null; pending: boolean; problem: string | null }>({ preview: null, pending: false, problem: null });
  const ready = plantId ? previewableLines(lines.map((l) => ({ ...l }))) : null;
  const key = ready ? JSON.stringify([kind, plantId, ready.map((l) => [l.itemId, l.uom, l.quantity, (l as { unitPrice?: string }).unitPrice?.trim() ?? ""])]) : "";
  useEffect(() => {
    if (!key) {
      return;
    }
    let cancelled = false;
    const timer = window.setTimeout(async () => {
      setState((s) => ({ ...s, pending: true }));
      try {
        const parsed = JSON.parse(key) as [string, string, [string, string, string, string][]];
        const body = parsed[2];
        const result =
          kind === "order"
            ? await previewQuery("/api/v1/companies/{companyId}/sales/orders/preview", companyId, {
                plantId,
                lines: body.map(([itemId, uom, quantity]) => ({ itemId, uom, quantity })),
              })
            : await previewQuery("/api/v1/companies/{companyId}/sales/quotes/preview", companyId, {
                plantId,
                lines: body.map(([itemId, uom, quantity, unitPrice]) => ({ itemId, uom, quantity, unitPrice: unitPrice === "" || !/^\d{1,13}(\.\d{1,4})?$/.test(unitPrice) ? null : unitPrice })),
              });
        if (!cancelled) {
          setState({ preview: result, pending: false, problem: null });
        }
      } catch (caught) {
        if (!cancelled) {
          setState({ preview: null, pending: false, problem: describeError(caught).message });
        }
      }
    }, 400);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [companyId, key, kind, plantId]);
  return key ? state : { preview: null, pending: false, problem: null };
}

/** V-11: the preview's totals: net, estimated ITBIS (or why not) and total. */
export function PreviewTotals({ preview, pending, problem }: { preview: Preview | null; pending: boolean; problem: string | null }) {
  if (problem) {
    return (
      <p className="muted" data-testid="preview-problem">
        Vista previa no disponible: {problem}
      </p>
    );
  }
  if (!preview) {
    return <p className="muted">Elija los productos y las cantidades para ver los importes calculados por el sistema.</p>;
  }
  return (
    <div className="preview-box" data-testid="preview" aria-busy={pending}>
      <h3>Importes calculados por el sistema (vista previa, no se guarda)</h3>
      <dl>
        <dt>Total neto</dt>
        <dd>
          <MoneyText value={preview.netTotal} testId="preview-net" />
        </dd>
        <dt>ITBIS estimado</dt>
        <dd>
          {preview.itbisTotal !== null ? (
            <MoneyText value={preview.itbisTotal} testId="preview-itbis" />
          ) : (
            <span data-testid="preview-itbis-unavailable">No se puede estimar: {preview.itbisUnavailableReason ?? preview.itbisUnavailableCode ?? "sin regla fiscal vigente"}</span>
          )}
        </dd>
        <dt>Total</dt>
        <dd>{preview.total !== null ? <MoneyText value={preview.total} testId="preview-total" /> : "—"}</dd>
      </dl>
      <p className="muted">El ITBIS definitivo se calcula al facturar (una exención CONFOTUR se decide ahí).</p>
    </div>
  );
}

/** V-14 (E-UX4-4): would this amount fit in the customer's credit now? The server's rule, read-only. */
export function CreditPreviewCard({ partyId, amount, intro }: { partyId: string; amount: string; intro?: string }) {
  const { companyId } = useSession();
  const valid = partyId !== "" && /^\d{1,13}(\.\d{1,2})?$/.test(amount);
  const { data, error } = useLoad(
    valid ? () => query("/api/v1/companies/{companyId}/sales/customers/{partyId}/credit-preview", { path: { companyId, partyId }, query: { amount } }) : null,
    [companyId, partyId, amount, valid],
  );
  if (!valid) {
    return null;
  }
  if (error) {
    return <p className="muted">No se pudo consultar el crédito del cliente: {describeError(error).message}</p>;
  }
  if (data === null) {
    return <p className="muted">Consultando el crédito del cliente…</p>;
  }
  const summary = creditPreviewSummary(data);
  return (
    <div className={`credit-preview ${summary.fits ? "fits" : "exceeds"}`} data-testid="credit-preview">
      {intro ? <p style={{ margin: "0 0 4px" }}>{intro}</p> : null}
      <strong data-testid="credit-preview-headline">{summary.headline}</strong>
      <div>
        Disponible hoy {data.available !== null ? <MoneyText value={data.available} /> : "—"} · después de este monto{" "}
        {data.availableAfter !== null ? <MoneyText value={data.availableAfter} /> : "—"}
      </div>
      {summary.reasons.length > 0 ? (
        <ul>
          {summary.reasons.map((r) => (
            <li key={r}>{r}</li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
