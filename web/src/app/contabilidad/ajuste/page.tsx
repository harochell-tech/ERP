"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { ConfirmAction, ErrorBox, Loading, Money, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatDecimal } from "@/lib/decimal";
import { COMPONENTS, formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Journal = Schemas["ManualJournalDetail"];

// FIN1-04 (E-FIN1-04-4, E-FIN1-04-6): an adjustment and what can be done with it now. Totals and difference are the server's;
// the preparer never sees "Aprobar" (four eyes, GL-03).
function Actions({ journal, onDone }: { journal: Journal; onDone: () => void }) {
  const { can, state } = useSession();
  const id = journal.manualJournalId;
  const target = { manualJournalId: id, expectedVersion: journal.version };
  const submit = useCommand(`submit-manual-journal:${id}`, "/api/v1/companies/{companyId}/finance/submit-manual-journal", `Ajuste ${journal.journalNo} enviado a aprobación.`);
  const withdraw = useCommand(`withdraw-manual-journal:${id}`, "/api/v1/companies/{companyId}/finance/withdraw-manual-journal", `Ajuste ${journal.journalNo} retirado para corregir.`);
  const approve = useCommand(`approve-manual-journal:${id}`, "/api/v1/companies/{companyId}/finance/approve-manual-journal", `Ajuste ${journal.journalNo} aprobado y contabilizado.`);
  const reject = useCommand(`reject-manual-journal:${id}`, "/api/v1/companies/{companyId}/finance/reject-manual-journal", `Ajuste ${journal.journalNo} rechazado.`);
  const reverse = useCommand(`reverse-manual-journal:${id}`, "/api/v1/companies/{companyId}/finance/reverse-manual-journal", `Ajuste ${journal.journalNo} reversado.`);
  const busy = submit.busy || withdraw.busy || approve.busy || reject.busy || reverse.busy;
  // UX1-01b: the API returns the preparer's display name (its e-mail until the first sign-in brings a name, E-UX1-01-3).
  const me = state.status === "ready" ? [state.session.email, state.session.displayName?.trim()].filter((v): v is string => !!v) : [];
  const preparedByMe = journal.preparedBy !== null && me.includes(journal.preparedBy);
  const balanced = !/[1-9]/.test(journal.difference);
  const after = (response: unknown) => {
    if (response) {
      onDone();
    }
  };

  return (
    <>
      <div className="actions">
        {journal.status === "DRAFT" && can("manual_journal:prepare") ? (
          <>
            <Link className="button" href={`/contabilidad/ajustes/nuevo/?id=${id}`}>
              Modificar
            </Link>
            <button type="button" className="primary" disabled={busy || !balanced} onClick={async () => after(await submit.run(target))}>
              Enviar a aprobación
            </button>
            {!balanced ? <span className="muted">Débitos y créditos deben ser iguales para enviarlo.</span> : null}
          </>
        ) : null}
        {journal.status === "PENDING_APPROVAL" && can("manual_journal:prepare") ? (
          <button type="button" disabled={busy} onClick={async () => after(await withdraw.run(target))}>
            Retirar para corregir
          </button>
        ) : null}
        {journal.status === "PENDING_APPROVAL" && can("manual_journal:approve") && !preparedByMe ? (
          <>
            <ConfirmAction
              label="Aprobar y contabilizar"
              title={`¿Aprobar y contabilizar el ajuste ${journal.journalNo}?`}
              className="primary"
              stepUp
              busy={busy}
              consequence={`Se contabiliza el asiento del ajuste ${journal.journalNo} por ${formatDecimal(journal.totalDebit)} con fecha ${formatDate(journal.postingDate)}. Después solo se corrige con una reversa.`}
              onConfirm={async () => after(await approve.run(target))}
            />
            <ReasonAction
              label="Rechazar"
              consequence={`El ajuste ${journal.journalNo} queda rechazado y no se contabiliza; quien lo preparó verá el motivo.`}
              busy={busy}
              onConfirm={async (reason) => after(await reject.run({ ...target, reason }))}
            />
          </>
        ) : null}
        {journal.status === "PENDING_APPROVAL" && can("manual_journal:approve") && preparedByMe ? (
          <span className="muted">Lo aprueba alguien distinto de quien lo preparó.</span>
        ) : null}
        {journal.status === "POSTED" && !journal.autoReverse && can("manual_journal:approve") ? (
          <ReasonAction
            label="Reversar ajuste"
            stepUp
            consequence={`Se contabiliza un asiento inverso al del ajuste ${journal.journalNo}; el ajuste queda reversado y no se puede reactivar.`}
            busy={busy}
            onConfirm={async (reason) => after(await reverse.run({ ...target, reason }))}
          />
        ) : null}
        {journal.postingEventId && can("audit:read") ? (
          <Link className="button" href={`/auditoria/asientos/?evento=${journal.postingEventId}`}>
            Ver asientos
          </Link>
        ) : null}
      </div>
      <ErrorBox error={submit.error ?? withdraw.error ?? approve.error ?? reject.error ?? reverse.error} />
    </>
  );
}

function AdjustmentDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data: journal, error, reload } = useLoad(
    can("ledger:read") && id ? () => query("/api/v1/companies/{companyId}/finance/manual-journals/{manualJournalId}", { path: { companyId, manualJournalId: id } }) : null,
    [companyId, id],
  );
  if (!can("ledger:read")) {
    return <NoPermission />;
  }
  if (journal === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <div className="actions">
        <h1 className="mono" style={{ margin: 0 }}>
          {journal.journalNo}
        </h1>
        <StatusBadge status={journal.status} testId="journal-status" />
      </div>
      <p>{journal.description}</p>
      <dl className="summary">
        <div>
          <dt>Fecha contable</dt>
          <dd>{formatDate(journal.postingDate)}</dd>
        </div>
        <div>
          <dt>Componente de cierre</dt>
          <dd>{COMPONENTS[journal.closeComponent] ?? journal.closeComponent}</dd>
        </div>
        <div>
          <dt>Reversa automática</dt>
          <dd>{journal.autoReverse ? "Sí, el día 1 del mes siguiente" : "No"}</dd>
        </div>
        <div>
          <dt>Soporte</dt>
          <dd>
            {journal.supportRef}
            <div className="muted mono" style={{ fontSize: 11, wordBreak: "break-all" }}>
              {journal.supportSha256}
            </div>
          </dd>
        </div>
        <div>
          <dt>Preparó</dt>
          <dd>{journal.preparedBy ?? "—"}</dd>
        </div>
        <div>
          <dt>Aprobó</dt>
          <dd>{journal.approvedBy ?? "—"}</dd>
        </div>
        {journal.rejectionReason ? (
          <div>
            <dt>Motivo del rechazo</dt>
            <dd>
              {journal.rejectionReason} ({journal.rejectedBy ?? "—"})
            </dd>
          </div>
        ) : null}
      </dl>
      <Actions journal={journal} onDone={reload} />
      <h2>Líneas</h2>
      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th>#</th>
            <th>Cuenta</th>
            <th>Memo</th>
            <th className="num">Débito (RD$)</th>
            <th className="num">Crédito (RD$)</th>
          </tr>
        </thead>
        <tbody>
          {journal.lines.map((l) => (
            <tr key={l.lineNo}>
              <td>{l.lineNo}</td>
              <td>
                <span className="mono">{l.accountCode}</span> {l.accountName}
              </td>
              <td className="wrap">{l.memo ?? ""}</td>
              <td className="num">{/[1-9]/.test(l.debit) ? <Money value={l.debit} /> : null}</td>
              <td className="num">{/[1-9]/.test(l.credit) ? <Money value={l.credit} /> : null}</td>
            </tr>
          ))}
          <tr>
            <td />
            <td>
              <strong>Totales</strong>
            </td>
            <td className="muted">Diferencia: <Money value={journal.difference} testId="journal-difference" /></td>
            <td className="num">
              <Money value={journal.totalDebit} testId="journal-debit" />
            </td>
            <td className="num">
              <Money value={journal.totalCredit} testId="journal-credit" />
            </td>
          </tr>
        </tbody>
      </table></div>
      <History history={journal.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <AdjustmentDetail />
    </Suspense>
  );
}
