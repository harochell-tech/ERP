"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { ErrorBox, Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { accountClassLabel, REPORTS } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// FIN1-04 (E-FIN1-04-10, E-FIN1-03-2): a structure version, its lines and accounts, the active accounts still missing, and
// "Aprobar" for the Aprobador de políticas (never the preparer).
function StructureDetail() {
  const { companyId, can, state } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const approve = useCommand(`approve-report-structure:${id}`, "/api/v1/companies/{companyId}/finance/approve-report-structure");
  const { data, error, reload } = useLoad(
    can("configuration:read") && id ? () => query("/api/v1/companies/{companyId}/finance/report-structures/{structureVersionId}", { path: { companyId, structureVersionId: id } }) : null,
    [companyId, id],
  );
  if (!can("configuration:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const h = data.header;
  const email = state.status === "ready" ? state.session.email : null;
  const depth = (code: string | null | undefined): number => {
    let d = 0;
    for (let c = code; c; c = data.lines.find((l) => l.lineCode === c)?.parentLineCode ?? null) {
      d++;
    }
    return d - 1;
  };
  return (
    <>
      <div className="actions">
        <h1 style={{ margin: 0 }}>
          {REPORTS[h.report] ?? h.report} · versión {h.version}
        </h1>
        <StatusBadge status={h.status} testId="structure-status" />
      </div>
      <p className="muted">
        Vigente desde {formatDate(h.effectiveFrom)} · preparó {h.preparedBy ?? "—"} · aprobó {h.approvedBy ?? "—"}
      </p>
      <div className="actions">
        {h.status === "DRAFT" && can("report_structure:approve") && h.preparedBy !== email ? (
          <button
            type="button"
            className="primary"
            disabled={approve.busy || data.missingAccounts.length > 0}
            onClick={async () => {
              if (await approve.run({ structureVersionId: id })) {
                reload();
              }
            }}
          >
            Aprobar estructura
          </button>
        ) : null}
        {can("account:manage") ? (
          <Link className="button" href={`/contabilidad/estructuras/nueva/?reporte=${h.report}&desde=${id}`}>
            Nueva versión a partir de esta
          </Link>
        ) : null}
      </div>
      <ErrorBox error={approve.error} />
      {data.missingAccounts.length > 0 ? (
        <div className="notice" role="alert">
          <strong>Cuentas activas sin línea ({data.missingAccounts.length}):</strong>{" "}
          {data.missingAccounts.map((a) => `${a.code} ${a.name}`).join(" · ")}
          {h.status === "DRAFT" ? " — no se puede aprobar hasta ubicarlas." : " — la conciliación STRUCT-COVERAGE lo advierte."}
        </div>
      ) : null}
      <table>
        <thead>
          <tr>
            <th>Línea</th>
            <th>Concepto</th>
            <th>Signo</th>
            <th>Cuentas</th>
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => (
            <tr key={l.lineCode}>
              <td className="mono">{l.lineCode}</td>
              <td style={{ paddingLeft: 12 + depth(l.lineCode) * 20 }}>{l.caption}</td>
              <td>{l.sign === 1 ? "Deudor (+)" : "Acreedor (−)"}</td>
              <td>
                {l.accounts.map((a) => (
                  <div key={a.accountId}>
                    <span className="mono">{a.code}</span> {a.name} <span className="muted">· {accountClassLabel(a.accountClass)}</span>
                  </div>
                ))}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <StructureDetail />
    </Suspense>
  );
}
