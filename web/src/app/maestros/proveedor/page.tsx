"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, Loading, Money, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Version = Schemas["PartyBankAccountView"];

/** E-VS2-01-4: who confirmed, at which number, when — at least 20 characters. */
const EVIDENCE_MIN = 20;

// VS2-08 / E-UI-2: the supplier's record with its bank-account versions (E-VS2-01-3/4/8). The treasurer requests a new version
// (step-up); someone else — the Controller — verifies it with evidence (step-up) or rejects it; the new account is payable 72 h
// after verification. Numbers arrive masked unless the reader may see them (E-VS2-07-3).
function RequestForm({ partyId, onDone }: { partyId: string; onDone: () => void }) {
  const request = useCommand(`request-party-bank:${partyId}`, "/api/v1/companies/{companyId}/master-data/request-party-bank-account");
  const [bankCode, setBankCode] = useState("");
  const [accountNumber, setAccountNumber] = useState("");
  const [accountHolder, setAccountHolder] = useState("");
  return (
    <form
      className="card"
      onSubmit={async (e) => {
        e.preventDefault();
        if (await request.run({ partyId, bankCode: bankCode.trim(), accountNumber: accountNumber.trim(), accountHolder: accountHolder.trim() })) {
          setBankCode("");
          setAccountNumber("");
          setAccountHolder("");
          onDone();
        }
      }}
    >
      <h2 style={{ marginTop: 0 }}>Solicitar cuenta nueva</h2>
      <Field label="Banco (código)">
        <input value={bankCode} onChange={(e) => setBankCode(e.target.value)} required />
      </Field>
      <Field label="Número de cuenta">
        <input className="mono" value={accountNumber} onChange={(e) => setAccountNumber(e.target.value)} required />
      </Field>
      <Field label="Titular">
        <input value={accountHolder} onChange={(e) => setAccountHolder(e.target.value)} required />
      </Field>
      <div className="actions">
        <button type="submit" className="primary" disabled={request.busy}>
          Solicitar
        </button>
      </div>
      <ErrorBox error={request.error} />
    </form>
  );
}

function VerifyForm({ version, onDone }: { version: Version; onDone: () => void }) {
  const id = version.partyBankAccountId;
  const verify = useCommand(`verify-party-bank:${id}`, "/api/v1/companies/{companyId}/master-data/verify-party-bank-account");
  const reject = useCommand(`reject-party-bank:${id}`, "/api/v1/companies/{companyId}/master-data/reject-party-bank-account");
  const [evidence, setEvidence] = useState("");
  const busy = verify.busy || reject.busy;
  return (
    <div className="card">
      <h2 style={{ marginTop: 0 }}>Verificar versión {version.version}</h2>
      <p className="muted">Solicitada por {version.requestedBy ?? "—"}. Al verificarla, la versión vigente queda reemplazada y la nueva se puede pagar 72 horas después.</p>
      <Field label={`Evidencia de la verificación (mínimo ${EVIDENCE_MIN} caracteres)`}>
        <textarea rows={3} style={{ width: 420 }} value={evidence} onChange={(e) => setEvidence(e.target.value)} placeholder="Quién confirmó, a qué número se llamó y cuándo" />
      </Field>
      <div className="actions">
        <button
          type="button"
          className="primary"
          disabled={busy || evidence.trim().length < EVIDENCE_MIN}
          onClick={async () => {
            if (await verify.run({ partyBankAccountId: id, evidence: evidence.trim() })) {
              onDone();
            }
          }}
        >
          Verificar cuenta
        </button>
        <ReasonAction
          label="Rechazar"
          busy={busy}
          onConfirm={async (reason) => {
            if (await reject.run({ partyBankAccountId: id, reason })) {
              onDone();
            }
          }}
        />
      </div>
      <ErrorBox error={verify.error ?? reject.error} />
    </div>
  );
}

function BankAccounts({ partyId }: { partyId: string }) {
  const { companyId, can, state } = useSession();
  const { data, error, reload } = useLoad(
    () => query("/api/v1/companies/{companyId}/treasury/suppliers/{partyId}/bank-accounts", { path: { companyId, partyId } }),
    [companyId, partyId],
  );
  if (data === null) {
    return <Loading error={error} />;
  }
  const email = state.status === "ready" ? state.session.email : null;
  const current = data.items.find((v) => v.status === "VERIFIED");
  const review = data.items.find((v) => v.status === "REVIEW");
  return (
    <>
      {current ? (
        <div className="card">
          <strong>Cuenta vigente para pagos: </strong>
          <span className="mono">
            {current.bankCode} {current.accountNumber}
          </span>{" "}
          <StatusBadge status={current.status} /> <span className="muted">· pagable desde {formatDateTime(current.payableFrom)}</span>
        </div>
      ) : (
        <p className="notice">El proveedor no tiene una cuenta verificada: no se le puede pagar.</p>
      )}
      {review && can("party_bank_account:verify") && review.requestedBy !== email ? <VerifyForm version={review} onDone={reload} /> : null}
      {!review && can("party_bank_account:request") ? <RequestForm partyId={partyId} onDone={reload} /> : null}
      <table>
        <thead>
          <tr>
            <th>Versión</th>
            <th>Banco y cuenta</th>
            <th>Titular</th>
            <th>Estado</th>
            <th>Solicitó</th>
            <th>Verificó o rechazó</th>
          </tr>
        </thead>
        <tbody>
          {data.items.map((v) => (
            <tr key={v.partyBankAccountId}>
              <td>{v.version}</td>
              <td className="mono">
                {v.bankCode} {v.accountNumber}
              </td>
              <td>{v.accountHolder}</td>
              <td>
                <StatusBadge status={v.status} />
              </td>
              <td>
                {v.requestedBy ?? "—"} <span className="muted">{formatDateTime(v.requestedAt)}</span>
              </td>
              <td>
                {v.verifiedBy ?? v.rejectedBy ?? "—"} <span className="muted">{v.verificationEvidence ?? v.rejectionReason ?? ""}</span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

function Supplier() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error } = useLoad(
    can("master_data:read") ? () => query("/api/v1/companies/{companyId}/master-data/suppliers", { path: { companyId }, query: { limit: 200 } }) : null,
    [companyId],
  );
  if (!can("master_data:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const supplier = data.items.find((s) => s.supplierId === id);
  if (!supplier) {
    return <p className="muted">El proveedor no existe.</p>;
  }
  return (
    <>
      <p className="muted">
        <Link href="/maestros/proveedores/">Proveedores</Link> › {supplier.legalName}
      </p>
      <div className="actions">
        <h1 style={{ margin: 0 }}>{supplier.legalName}</h1>
        <StatusBadge status={supplier.status} />
      </div>
      <dl className="summary">
        <div>
          <dt>RNC</dt>
          <dd className="mono">{supplier.rnc ?? "—"}</dd>
        </div>
        <div>
          <dt>CxP abierta</dt>
          <dd>
            <Money value={supplier.openApAmount} />
          </dd>
        </div>
        <div>
          <dt>Cuenta bancaria</dt>
          <dd>
            <StatusBadge status={supplier.bankAccountState} />
          </dd>
        </div>
      </dl>
      <h2>Cuentas bancarias</h2>
      {can("bank:read") ? <BankAccounts partyId={supplier.supplierId} /> : <p className="muted">Su rol no ve las cuentas bancarias.</p>}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Supplier />
    </Suspense>
  );
}
