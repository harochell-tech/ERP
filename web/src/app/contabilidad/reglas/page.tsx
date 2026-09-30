"use client";

import { query, type Schemas } from "@/api/client";
import { ConfirmAction, ErrorBox, Loading, NoPermission, StatusBadge } from "@/components/ui";
import { humanizeExplanation, ruleLinesSummary } from "@/lib/configuration";
import { COMPONENTS, formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type RuleVersion = Schemas["PostingRuleVersionView"];

// E-B03-15-2 / UX2-02 (E-UX2-7): posting rules arrive as DRAFT with the migrations; the Controller approves each version (step-up).
// Each version shows what it generates — "Genera: Débito <role> / Crédito <role>" by the roles' names — and each line's explanation.

function RuleRow({ rule, onDone }: { rule: RuleVersion; onDone: () => void }) {
  const { can } = useSession();
  const approve = useCommand(`approve-rule:${rule.ruleCode}:${rule.version}`, "/api/v1/companies/{companyId}/finance/approve-posting-rule-version", `Regla ${rule.ruleCode} v${rule.version} aprobada y activa.`);
  const summary = ruleLinesSummary(rule.lines);
  return (
    <tr>
      <td>{rule.ruleCode}</td>
      <td className="wrap" style={{ minWidth: 260 }}>
        <div>
          <span className="mono">{rule.eventType}</span>
        </div>
        {summary ? <strong data-testid={`rule-summary:${rule.ruleCode}:${rule.version}`}>{summary}</strong> : null}
        {rule.lines && rule.lines.length > 0 ? (
          <ul className="plain-list muted">
            {rule.lines.map((l) => (
              <li key={l.code}>
                {l.side === "DEBIT" ? "Débito" : "Crédito"} {l.accountRoleName ?? l.accountRole}
                {l.explanation ? `: ${humanizeExplanation(l.explanation)}` : ""}
              </li>
            ))}
          </ul>
        ) : null}
      </td>
      <td className="num">{rule.version}</td>
      <td>{COMPONENTS[rule.closeComponent] ?? rule.closeComponent}</td>
      <td>
        {formatDate(rule.effectiveFrom)}
        {rule.effectiveTo ? ` – ${formatDate(rule.effectiveTo)}` : ""}
      </td>
      <td>
        <StatusBadge status={rule.status} />
      </td>
      <td className="wrap">{rule.approvedBy ?? "—"}</td>
      <td>
        {rule.status === "DRAFT" && can("posting_rule:approve") ? (
          <ConfirmAction
            label="Aprobar"
            title={`¿Aprobar la regla ${rule.ruleCode} versión ${rule.version}?`}
            stepUp
            busy={approve.busy}
            consequence={`La versión ${rule.version} de ${rule.ruleCode} queda activa desde ${formatDate(rule.effectiveFrom)} y contabiliza los eventos ${rule.eventType} a partir de entonces${summary ? ` (${summary.replace(/^Genera: /, "")})` : ""}; no se puede volver a borrador.`}
            onConfirm={async () => (await approve.run({ ruleCode: rule.ruleCode, version: rule.version })) && onDone()}
          />
        ) : null}
        <ErrorBox error={approve.error} />
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("configuration:read");
  const { data, error, reload } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/finance/posting-rules", { path: { companyId } }) : null, [companyId]);

  if (!allowed) {
    return <NoPermission />;
  }
  const drafts = data?.items.filter((r) => r.status === "DRAFT") ?? [];
  return (
    <>
      <h1>Reglas de contabilización</h1>
      {drafts.length > 0 ? (
        <div className="alert-block" role="status" data-testid="draft-rules">
          <strong>Reglas en borrador</strong>
          Estas versiones no contabilizan hasta que el Controller las apruebe:
          <ul>
            {drafts.map((r) => (
              <li key={`${r.ruleCode}:${r.version}`}>
                {r.ruleCode} versión {r.version} ({r.eventType})
              </li>
            ))}
          </ul>
        </div>
      ) : null}
      {data === null ? (
        <Loading error={error} />
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Regla</th>
                <th>Evento y asiento</th>
                <th className="num">Versión</th>
                <th>Componente de cierre</th>
                <th>Vigencia</th>
                <th>Estado</th>
                <th>Aprobada por</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {data.items.map((r) => (
                <RuleRow key={`${r.ruleCode}:${r.version}:${r.status}`} rule={r} onDone={reload} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
