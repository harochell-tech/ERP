"use client";

import { query, type Schemas } from "@/api/client";
import { ErrorBox, Loading, NoPermission } from "@/components/ui";
import { COMPONENTS, formatDate, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type RuleVersion = Schemas["PostingRuleVersionView"];

// E-B03-15-2: posting rules arrive as DRAFT with the migrations; the Controller approves each version (step-up).

function RuleRow({ rule, onDone }: { rule: RuleVersion; onDone: () => void }) {
  const { can } = useSession();
  const approve = useCommand(`approve-rule:${rule.ruleCode}:${rule.version}`, "/api/v1/companies/{companyId}/finance/approve-posting-rule-version");
  return (
    <tr>
      <td>{rule.ruleCode}</td>
      <td>{rule.eventType}</td>
      <td className="num">{rule.version}</td>
      <td>{COMPONENTS[rule.closeComponent] ?? rule.closeComponent}</td>
      <td>
        {formatDate(rule.effectiveFrom)}
        {rule.effectiveTo ? ` – ${formatDate(rule.effectiveTo)}` : ""}
      </td>
      <td>{statusLabel(rule.status)}</td>
      <td>{rule.approvedBy ?? "—"}</td>
      <td>
        {rule.status === "DRAFT" && can("posting_rule:approve") ? (
          <button type="button" disabled={approve.busy} onClick={async () => (await approve.run({ ruleCode: rule.ruleCode, version: rule.version })) && onDone()}>
            Aprobar
          </button>
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
  return (
    <>
      <h1>Reglas de contabilización</h1>
      {data === null ? (
        <Loading error={error} />
      ) : (
        <table>
          <thead>
            <tr>
              <th>Regla</th>
              <th>Evento</th>
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
      )}
    </>
  );
}
