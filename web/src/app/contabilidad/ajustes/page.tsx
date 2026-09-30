"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate, statusLabel } from "@/lib/labels";
import { inMonth } from "@/lib/ux4a-contabilidad";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

const STATUSES = ["", "DRAFT", "PENDING_APPROVAL", "POSTED", "REJECTED", "REVERSED"];

// FIN1-04: the adjustment journal (E-FIN1-1…10). The total is the server's.
// UX4-02 (A-15): no Componente column (a fiscal adjustment says so in its description cell) and a month filter on the posting date.
export default function Page() {
  const { companyId, can } = useSession();
  const [status, setStatus] = useState("");
  const [month, setMonth] = useState("");
  const allowed = can("ledger:read");
  const { data, error } = useLoad(
    allowed ? () => query("/api/v1/companies/{companyId}/finance/manual-journals", { path: { companyId }, query: { status, limit: 200 } }) : null,
    [companyId, status, allowed],
  );
  if (!allowed) {
    return <NoPermission />;
  }
  return (
    <>
      <div className="actions">
        <h1 style={{ margin: 0 }}>Diario de ajustes</h1>
        {can("manual_journal:prepare") ? (
          <Link className="button primary" href="/contabilidad/ajustes/nuevo/">
            Nuevo ajuste
          </Link>
        ) : null}
      </div>
      <p className="muted">Ajustes a cuentas que no son de control; los aprueba una persona distinta de quien los prepara.</p>
      <label className="field">
        <span>Estado</span>
        <select aria-label="Estado" value={status} onChange={(e) => setStatus(e.target.value)}>
          {STATUSES.map((s) => (
            <option key={s} value={s}>
              {s ? statusLabel(s) : "Todos"}
            </option>
          ))}
        </select>
      </label>
      <label className="field">
        <span>Mes</span>
        <input type="month" aria-label="Mes" value={month} onChange={(e) => setMonth(e.target.value)} />
      </label>
      {month ? (
        <button type="button" className="link" onClick={() => setMonth("")}>
          Ver todos los meses
        </button>
      ) : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.filter((j) => inMonth(j.postingDate, month)).length === 0 ? (
        <p className="muted">{month || status ? "No hay ajustes con estos filtros." : "No hay ajustes."}</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Número</th>
              <th>Fecha</th>
              <th>Descripción</th>
              <th className="num">Total (RD$)</th>
              <th>Estado</th>
              <th>Preparó</th>
            </tr>
          </thead>
          <tbody>
            {data.items.filter((j) => inMonth(j.postingDate, month)).map((j) => (
              <tr key={j.manualJournalId}>
                <td className="mono">
                  <Link href={`/contabilidad/ajuste/?id=${j.manualJournalId}`}>{j.journalNo}</Link>
                </td>
                <td>{formatDate(j.postingDate)}</td>
                <td className="wrap">
                  {j.description}
                  {j.autoReverse ? <span className="muted"> · reversa automática</span> : null}
                  {j.closeComponent === "ACR-TAX" ? <span className="muted"> · con efecto fiscal</span> : null}
                </td>
                <td className="num">
                  <Money value={j.total} />
                </td>
                <td className="wrap">
                  <StatusBadge status={j.status} />
                </td>
                <td className="wrap">{j.preparedBy ?? "—"}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
