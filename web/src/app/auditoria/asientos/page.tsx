"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { Field, Loading, Money, NoPermission } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { eventTypeLabel, journalTypeLabel, ruleLabel } from "@/lib/explain";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { journalSearchText, journalsTitle, shortHash } from "@/lib/ux4a-auditoria";

/**
 * UX4-02 (A-07, E-UX4-15): without an event, a search of journals by document number (receipt, NCF, payment, adjustment, invoice,
 * receipt of payment, deposit, conduce, production run) or by an id; each hit opens the event's journals.
 */
function Search() {
  const { companyId } = useSession();
  const router = useRouter();
  const typed = useSearchParams().get("buscar") ?? "";
  const [text, setText] = useState(typed);
  const search = journalSearchText(typed);
  const { data, error } = useLoad(
    search ? () => query("/api/v1/companies/{companyId}/audit/journals", { path: { companyId }, query: { text: search, limit: 100 } }) : null,
    [companyId, search],
  );
  return (
    <>
      <h1>Buscar asientos</h1>
      <p className="muted">Escriba el número de un documento (recepción, NCF de la factura del proveedor, pago, ajuste, factura, recibo, depósito, conduce, corrida) o un identificador.</p>
      <form
        className="inline-form"
        onSubmit={(e) => {
          e.preventDefault();
          const next = journalSearchText(text);
          router.push(next ? `/auditoria/asientos/?buscar=${encodeURIComponent(next)}` : "/auditoria/asientos/");
        }}
      >
        <Field label="Documento o identificador">
          <input value={text} maxLength={100} onChange={(e) => setText(e.target.value)} placeholder="RM-2026-000001" />
        </Field>
        <div className="actions">
          <button type="submit" className="primary">
            Buscar
          </button>
        </div>
      </form>
      {search === null ? null : data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted" data-testid="journal-search-empty">
          No hay asientos para «{search}». Revise el número (por ejemplo RM-2026-000001, PAG-000001 o el NCF B0100000001).
        </p>
      ) : (
        <div className="table-wrap">
          <table data-testid="journal-search-results">
            <thead>
              <tr>
                <th>Documento</th>
                <th>Evento</th>
                <th>Regla</th>
                <th>Tipo</th>
                <th>Fecha contable</th>
                <th className="num">Débito total (RD$)</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {data.items.map((h) => (
                <tr key={h.journalId} data-testid="journal-hit">
                  <td className="mono">{h.documentNumber ?? "—"}</td>
                  <td className="wrap">{eventTypeLabel(h.eventType)}</td>
                  <td className="wrap">{ruleLabel(h.ruleCode)}</td>
                  <td>
                    {journalTypeLabel(h.journalType)}
                    {h.generation > 1 ? `, generación ${h.generation}` : ""}
                  </td>
                  <td>{formatDate(h.postingDate)}</td>
                  <td className="num">
                    <Money value={h.totalDebit} />
                  </td>
                  <td>
                    <Link href={`/auditoria/asientos/?evento=${h.sourceEventId}`}>Ver asientos</Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

/** The journals an event produced (E-11: the accounting evidence), each line linked to its explanation (EX-01). */
function Journals({ eventId }: { eventId: string }) {
  const { companyId, can } = useSession();
  const { data, error } = useLoad(
    () => query("/api/v1/companies/{companyId}/finance/events/{sourceEventId}/journals", { path: { companyId, sourceEventId: eventId } }),
    [companyId, eventId],
  );
  // UX4-02 (A-08): the document of the event (SearchJournals by the event id) names the page.
  const hits = useLoad(
    () => query("/api/v1/companies/{companyId}/audit/journals", { path: { companyId }, query: { text: eventId, limit: 50 } }),
    [companyId, eventId],
  );
  // UX2-02 (E-UX2-5): account roles by their names when the reader may see the configuration.
  const roles = useLoad(can("configuration:read") ? () => query("/api/v1/companies/{companyId}/finance/account-roles", { path: { companyId } }) : null, [companyId]);
  const roleName = (code: string) => roles.data?.items.find((r) => r.roleCode === code)?.name ?? code;

  if (data === null) {
    return <Loading error={error} />;
  }
  const own = hits.data?.items.find((h) => h.sourceEventId === eventId) ?? null;
  const journalIds = new Set(data.journals.map((j) => j.journalId));
  return (
    <>
      <p>
        <Link href="/auditoria/asientos/">← Buscar asientos</Link>
      </p>
      <h1 data-testid="journals-title">{journalsTitle(own?.aggregateType, own?.documentNumber)}</h1>
      <p className="muted" title={data.sourceEventId}>
        {own ? eventTypeLabel(own.eventType) : "Evento"} · <span className="mono">{shortHash(data.sourceEventId)}</span>
      </p>
      {data.journals.map((j) => (
        <section key={j.journalId} data-testid="journal" id={`asiento-${j.journalId}`}>
          <h2>{ruleLabel(j.ruleCode)}</h2>
          <dl className="facts">
            <dt>Versión de la regla</dt>
            <dd>{j.ruleVersion}</dd>
            <dt>Tipo de asiento</dt>
            <dd>{journalTypeLabel(j.journalType)}</dd>
            <dt>Generación</dt>
            <dd>{j.generation}</dd>
            <dt>Fecha contable</dt>
            <dd>
              {formatDate(j.postingDate)}
              {j.lateEntry ? " (registro tardío)" : ""}
            </dd>
            {j.reversesJournalId ? (
              <>
                <dt>Reversa del asiento</dt>
                <dd>
                  {journalIds.has(j.reversesJournalId) ? (
                    <a href={`#asiento-${j.reversesJournalId}`} title={j.reversesJournalId}>
                      Ver el asiento reversado
                    </a>
                  ) : (
                    <Link href={`/auditoria/asientos/?buscar=${j.reversesJournalId}`} title={j.reversesJournalId}>
                      Ver el asiento reversado
                    </Link>
                  )}
                </dd>
              </>
            ) : null}
          </dl>
          <div className="table-wrap"><table>
            <thead>
              <tr>
                <th>#</th>
                <th>Cuenta</th>
                <th>Rol</th>
                <th className="num">Débito (RD$)</th>
                <th className="num">Crédito (RD$)</th>
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
                  <td title={e.accountRole}>{roleName(e.accountRole)}</td>
                  <td className="num">{formatDecimal(e.debit)}</td>
                  <td className="num">{formatDecimal(e.credit)}</td>
                  <td>
                    <Link href={`/auditoria/explicar/?entrada=${e.glEntryId}`}>Explicar</Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table></div>
        </section>
      ))}
    </>
  );
}

function Page() {
  const { can } = useSession();
  const eventId = useSearchParams().get("evento") ?? "";
  if (!can("audit:read")) {
    return <NoPermission />;
  }
  return eventId ? <Journals eventId={eventId} /> : <Search />;
}

export default function JournalsPage() {
  return (
    <Suspense>
      <Page />
    </Suspense>
  );
}
