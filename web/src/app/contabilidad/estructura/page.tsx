"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { ConfirmAction, ErrorBox, Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { accountClassLabel, REPORTS } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { effectiveFromLabel, lineOptionLabel, STRUCTURE_NEXT_STEP } from "@/lib/ux4a-contabilidad";

// FIN1-04 (E-FIN1-04-10, E-FIN1-03-2): a structure version, its lines and accounts, the active accounts still missing, and
// "Aprobar" for the Aprobador de políticas (never the preparer).
// UX4-02 (A-17): a draft "regirá desde" (not "vigente desde"), says what follows and who approves; no "aprobó —".
function StructureDetail() {
  const { companyId, can, isMine } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const approve = useCommand(`approve-report-structure:${id}`, "/api/v1/companies/{companyId}/finance/approve-report-structure", "Estructura de reporte aprobada y activa.");
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
  // UX1-01b: the API returns the preparer's display name (its e-mail until the first sign-in brings a name, E-UX1-01-3).
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
        <h1 style={{ margin: 0, overflowWrap: "anywhere" }}>
          {REPORTS[h.report] ?? h.report} · versión {h.version}
        </h1>
        <StatusBadge status={h.status} testId="structure-status" />
      </div>
      <p className="muted">
        {effectiveFromLabel(h.status)} {formatDate(h.effectiveFrom)} · preparó {h.preparedBy ?? "—"}
        {h.approvedBy ? ` · aprobó ${h.approvedBy}` : ""}
      </p>
      {h.status === "DRAFT" ? (
        <p className="notice" data-testid="structure-next-step">
          <strong>Qué sigue:</strong> {STRUCTURE_NEXT_STEP}
        </p>
      ) : null}
      <div className="actions">
        {h.status === "DRAFT" && can("report_structure:approve") && !isMine(h.preparedBy) ? (
          <ConfirmAction
            label="Aprobar estructura"
            className="primary"
            stepUp
            busy={approve.busy}
            disabled={data.missingAccounts.length > 0}
            consequence={`La versión ${h.version} de ${REPORTS[h.report] ?? h.report} queda activa desde ${formatDate(h.effectiveFrom)} y reemplaza a la anterior en los estados financieros; no se puede volver a borrador.`}
            onConfirm={async () => {
              if (await approve.run({ structureVersionId: id })) {
                reload();
              }
            }}
          />
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
          {h.status === "DRAFT" ? " — no se puede aprobar hasta ubicarlas." : " — la verificación de cobertura de las estructuras lo advierte en el cierre."}
        </div>
      ) : null}
      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th>Concepto</th>
            <th>Agrupada bajo</th>
            <th>Signo</th>
            <th>Cuentas</th>
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => (
            <tr key={l.lineCode}>
              <td style={{ paddingLeft: 12 + depth(l.lineCode) * 20 }}>
                {l.caption} <span className="muted mono">({l.lineCode})</span>
              </td>
              <td>{l.parentLineCode ? lineOptionLabel(l.parentLineCode, data.lines.find((p) => p.lineCode === l.parentLineCode)?.caption ?? "") : "—"}</td>
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
      </table></div>
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
