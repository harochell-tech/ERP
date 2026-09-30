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
