"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, Loading, Money, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatDate, lineStatusLabel, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Line = Schemas["BankStatementLineView"];
type Suggestion = Schemas["LineSuggestion"];
type PaymentSummary = Schemas["PaymentSummary"];

const BASIS: Readonly<Record<string, string>> = { PAYMENT_NO: "por número de pago", REFERENCE: "por referencia", AMOUNT_ONLY: "solo por monto" };

const ITEM_KINDS: Readonly<Record<string, string>> = {
  OUTSTANDING_PAYMENT: "Pago sin débito en el extracto",
  OUTSTANDING_RETURN: "Reversa sin devolución en el extracto",
  OUTSTANDING_CHARGE: "Cargo sin línea",
  UNRECORDED_DEBIT: "Débito del banco sin asiento",
  UNRECORDED_CREDIT: "Crédito del banco sin asiento",
};

// VS2-08: BANK-GL of the account at the date (read-only, E-VS2-07-5) and the statement's lines. A match is always confirmed by a
// person (E-VS2-05-6): the suggestion, or a payment picked by hand; a CREDIT line only as the return of a reversed payment
// (E-VS2-05-10). The Controller recognizes charges (R-10) and unmatches with a reason.
function LineActions({
  line,
  suggestion,
  released,
  reversed,
  onDone,
}: {
  line: Line;
  suggestion?: Suggestion;
  released: PaymentSummary[];
  reversed: PaymentSummary[];
  onDone: () => void;
}) {
  const { can } = useSession();
  const match = useCommand(`match-line:${line.lineId}`, "/api/v1/companies/{companyId}/treasury/match-bank-line");
  const unmatch = useCommand(`unmatch-line:${line.lineId}`, "/api/v1/companies/{companyId}/treasury/unmatch-bank-line");
  const charge = useCommand(`charge-line:${line.lineId}`, "/api/v1/companies/{companyId}/treasury/recognize-bank-charge");
  const [manual, setManual] = useState("");
  const busy = match.busy || unmatch.busy || charge.busy;
  const after = (response: unknown) => {
    if (response) {
      onDone();
    }
  };
  const candidates = line.direction === "DEBIT" ? released : reversed;
  const run = (paymentId: string, paymentVersion: number) =>
    match.run({ lineId: line.lineId, expectedLineVersion: line.version, paymentId, expectedPaymentVersion: paymentVersion });

  return (
    <div className="inline-form">
      {line.status === "UNMATCHED" && can("bank_line:match")
        ? (suggestion?.candidates ?? []).map((c) => (
            <button key={c.paymentId} type="button" className="primary" disabled={busy} onClick={async () => after(await run(c.paymentId, c.paymentVersion))}>
              Conciliar con {c.paymentNo} ({BASIS[c.basis] ?? c.basis})
            </button>
          ))
        : null}
      {line.status === "UNMATCHED" && can("bank_line:match") && candidates.length > 0 ? (
        <>
          <select aria-label={`Pago para la línea ${line.description}`} value={manual} onChange={(e) => setManual(e.target.value)}>
            <option value="">{line.direction === "DEBIT" ? "Elegir pago liberado…" : "Elegir pago revertido (devolución)…"}</option>
            {candidates.map((p) => (
              <option key={p.paymentId} value={p.paymentId}>
                {p.paymentNo} · {p.supplierName}
              </option>
            ))}
          </select>
          <button
            type="button"
            disabled={busy || manual === ""}
            onClick={async () => {
              const p = candidates.find((x) => x.paymentId === manual);
              if (p) {
                after(await run(p.paymentId, p.version));
              }
            }}
          >
            Conciliar
          </button>
        </>
      ) : null}
      {line.status === "UNMATCHED" && line.direction === "DEBIT" && can("bank_charge:recognize") ? (
        <button type="button" disabled={busy} onClick={async () => after(await charge.run({ lineId: line.lineId, expectedVersion: line.version }))}>
          Registrar como cargo
        </button>
      ) : null}
      {line.status === "MATCHED" && can("bank_line:unmatch") ? (
        <ReasonAction label="Desconciliar" busy={busy} onConfirm={async (reason) => after(await unmatch.run({ lineId: line.lineId, expectedVersion: line.version, reason }))} />
      ) : null}
      <ErrorBox error={match.error ?? unmatch.error ?? charge.error} />
    </div>
  );
}

