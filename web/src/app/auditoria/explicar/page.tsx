"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { Loading, NoPermission } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { commandLabel, documentHref, documentKindLabel, eventTypeLabel, integrityLabel, journalTypeLabel, ruleLabel } from "@/lib/explain";
import { COMPONENTS, formatDate, formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

/** The PR-17 Explain document (its schema is free-form in OpenAPI); only the fields this page shows. */
interface Explanation {
  explanation: string;
  complete: boolean;
  entry: {
    lineNo: number;
    ruleLineCode: string;
    account: { code: string; name: string; role: string };
    debit: string;
    credit: string;
    // E-UX3-4: the names beside the ids.
    plantCode: string | null;
    plantName: string | null;
    itemCode: string | null;
    itemDescription: string | null;
    partyLegalName: string | null;
  };
  journal: { journalId: string; type: string; generation: number; postingDate: string; lateEntry: boolean; reversesJournalId: string | null; reversedByJournalId: string | null };
  event: { eventId: string; type: string; occurredAt: string; businessDate: string; command: string | null; user: string | null; payload: unknown };
  document: { kind: string; id: string | null; number: string | null; purchaseOrder: string | null } | null;
  rule: { code: string; version: number; closeComponent: string | null };
  mapping: { accountRole: string; itemCategory: string | null; effectiveFrom: string; effectiveTo: string | null; approvedBy: string | null } | null;
  policies: { policy: string; version: number; effectiveFrom: string }[];
  fiscal: { date: string; lines: { taxCode: string; base: string; rate: string; amount: string; effect: string }[] } | null;
  determinationInputs: unknown;
  integrity: { status: string | null; ledgerSequence: number | null };
}

/**
 * EX-01 (v2 §13.1): why a GL line exists — event, document, rule and version, mapping, policies, fiscal determination and text.
 * UX3-02 (E-UX3-4): every code in Spanish, the mapping as a card, names beside the ids, the raw JSON folded.
 */
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
  // Account roles by their names when the reader may see the configuration (as the journals page, E-UX2-5).
  const roles = useLoad(can("configuration:read") ? () => query("/api/v1/companies/{companyId}/finance/account-roles", { path: { companyId } }) : null, [companyId]);
  const roleName = (code: string) => roles.data?.items.find((r) => r.roleCode === code)?.name ?? code;

  if (!can("audit:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const { entry, journal, event, document, rule, mapping } = data;
  const href = document ? documentHref(document.kind, document.id) : null;
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
          {entry.lineNo} — {entry.account.code} {entry.account.name} ({roleName(entry.account.role)}) — débito RD$ {formatDecimal(entry.debit)}, crédito RD${" "}
          {formatDecimal(entry.credit)}
        </dd>
        {entry.plantCode ? (
          <>
            <dt>Planta</dt>
            <dd>{entry.plantName ? `${entry.plantName} (${entry.plantCode})` : entry.plantCode}</dd>
          </>
        ) : null}
        {entry.itemCode ? (
          <>
            <dt>Artículo</dt>
            <dd>
              {entry.itemCode} {entry.itemDescription ?? ""}
            </dd>
          </>
        ) : null}
        {entry.partyLegalName ? (
          <>
            <dt>Tercero</dt>
            <dd>{entry.partyLegalName}</dd>
          </>
        ) : null}
        <dt>Asiento</dt>
        <dd>
          {journalTypeLabel(journal.type)}, generación {journal.generation}, fecha contable {formatDate(journal.postingDate)}
          {journal.lateEntry ? " (registro tardío)" : ""}
          {journal.reversesJournalId ? " — reversa de otro asiento" : ""}
          {journal.reversedByJournalId ? " — reversado después" : ""}
        </dd>
        <dt>Evento</dt>
        <dd>
          {eventTypeLabel(event.type)} — ocurrió {formatDateTime(event.occurredAt)}, fecha de negocio {formatDate(event.businessDate)} (
          <Link href={`/auditoria/asientos/?evento=${event.eventId}`}>asientos del evento</Link>)
        </dd>
        <dt>Acción</dt>
        <dd>
          {commandLabel(event.command)}, por {event.user ?? "—"}
        </dd>
        <dt>Documento</dt>
        <dd data-testid="explain-document">
          {document ? (
            <>
              {documentKindLabel(document.kind)} {href ? <Link href={href}>{document.number ?? "abrir"}</Link> : (document.number ?? "")}
              {document.purchaseOrder ? ` (orden ${document.purchaseOrder})` : ""}
            </>
          ) : (
            "—"
          )}
        </dd>
        <dt>Regla contable</dt>
        <dd>
          {ruleLabel(rule.code)}, versión {rule.version}
          {rule.closeComponent ? ` — cierra con ${COMPONENTS[rule.closeComponent] ?? rule.closeComponent}` : ""}
        </dd>
        <dt>Políticas</dt>
        <dd>{data.policies.length === 0 ? "—" : data.policies.map((p) => `${p.policy} versión ${p.version} (desde ${formatDate(p.effectiveFrom)})`).join("; ")}</dd>
        <dt>Integridad</dt>
        <dd>
          {integrityLabel(data.integrity.status)}
          {data.integrity.ledgerSequence !== null ? ` — secuencia ${data.integrity.ledgerSequence}` : ""}
        </dd>
      </dl>
      <section className="card" data-testid="mapping-card">
        <h2>Por qué esta cuenta</h2>
        {mapping ? (
          <dl className="facts">
            <dt>Rol de cuenta</dt>
            <dd>{roleName(mapping.accountRole)}</dd>
            <dt>Categoría de artículo</dt>
            <dd>{mapping.itemCategory ?? "Todas"}</dd>
            <dt>Vigencia del mapa</dt>
            <dd>
              Desde {formatDate(mapping.effectiveFrom)}
              {mapping.effectiveTo ? ` hasta ${formatDate(mapping.effectiveTo)}` : " (sin fecha final)"}
            </dd>
            <dt>Aprobado por</dt>
            <dd>{mapping.approvedBy ?? "—"}</dd>
            <dt>Cuenta</dt>
            <dd>
              {entry.account.code} {entry.account.name}
            </dd>
          </dl>
        ) : (
          <p className="muted">Esta línea no usó un mapa de cuentas (por ejemplo, un ajuste manual o una reversa exacta).</p>
        )}
      </section>
      {data.fiscal ? (
        <>
          <h2>Determinación fiscal ({formatDate(data.fiscal.date)})</h2>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Impuesto</th>
                  <th className="num">Base (RD$)</th>
                  <th className="num">Tasa</th>
                  <th className="num">Monto (RD$)</th>
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
          </div>
        </>
      ) : null}
      <details className="technical">
        <summary>Detalle técnico (avanzado)</summary>
        <p className="muted mono">
          Línea de regla {entry.ruleLineCode} · asiento {journal.journalId}
          {journal.reversesJournalId ? ` · reversa de ${journal.reversesJournalId}` : ""}
          {journal.reversedByJournalId ? ` · reversado por ${journal.reversedByJournalId}` : ""} · evento {event.type} · comando {event.command ?? "—"} · rol{" "}
          {entry.account.role}
        </p>
        <h3>Mapa de cuenta</h3>
        <pre>{JSON.stringify(mapping, null, 2)}</pre>
        <h3>Valores de entrada congelados</h3>
        <pre>{JSON.stringify(data.determinationInputs, null, 2)}</pre>
        <h3>Datos del evento</h3>
        <pre>{JSON.stringify(event.payload, null, 2)}</pre>
      </details>
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
