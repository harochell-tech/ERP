"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, NoPermission } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

/** The PR-17 Explain document (its schema is free-form in OpenAPI); only the fields this page shows. */
interface Explanation {
  explanation: string;
  complete: boolean;
  entry: { lineNo: number; ruleLineCode: string; account: { code: string; name: string; role: string }; debit: string; credit: string };
  journal: { journalId: string; type: string; generation: number; postingDate: string; lateEntry: boolean; reversesJournalId: string | null; reversedByJournalId: string | null };
  event: { eventId: string; type: string; occurredAt: string; businessDate: string; command: string | null; user: string | null };
  document: { kind: string; number: string | null; purchaseOrder: string | null } | null;
  rule: { code: string; version: number; closeComponent: string | null };
  mapping: unknown;
  policies: { policy: string; version: number; effectiveFrom: string }[];
  fiscal: { date: string; lines: { taxCode: string; base: string; rate: string; amount: string; effect: string }[] } | null;
  determinationInputs: unknown;
  integrity: { status: string; ledgerSequence: number | null };
}

/** EX-01 (v2 §13.1): why a GL line exists — event, document, rule and version, mapping, policies, fiscal determination and text. */
function Explain() {
  const { companyId, can } = useSession();
  const entryId = useSearchParams().get("entrada") ?? "";
  const { data, error } = useLoad(
    can("audit:read") && entryId
      ? async () =>
          (await query("/api/v1/companies/{companyId}/finance/entries/{glEntryId}/explanation", { path: { companyId, glEntryId: entryId } })) as unknown as Explanation
      : null,
    [companyId, entryId],
  );

  if (!can("audit:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <h1>Explicación del asiento</h1>
      <p data-testid="explanation">
        <strong>{data.explanation}</strong>
      </p>
      {!data.complete ? <p className="error">Faltan datos para completar la explicación; los marcadores sin valor se muestran tal cual.</p> : null}
      <dl className="facts">
        <dt>Línea</dt>
        <dd>
          {data.entry.lineNo} ({data.entry.ruleLineCode}) — {data.entry.account.code} {data.entry.account.name} [{data.entry.account.role}] — débito{" "}
          {formatDecimal(data.entry.debit)}, crédito {formatDecimal(data.entry.credit)}
        </dd>
        <dt>Asiento</dt>
        <dd>
          {data.journal.type}, generación {data.journal.generation}, fecha contable {formatDate(data.journal.postingDate)}
          {data.journal.lateEntry ? " (tardío)" : ""}
          {data.journal.reversesJournalId ? ` — reversa de ${data.journal.reversesJournalId}` : ""}
          {data.journal.reversedByJournalId ? ` — reversado por ${data.journal.reversedByJournalId}` : ""}
        </dd>
        <dt>Evento</dt>
        <dd>
          {data.event.type} — ocurrió {formatDateTime(data.event.occurredAt)}, fecha de negocio {formatDate(data.event.businessDate)}, comando {data.event.command ?? "—"}, usuario{" "}
          {data.event.user ?? "—"} (<Link href={`/auditoria/asientos/?evento=${data.event.eventId}`}>asientos del evento</Link>)
        </dd>
        <dt>Documento</dt>
        <dd>{data.document ? `${data.document.kind} ${data.document.number ?? ""}${data.document.purchaseOrder ? ` (orden ${data.document.purchaseOrder})` : ""}` : "—"}</dd>
        <dt>Regla</dt>
        <dd>
          {data.rule.code} versión {data.rule.version}
          {data.rule.closeComponent ? ` — componente ${data.rule.closeComponent}` : ""}
        </dd>
        <dt>Políticas</dt>
        <dd>{data.policies.length === 0 ? "—" : data.policies.map((p) => `${p.policy} v${p.version} (desde ${formatDate(p.effectiveFrom)})`).join("; ")}</dd>
        <dt>Integridad</dt>
        <dd>
          {data.integrity.status}
          {data.integrity.ledgerSequence !== null ? ` — secuencia ${data.integrity.ledgerSequence}` : ""}
        </dd>
      </dl>
      {data.fiscal ? (
        <>
          <h2>Determinación fiscal ({formatDate(data.fiscal.date)})</h2>
          <table>
            <thead>
              <tr>
                <th>Impuesto</th>
                <th className="num">Base</th>
                <th className="num">Tasa</th>
                <th className="num">Monto</th>
                <th>Efecto</th>
              </tr>
            </thead>
            <tbody>
              {data.fiscal.lines.map((l, i) => (
                <tr key={i}>
                  <td>{l.taxCode}</td>
                  <td className="num">{formatDecimal(l.base)}</td>
                  <td className="num">{formatDecimal(l.rate, 0)}</td>
                  <td className="num">{formatDecimal(l.amount)}</td>
                  <td>{l.effect}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      ) : null}
      <h2>Mapeo de cuenta</h2>
      <pre>{JSON.stringify(data.mapping, null, 2)}</pre>
      <h2>Valores de entrada congelados</h2>
      <pre>{JSON.stringify(data.determinationInputs, null, 2)}</pre>
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Explain />
    </Suspense>
  );
}
