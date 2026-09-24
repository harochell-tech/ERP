"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, NoPermission } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

/** The journals an event produced (E-11: the accounting evidence), each line linked to its explanation (EX-01). */
function Journals() {
  const { companyId, can } = useSession();
  const eventId = useSearchParams().get("evento") ?? "";
  const { data, error } = useLoad(
    can("audit:read") && eventId
      ? () => query("/api/v1/companies/{companyId}/finance/events/{sourceEventId}/journals", { path: { companyId, sourceEventId: eventId } })
      : null,
    [companyId, eventId],
  );

  if (!can("audit:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <h1>Asientos del evento</h1>
      <p className="muted">{data.sourceEventId}</p>
      {data.journals.map((j) => (
        <section key={j.journalId} data-testid="journal">
          <h2>
            {j.ruleCode} v{j.ruleVersion} — {j.journalType}, generación {j.generation}, fecha contable {formatDate(j.postingDate)}
            {j.lateEntry ? " (registro tardío)" : ""}
          </h2>
          {j.reversesJournalId ? <p>Reversa del asiento {j.reversesJournalId}.</p> : null}
          <table>
            <thead>
              <tr>
                <th>#</th>
                <th>Cuenta</th>
                <th>Rol</th>
                <th className="num">Débito</th>
                <th className="num">Crédito</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {j.entries.map((e) => (
                <tr key={e.glEntryId}>
                  <td>{e.lineNo}</td>
                  <td>
                    {e.accountCode} {e.accountName}
                  </td>
                  <td>{e.accountRole}</td>
                  <td className="num">{formatDecimal(e.debit)}</td>
                  <td className="num">{formatDecimal(e.credit)}</td>
                  <td>
                    <Link href={`/auditoria/explicar/?entrada=${e.glEntryId}`}>Explicar</Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      ))}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Journals />
    </Suspense>
  );
}
