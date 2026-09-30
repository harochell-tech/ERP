"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { Field, Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { todayInDominicanRepublic } from "@/lib/labels";
import { accountClassLabel, csvUrl, monthStart } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { bankAccountLabel } from "@/lib/ux4a";
import { balanceCell } from "@/lib/ux4a-contabilidad";

const PATH = "/api/v1/companies/{companyId}/finance/trial-balance";

// FIN1-04 (E-FIN1-04-7, E-FIN1-03-4/6): the trial balance of a range, optionally filtered; an account opens its ledger.
// UX4-02 (A-11, E-UX4-2): the closing balance reads as "Saldo deudor" / "Saldo acreedor" (the server's split and totals).
export default function Page() {
  const { companyId, can, plantName } = useSession();
  const today = todayInDominicanRepublic();
  const [from, setFrom] = useState(monthStart(today));
  const [to, setTo] = useState(today);
  const [plantId, setPlantId] = useState("");
  const [partyId, setPartyId] = useState("");
  const [bankAccountId, setBankAccountId] = useState("");
  const allowed = can("ledger:read");
  const params = { path: { companyId }, query: { from, to, plantId, partyId, bankAccountId } };
  const { data, error } = useLoad(allowed && from && to ? () => query(PATH, params) : null, [companyId, from, to, plantId, partyId, bankAccountId, allowed]);
  const { data: filters } = useLoad(
    allowed
      ? async () => {
          const [plants, suppliers, banks] = await Promise.all([
            can("master_data:read") ? query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId } }) : Promise.resolve(null),
            can("master_data:read") ? query("/api/v1/companies/{companyId}/master-data/suppliers", { path: { companyId }, query: { limit: 200 } }) : Promise.resolve(null),
            can("bank:read") ? query("/api/v1/companies/{companyId}/treasury/bank-accounts", { path: { companyId } }) : Promise.resolve(null),
          ]);
          return { plants: plants?.items ?? [], suppliers: suppliers?.items ?? [], banks: banks?.items ?? [] };
        }
      : null,
    [companyId, allowed],
  );
  if (!allowed) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Balanza de comprobación</h1>
      <div>
        <Field label="Desde">
          <input type="date" aria-label="Desde" value={from} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label="Hasta">
          <input type="date" aria-label="Hasta" value={to} onChange={(e) => setTo(e.target.value)} />
        </Field>
        {filters && filters.plants.length > 0 ? (
          <Field label="Planta">
            <select aria-label="Planta" value={plantId} onChange={(e) => setPlantId(e.target.value)}>
              <option value="">Todas</option>
              {filters.plants.map((p) => (
                <option key={p.plantId} value={p.plantId}>
                  {plantName(p.plantId, p.code)}
                </option>
              ))}
            </select>
          </Field>
        ) : null}
        {filters && filters.suppliers.length > 0 ? (
          <Field label="Proveedor">
            <select aria-label="Proveedor" value={partyId} onChange={(e) => setPartyId(e.target.value)}>
              <option value="">Todos</option>
              {filters.suppliers.map((s) => (
                <option key={s.supplierId} value={s.supplierId}>
                  {s.legalName}
                </option>
              ))}
            </select>
          </Field>
        ) : null}
        {filters && filters.banks.length > 0 ? (
          <Field label="Cuenta bancaria">
            <select aria-label="Cuenta bancaria" value={bankAccountId} onChange={(e) => setBankAccountId(e.target.value)}>
              <option value="">Todas</option>
              {filters.banks.map((b) => (
                <option key={b.bankAccountId} value={b.bankAccountId}>
                  {bankAccountLabel(b)}
                </option>
              ))}
            </select>
          </Field>
        ) : null}
      </div>
      {data === null ? (
        <Loading error={error} />
      ) : (
        <>
          <div className="actions">
            {data.filtered ? (
              <span className="notice" style={{ margin: 0 }}>
                Balanza filtrada: muestra solo las líneas del filtro y no tiene que cuadrar.
              </span>
            ) : (
              <StatusBadge status={data.balanced ? "MATCHED" : "EXCEPTIONS"} label={data.balanced ? "Cuadra" : "No cuadra"} testId="trial-balance-status" />
            )}
            <a className="button" href={csvUrl(PATH, params)} download>
              Descargar CSV
            </a>
          </div>
          <div className="table-wrap"><table>
            <thead>
              <tr>
                <th>Código</th>
                <th>Cuenta</th>
                <th>Clase</th>
                <th className="num">Saldo inicial (RD$)</th>
                <th className="num">Débitos (RD$)</th>
                <th className="num">Créditos (RD$)</th>
                <th className="num">Saldo deudor (RD$)</th>
                <th className="num">Saldo acreedor (RD$)</th>
              </tr>
            </thead>
            <tbody>
              {data.rows.map((r) => (
                <tr key={r.accountId ?? "prior-years"}>
                  <td className="mono">{r.code}</td>
                  <td>{r.accountId ? <Link href={`/contabilidad/mayor/?cuenta=${r.accountId}&desde=${from}&hasta=${to}`}>{r.name}</Link> : r.name}</td>
                  <td>{r.accountId ? accountClassLabel(r.accountClass) : "Patrimonio"}</td>
                  <td className="num">
                    <Money value={r.opening} />
                  </td>
                  <td className="num">
                    <Money value={r.debit} />
                  </td>
                  <td className="num">
                    <Money value={r.credit} />
                  </td>
                  <td className="num">
                    <Money value={balanceCell(r.debitBalance)} />
                  </td>
                  <td className="num">
                    <Money value={balanceCell(r.creditBalance)} />
                  </td>
                </tr>
              ))}
              <tr>
                <td />
                <td>
                  <strong>Totales</strong>
                </td>
                <td />
                <td className="num">
                  <Money value={data.totalOpening} />
                </td>
                <td className="num">
                  <Money value={data.totalDebit} testId="trial-balance-debit" />
                </td>
                <td className="num">
                  <Money value={data.totalCredit} testId="trial-balance-credit" />
                </td>
                <td className="num">
                  <Money value={data.totalDebitBalance} testId="trial-balance-debit-balance" />
                </td>
                <td className="num">
                  <Money value={data.totalCreditBalance} testId="trial-balance-credit-balance" />
                </td>
              </tr>
            </tbody>
          </table></div>
        </>
      )}
    </>
  );
}