const TABS: readonly { id: string; label: string; status: string | null }[] = [
  { id: "unmatched", label: "Sin conciliar", status: "UNMATCHED" },
  { id: "matched", label: "Conciliadas", status: "MATCHED" },
  { id: "charges", label: "Cargos registrados", status: "CHARGE_RECOGNIZED" },
  { id: "transit", label: "Partidas en tránsito", status: null },
];

function Reconciliation() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const params = useSearchParams();
  const account = params.get("cuenta") ?? "";
  const statementId = params.get("extracto") ?? "";
  const [asOf, setAsOf] = useState(todayInDominicanRepublic());
  const [tab, setTab] = useState("unmatched");
  const allowed = can("bank:read");

  const { data: banks } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/treasury/bank-accounts", { path: { companyId } }) : null, [companyId]);
  const bank = account || banks?.items[0]?.bankAccountId || "";
  const { data: statements } = useLoad(
    allowed && bank ? () => query("/api/v1/companies/{companyId}/treasury/bank-statements", { path: { companyId }, query: { bankAccountId: bank, limit: 200 } }) : null,
    [companyId, bank],
  );
  const recon = useLoad(
    allowed && bank ? () => query("/api/v1/companies/{companyId}/treasury/bank-accounts/{bankAccountId}/reconciliation", { path: { companyId, bankAccountId: bank }, query: { asOf } }) : null,
    [companyId, bank, asOf],
  );
  const lines = useLoad(
    allowed && bank
      ? () =>
          query("/api/v1/companies/{companyId}/treasury/bank-statement-lines", {
            path: { companyId },
            query: statementId ? { statementId, limit: 200 } : { bankAccountId: bank, limit: 200 },
          })
      : null,
    [companyId, bank, statementId],
  );
  const suggestions = useLoad(
    allowed && statementId ? () => query("/api/v1/companies/{companyId}/treasury/bank-statements/{statementId}/match-suggestions", { path: { companyId, statementId } }) : null,
    [companyId, statementId],
  );
  const payments = useLoad(
    can("payment:read") ? () => query("/api/v1/companies/{companyId}/treasury/payments", { path: { companyId }, query: { limit: 200 } }) : null,
    [companyId],
  );
  if (!allowed) {
    return <NoPermission />;
  }
  const reloadAll = () => {
    recon.reload();
    lines.reload();
    suggestions.reload();
    payments.reload();
  };
  const released = (payments.data?.items ?? []).filter((p) => p.status === "RELEASED" && p.bankAccountId === bank);
  const reversed = (payments.data?.items ?? []).filter((p) => p.status === "REVERSED" && p.bankAccountId === bank);
  const r = recon.data;
  const current = TABS.find((t) => t.id === tab) ?? TABS[0]!;
  const shown = (lines.data?.items ?? []).filter((l) => l.status === current.status);
  const items = r ? [...r.glItems, ...r.lineItems] : [];

  return (
    <>
      <h1>Conciliación bancaria</h1>
      <div className="card">
        <Field label="Cuenta bancaria">
          <select value={bank} onChange={(e) => router.push(`/tesoreria/conciliacion/?cuenta=${e.target.value}`)}>
            {(banks?.items ?? []).map((b) => (
              <option key={b.bankAccountId} value={b.bankAccountId}>
                {b.bankCode} {b.accountNumber}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Extracto">
          <select value={statementId} onChange={(e) => router.push(`/tesoreria/conciliacion/?cuenta=${bank}${e.target.value ? `&extracto=${e.target.value}` : ""}`)}>
            <option value="">Todas las líneas de la cuenta</option>
            {(statements?.items ?? []).map((s) => (
              <option key={s.statementId} value={s.statementId}>
                {formatDate(s.periodFrom)} – {formatDate(s.periodTo)} ({s.fileName})
              </option>
            ))}
          </select>
        </Field>
        <Field label="Al">
          <input type="date" value={asOf} onChange={(e) => setAsOf(e.target.value)} />
        </Field>
      </div>
      {r === null ? (
        <Loading error={recon.error} />
      ) : r.skipped ? (
        <p className="muted">La cuenta no tiene movimientos ni saldo a esa fecha.</p>
      ) : (
        <>
          <div className="cards">
            <div className="stat">
              <span>Saldo en libros</span>
              <span className="value">
                <Money value={r.glBalance} />
              </span>
            </div>
            <div className="stat">
              <span>Saldo del extracto</span>
              <span className="value">{r.statementBalance === null ? "Sin extracto" : <Money value={r.statementBalance} />}</span>
            </div>
            <div className="stat">
              <span>Partidas en tránsito</span>
              <span className="value">{items.length}</span>
            </div>
            <div className={`stat ${r.findings.some((f) => f.severity === "ERROR") ? "bad" : "good"}`}>
              <span>Diferencia (BANK-GL)</span>
              <span className="value" data-testid="bank-gl-difference">
                {r.difference === null ? "—" : <Money value={r.difference} />}
              </span>
            </div>
          </div>
          {r.findings.length > 0 ? (
            <ul>
              {r.findings.map((f) => (
                <li key={`${f.classification}:${f.matchKey}`}>
                  <StatusBadge status={f.severity === "ERROR" ? "FAILED" : "MATCH_EXCEPTION"} label={f.severity === "ERROR" ? "Error" : "Advertencia"} /> {f.classification}
                </li>
              ))}
            </ul>
          ) : null}
        </>
      )}
      <div className="tabs" role="tablist">
        {TABS.map((t) => (
          <button key={t.id} type="button" role="tab" aria-selected={t.id === tab} onClick={() => setTab(t.id)}>
            {t.label}
            {t.status ? ` (${(lines.data?.items ?? []).filter((l) => l.status === t.status).length})` : ` (${items.length})`}
          </button>
        ))}
      </div>
      {current.status === null ? (
        items.length === 0 ? (
          <p className="muted">No hay partidas en tránsito a esa fecha.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>Partida</th>
                <th>Referencia</th>
                <th>Fecha</th>
                <th className="num">Efecto</th>
              </tr>
            </thead>
            <tbody>
              {items.map((i) => (
                <tr key={`${i.kind}:${i.reference}:${i.date}`}>
                  <td>{ITEM_KINDS[i.kind] ?? i.kind}</td>
                  <td className="mono">{i.reference}</td>
                  <td>{formatDate(i.date)}</td>
                  <td className="num">
                    <Money value={i.amount} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )
      ) : lines.data === null ? (
        <Loading error={lines.error} />
      ) : shown.length === 0 ? (
        <p className="muted">No hay líneas en esta pestaña.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Fecha</th>
              <th>Descripción · referencia</th>
              <th className="num">Débito</th>
              <th className="num">Crédito</th>
              <th>Estado</th>
              <th>Acción</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((l) => (
              <tr key={l.lineId}>
                <td>{formatDate(l.valueDate)}</td>
                <td>
                  {l.description} {l.bankReference ? <span className="muted">· {l.bankReference}</span> : null}
                  {l.matchedPaymentNo ? <div className="mono muted">{l.matchedPaymentNo}</div> : null}
                </td>
                <td className="num">{l.direction === "DEBIT" ? <Money value={l.amount} /> : null}</td>
                <td className="num">{l.direction === "CREDIT" ? <Money value={l.amount} /> : null}</td>
                <td>
                  <StatusBadge status={l.status} label={lineStatusLabel(l.status)} />
                </td>
                <td>
                  <LineActions
                    line={l}
                    suggestion={suggestions.data?.lines.find((s) => s.lineId === l.lineId)}
                    released={released}
                    reversed={reversed}
                    onDone={reloadAll}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Reconciliation />
    </Suspense>
  );
}
