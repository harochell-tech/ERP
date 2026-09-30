"use client";

import Link from "next/link";
import { Fragment, useState } from "react";
import { ApiError, query, type Schemas } from "@/api/client";
import { ErrorBox, Field, Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { todayInDominicanRepublic } from "@/lib/labels";
import { csvUrl } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

const BALANCE = "/api/v1/companies/{companyId}/finance/balance-sheet";
const INCOME = "/api/v1/companies/{companyId}/finance/income-statement";

/** E-FIN1-04-9: what is missing before the statements can be produced, with where to fix it. */
function Blocked({ error }: { error: unknown }) {
  if (error instanceof ApiError && error.code === "ACCOUNT_CLASS_MISSING") {
    return (
      <div className="notice" role="alert">
        Hay cuentas activas sin clasificar (sin clase): {error.message.replace(/^.*?:\s*/, "")} Asígnela en el <Link href="/contabilidad/cuentas/">catálogo de cuentas</Link>.
      </div>
    );
  }
  if (error instanceof ApiError && error.code === "STRUCTURE_MISSING") {
    return (
      <div className="notice" role="alert">
        No hay una estructura de reporte aprobada. Prepárela en <Link href="/contabilidad/estructuras/">Estructuras de reporte</Link>.
      </div>
    );
  }
  return <ErrorBox error={error} />;
}

function Lines({ lines, unassigned }: { lines: Schemas["StatementLine"][]; unassigned: Schemas["StatementAccount"][] }) {
  const [open, setOpen] = useState<Record<string, boolean>>({});
  return (
    <div className="table-wrap"><table>
      <thead>
        <tr>
          <th>Concepto</th>
          <th className="num">Monto (RD$)</th>
        </tr>
      </thead>
      <tbody>
        {lines.map((l) => (
          <Fragment key={l.lineCode}>
            <tr>
              <td style={{ paddingLeft: 12 + l.depth * 20 }}>
                {l.accounts.length > 0 ? (
                  <button type="button" className="link" aria-expanded={open[l.lineCode] ?? false} onClick={() => setOpen({ ...open, [l.lineCode]: !open[l.lineCode] })}>
                    {open[l.lineCode] ? "▾" : "▸"}
                  </button>
                ) : null}{" "}
                {l.depth === 0 ? <strong>{l.caption}</strong> : l.caption}
              </td>
              <td className="num" data-testid={`line-${l.lineCode}`}>
                <Money value={l.amount} />
              </td>
            </tr>
            {open[l.lineCode]
              ? l.accounts.map((a) => (
                  <tr key={a.accountId} className="muted">
                    <td style={{ paddingLeft: 32 + l.depth * 20 }}>
                      <span className="mono">{a.code}</span> {a.name}
                    </td>
                    <td className="num">
                      <Money value={a.amount} />
                    </td>
                  </tr>
                ))
              : null}
          </Fragment>
        ))}
        {unassigned.map((a) => (
          <tr key={a.accountId}>
            <td>
              <span className="mono">{a.code}</span> {a.name} <span className="badge tone-attention">Sin línea en la estructura</span>
            </td>
            <td className="num">
              <Money value={a.amount} />
            </td>
          </tr>
        ))}
      </tbody>
    </table></div>
  );
}

/** UX4-02 (A-10): plain wording for the report structure the statement follows (the version number only as a tooltip). */
function StructureNote({ version }: { version: number }) {
  return (
    <span className="muted" title={`Versión ${version} de la estructura de reporte`}>
      Agrupado según el formato de reporte aprobado (<Link href="/contabilidad/estructuras/">ver formatos</Link>)
    </span>
  );
}

function Total({ label, value, testId }: { label: string; value: string; testId?: string }) {
  return (
    <div className="stat">
      <span>{label}</span>
      <span className="value">
        <Money value={value} testId={testId} currency />
      </span>
    </div>
  );
}

// FIN1-04 (E-FIN1-04-9, E-FIN1-03-3/5): balance sheet at a date and income statement of a range, from the approved structures.
// UX4-02 (A-10, E-UX4-2): the results of the year and of prior years read inside Patrimonio and the sheet ends with "Total pasivo
// + patrimonio" (the server's totals); the difference shows only when it does not balance.
export default function Page() {
  const { companyId, can } = useSession();
  const today = todayInDominicanRepublic();
  const [tab, setTab] = useState<"balance" | "income">("balance");
  const [asOf, setAsOf] = useState(today);
  const [from, setFrom] = useState(`${today.slice(0, 4)}-01-01`);
  const [to, setTo] = useState(today);
  const allowed = can("ledger:read");
  const balanceParams = { path: { companyId }, query: { asOf } };
  const incomeParams = { path: { companyId }, query: { from, to } };
  const balance = useLoad(allowed && tab === "balance" && asOf ? () => query(BALANCE, balanceParams) : null, [companyId, asOf, tab, allowed]);
  const income = useLoad(allowed && tab === "income" && from && to ? () => query(INCOME, incomeParams) : null, [companyId, from, to, tab, allowed]);
  if (!allowed) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Estados financieros</h1>
      <div className="tabs" role="tablist">
        <button type="button" role="tab" aria-selected={tab === "balance"} onClick={() => setTab("balance")}>
          Balance general
        </button>
        <button type="button" role="tab" aria-selected={tab === "income"} onClick={() => setTab("income")}>
          Estado de resultados
        </button>
      </div>
      {tab === "balance" ? (
        <>
          <Field label="Fecha">
            <input type="date" aria-label="Fecha del balance" value={asOf} onChange={(e) => setAsOf(e.target.value)} />
          </Field>
          {balance.data === null ? (
            balance.error ? <Blocked error={balance.error} /> : <Loading />
          ) : (
            <>
              <div className="actions">
                <StatusBadge status={balance.data.balanced ? "MATCHED" : "EXCEPTIONS"} label={balance.data.balanced ? "Cuadra" : `Diferencia ${formatDecimal(balance.data.difference)}`} testId="balance-status" />
                <StructureNote version={balance.data.structureVersion} />
                <a className="button" href={csvUrl(BALANCE, balanceParams)} download>
                  Descargar CSV
                </a>
              </div>
              <Lines lines={balance.data.lines} unassigned={balance.data.unassignedAccounts} />
              <div className="table-wrap">
                <table data-testid="balance-summary">
                  <caption className="muted" style={{ textAlign: "left" }}>
                    Resumen
                  </caption>
                  <tbody>
                    <tr>
                      <th scope="row">Total activo</th>
                      <td className="num">
                        <Money value={balance.data.totalAssets} testId="total-assets" currency />
                      </td>
                    </tr>
                    <tr>
                      <th scope="row">Total pasivo</th>
                      <td className="num">
                        <Money value={balance.data.totalLiabilities} testId="total-liabilities" currency />
                      </td>
                    </tr>
                    <tr>
                      <th scope="row">Patrimonio</th>
                      <td />
                    </tr>
                    <tr>
                      <td style={{ paddingLeft: 32 }}>Capital, reservas y otras cuentas de patrimonio</td>
                      <td className="num">
                        <Money value={balance.data.totalEquity} />
                      </td>
                    </tr>
                    <tr>
                      <td style={{ paddingLeft: 32 }}>Resultado del ejercicio</td>
                      <td className="num">
                        <Money value={balance.data.currentYearResult} testId="current-year-result" />
                      </td>
                    </tr>
                    <tr>
                      <td style={{ paddingLeft: 32 }}>Resultados de ejercicios anteriores</td>
                      <td className="num">
                        <Money value={balance.data.priorYearsResult} />
                      </td>
                    </tr>
                    <tr>
                      <th scope="row">Total patrimonio</th>
                      <td className="num">
                        <Money value={balance.data.totalEquityWithResults} testId="total-equity" currency />
                      </td>
                    </tr>
                    <tr>
                      <th scope="row">Total pasivo + patrimonio</th>
                      <td className="num">
                        <strong>
                          <Money value={balance.data.totalLiabilitiesAndEquity} testId="total-liabilities-and-equity" currency />
                        </strong>
                      </td>
                    </tr>
                    {balance.data.balanced ? null : (
                      <tr>
                        <th scope="row">Diferencia (activo − pasivo y patrimonio)</th>
                        <td className="num">
                          <Money value={balance.data.difference} testId="balance-difference" currency />
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </>
      ) : (
        <>
          <Field label="Desde">
            <input type="date" aria-label="Desde" value={from} onChange={(e) => setFrom(e.target.value)} />
          </Field>
          <Field label="Hasta">
            <input type="date" aria-label="Hasta" value={to} onChange={(e) => setTo(e.target.value)} />
          </Field>
          {income.data === null ? (
            income.error ? <Blocked error={income.error} /> : <Loading />
          ) : (
            <>
              <div className="actions">
                <StructureNote version={income.data.structureVersion} />
                <a className="button" href={csvUrl(INCOME, incomeParams)} download>
                  Descargar CSV
                </a>
              </div>
              <Lines lines={income.data.lines} unassigned={income.data.unassignedAccounts} />
              <div className="cards">
                <Total label="Ingresos" value={income.data.revenue} />
                <Total label="Costos" value={income.data.cost} />
                <Total label="Gastos" value={income.data.expenses} />
                <Total label="Resultado neto" value={income.data.netIncome} testId="net-income" />
              </div>
            </>
          )}
        </>
      )}
    </>
  );
}
