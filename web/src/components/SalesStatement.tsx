"use client";

import type { Schemas } from "@/api/client";
import { Money } from "@/components/ui";
import { formatDate } from "@/lib/labels";

// VS3-10b (E-VS3-09-3/4): the lines of a customer's statement of account, shared by the screen and its print view (UX4-03, V-19).

const KINDS: Readonly<Record<string, string>> = {
  FACTURA: "Factura",
  FACTURA_ANULADA: "Factura anulada",
  NOTA_DE_CREDITO: "Nota de crédito",
  COBRO: "Cobro",
  COBRO_ANULADO: "Cobro anulado",
  CHEQUE_DEVUELTO: "Cheque devuelto",
  RETENCION: "Retención",
  RETENCION_REVERSADA: "Retención reversada",
  DEVOLUCION: "Devolución al cliente",
  OTRO: "Otro",
};

export function StatementTable({ statement }: { statement: Schemas["CustomerStatement"] }) {
  const s = statement;
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th>Fecha</th>
            <th>Tipo</th>
            <th>Documento</th>
            <th className="num">Débito (RD$)</th>
            <th className="num">Crédito (RD$)</th>
            <th className="num">Saldo (RD$)</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td>{formatDate(s.from)}</td>
            <td colSpan={4}>Saldo inicial</td>
            <td className="num">
              <Money value={s.opening} />
            </td>
          </tr>
          {s.entries.map((e, i) => (
            <tr key={i}>
              <td>{formatDate(e.postingDate)}</td>
              <td>{KINDS[e.kind] ?? e.kind}</td>
              <td className="mono">{e.documentNo ?? "—"}</td>
              <td className="num">
                <Money value={e.debit} />
              </td>
              <td className="num">
                <Money value={e.credit} />
              </td>
              <td className="num">
                <Money value={e.balance} />
              </td>
            </tr>
          ))}
          <tr>
            <th colSpan={3}>Saldo final</th>
            <td className="num">
              <Money value={s.totalDebit} />
            </td>
            <td className="num">
              <Money value={s.totalCredit} />
            </td>
            <td className="num">
              <Money value={s.closing} testId="statement-closing" />
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  );
}

/**
 * FIS1b-07 (E-FIS1b-9): what the customer owes outside the fiscal receivable — delivered under proforma, waiting for its e-CF. It is
 * not part of the balance above: it becomes an invoice (and joins it) when the DGII resolves the exemption.
 */
export function StatementProformas({ statement }: { statement: Schemas["CustomerStatement"] }) {
  if (statement.openProformas.length === 0) {
    return null;
  }
  return (
    <section data-testid="statement-proformas">
      <h2>Proformas abiertas</h2>
      <p className="muted">Entregas que esperan su comprobante fiscal. No forman parte del saldo de arriba; se cobran contra la proforma.</p>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Proforma</th>
              <th>Conduce</th>
              <th>Fecha</th>
              <th>Vence</th>
              <th className="num">Total (RD$)</th>
              <th className="num">Cobrado (RD$)</th>
              <th className="num">Saldo (RD$)</th>
              <th className="num">Depósito ITBIS (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {statement.openProformas.map((f) => (
              <tr key={f.proformaId}>
                <td className="mono">{f.proformaNo}</td>
                <td className="mono">{f.deliveryNo}</td>
                <td>{formatDate(f.proformaDate)}</td>
                <td>{formatDate(f.dueDate)}</td>
                <td className="num">
                  <Money value={f.total} />
                </td>
                <td className="num">
                  <Money value={f.allocated} />
                </td>
                <td className="num">
                  <Money value={f.balance} />
                </td>
                <td className="num">
                  <Money value={f.deposit} />
                </td>
              </tr>
            ))}
            <tr>
              <th colSpan={6}>Saldo en proformas</th>
              <td className="num">
                <Money value={statement.proformaBalance} testId="statement-proforma-balance" />
              </td>
              <td />
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  );
}
