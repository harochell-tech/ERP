"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { Field, Loading, Money, NoPermission } from "@/components/ui";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { accountClassLabel, csvUrl, monthStart } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

const PATH = "/api/v1/companies/{companyId}/finance/accounts/{accountId}/ledger";
const PAGE = 100;

const DOCUMENTS: Readonly<Record<string, string>> = {
  GOODS_RECEIPT: "Recepción",
  GOODS_RECEIPT_REVERSAL: "Reversa de recepción",
  RECEIPT_CORRECTION: "Corrección de recepción",
  SUPPLIER_INVOICE: "Factura de proveedor",
  PAYMENT: "Pago",
  MANUAL_JOURNAL: "Ajuste",
  BANK_CHARGE: "Cargo bancario",
};

// FIN1-04 (E-FIN1-04-8, E-FIN1-03-7): the movements of one account with their documents and running balance; each one opens
// "Explicar asiento" for those who may read it (audit:read).
function Ledger() {
  const { companyId, can } = useSession();
  const search = useSearchParams();
  const today = todayInDominicanRepublic();
  const [accountId, setAccountId] = useState(search.get("cuenta") ?? "");
  const [from, setFrom] = useState(search.get("desde") ?? monthStart(today));
  const [to, setTo] = useState(search.get("hasta") ?? today);
  const [offset, setOffset] = useState(0);
  const allowed = can("ledger:read");
  const { data: accounts } = useLoad(can("configuration:read") ? () => query("/api/v1/companies/{companyId}/finance/accounts", { path: { companyId } }) : null, [companyId]);
  const params = { path: { companyId, accountId }, query: { from, to, limit: PAGE, offset } };
  const { data, error } = useLoad(allowed && accountId && from && to ? () => query(PATH, params) : null, [companyId, accountId, from, to, offset, allowed]);
  if (!allowed) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Mayor por cuenta</h1>
      <div>
        <Field label="Cuenta">
          <select aria-label="Cuenta" value={accountId} onChange={(e) => { setAccountId(e.target.value); setOffset(0); }}>
            <option value="">—</option>
            {(accounts?.items ?? []).map((a) => (
              <option key={a.accountId} value={a.accountId}>
                {a.code} — {a.name}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Desde">
          <input type="date" aria-label="Desde" value={from} onChange={(e) => { setFrom(e.target.value); setOffset(0); }} />
        </Field>
        <Field label="Hasta">
          <input type="date" aria-label="Hasta" value={to} onChange={(e) => { setTo(e.target.value); setOffset(0); }} />
        </Field>
      </div>
      {!accountId ? (
        <p className="muted">Elija una cuenta.</p>
      ) : data === null ? (
        <Loading error={error} />
      ) : (
        <>
          <dl className="summary">
            <div>
              <dt>Cuenta</dt>
              <dd>
                <span className="mono">{data.code}</span> {data.name} · {accountClassLabel(data.accountClass)}
              </dd>
            </div>
            <div>
              <dt>Saldo inicial</dt>
              <dd>
                <Money value={data.opening} currency />
              </dd>
            </div>
            <div>
              <dt>Débitos / créditos</dt>
              <dd>
                <Money value={data.totalDebit} currency /> / <Money value={data.totalCredit} currency />
              </dd>
            </div>
            <div>
              <dt>Saldo final</dt>
              <dd>
                <Money value={data.closing} testId="ledger-closing" currency />
              </dd>
            </div>
          </dl>
          <div className="actions">
            <a className="button" href={csvUrl(PATH, { path: params.path, query: { from, to } })} download>
              Descargar CSV
            </a>
          </div>
          <div className="table-wrap"><table>
            <thead>
              <tr>
                <th>Fecha</th>
                <th>Documento</th>
                <th>Evento</th>
                <th className="num">Débito (RD$)</th>
                <th className="num">Crédito (RD$)</th>
                <th className="num">Saldo (RD$)</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {data.movements.map((m) => (
                <tr key={m.glEntryId}>
                  <td>{formatDate(m.postingDate)}</td>
                  <td>
                    {m.documentKind ? DOCUMENTS[m.documentKind] ?? m.documentKind : "—"} <span className="mono">{m.documentNumber ?? ""}</span>
                    {m.journalType === "REVERSAL" ? <span className="muted"> (reversa)</span> : null}
                  </td>
                  <td className="muted">{m.eventType ?? ""}</td>
                  <td className="num">{/[1-9]/.test(m.debit) ? <Money value={m.debit} /> : null}</td>
                  <td className="num">{/[1-9]/.test(m.credit) ? <Money value={m.credit} /> : null}</td>
                  <td className="num">
                    <Money value={m.balance} />
                  </td>
                  <td>{can("audit:read") ? <Link href={`/auditoria/explicar/?entrada=${m.glEntryId}`}>Explicar</Link> : null}</td>
                </tr>
              ))}
            </tbody>
          </table></div>
          <div className="actions">
            <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>
              Anteriores
            </button>
            <span className="muted">
              {data.count === 0 ? "Sin movimientos" : `${offset + 1}–${Math.min(offset + PAGE, data.count)} de ${data.count}`}
            </span>
            <button type="button" disabled={offset + PAGE >= data.count} onClick={() => setOffset(offset + PAGE)}>
              Siguientes
            </button>
          </div>
        </>
      )}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Ledger />
    </Suspense>
  );
}
