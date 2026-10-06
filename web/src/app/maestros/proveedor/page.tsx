"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, ReasonAction, StatusBadge, useFieldErrors } from "@/components/ui";
import { LoadingIndicator } from "@/components/StateNotices";
import { formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { emailsProblem, parseEmails } from "@/lib/partyImport";
import { useLoad } from "@/lib/useQuery";
import { allSuppliers } from "@/lib/paging";

type Version = Schemas["PartyBankAccountView"];

/** E-VS2-01-4: who confirmed, at which number, when — at least 20 characters. */
const EVIDENCE_MIN = 20;

// VS2-08 / E-UI-2: the supplier's record with its bank-account versions (E-VS2-01-3/4/8). The treasurer requests a new version
// (step-up); someone else — the Controller — verifies it with evidence (step-up) or rejects it; the new account is payable 72 h
// after verification. Numbers arrive masked unless the reader may see them (E-VS2-07-3).
function RequestForm({ partyId, foreign, onDone }: { partyId: string; foreign: boolean; onDone: () => void }) {
  const request = useCommand(`request-party-bank:${partyId}`, "/api/v1/companies/{companyId}/master-data/request-party-bank-account");
  const [bankCode, setBankCode] = useState("");
  const [accountNumber, setAccountNumber] = useState("");
  const [accountHolder, setAccountHolder] = useState("");
  const fe = useFieldErrors<"bankCode" | "accountNumber" | "accountHolder">();
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (
          !fe.check({
            bankCode: bankCode.trim() === "" && "Indique el código del banco.",
            accountNumber: accountNumber.trim() === "" && "Indique el número de cuenta.",
            accountHolder: accountHolder.trim() === "" && "Indique el titular de la cuenta.",
          })
        ) {
          return;
        }
        if (
          await request.run(
            { partyId, bankCode: bankCode.trim(), accountNumber: accountNumber.trim(), accountHolder: accountHolder.trim() },
            undefined,
            `Cuenta ${bankCode.trim()} solicitada: queda en revisión hasta que otra persona la verifique.`,
          )
        ) {
          setBankCode("");
          setAccountNumber("");
          setAccountHolder("");
          onDone();
        }
      }}
    >
      <h2>Solicitar cuenta nueva</h2>
      <Field label={foreign ? "Banco (SWIFT/BIC o nombre)" : "Banco (código)"} required error={fe.errors.bankCode}>
        <input value={bankCode} onChange={(e) => setBankCode(e.target.value)} />
      </Field>
      {/* E-USD1-05-6: a foreign supplier's account may be an IBAN, with letters. */}
      <Field label={foreign ? "Número de cuenta o IBAN" : "Número de cuenta"} required error={fe.errors.accountNumber}>
        <input className="mono" inputMode={foreign ? "text" : "numeric"} value={accountNumber} onChange={(e) => setAccountNumber(e.target.value)} />
      </Field>
      <Field label="Titular" required error={fe.errors.accountHolder}>
        <input value={accountHolder} onChange={(e) => setAccountHolder(e.target.value)} />
      </Field>
      <div className="actions form-actions">
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
  const account = `${version.bankCode} ${version.accountNumber}`;
  const verify = useCommand(`verify-party-bank:${id}`, "/api/v1/companies/{companyId}/master-data/verify-party-bank-account", `Cuenta ${account} verificada: se podrá pagar 72 horas después.`);
  const reject = useCommand(`reject-party-bank:${id}`, "/api/v1/companies/{companyId}/master-data/reject-party-bank-account", `Cuenta ${account} rechazada.`);
  const [evidence, setEvidence] = useState("");
  const busy = verify.busy || reject.busy;
  return (
    <div className="card">
      <h2>Verificar versión {version.version}</h2>
      <p className="muted">Solicitada por {version.requestedBy ?? "—"}. Al verificarla, la versión vigente queda reemplazada y la nueva se puede pagar 72 horas después.</p>
      <Field label="Evidencia de la verificación" required wide hint={`Mínimo ${EVIDENCE_MIN} caracteres: quién confirmó, a qué número se llamó y cuándo.`}>
        <textarea rows={3} value={evidence} onChange={(e) => setEvidence(e.target.value)} placeholder="Quién confirmó, a qué número se llamó y cuándo" />
      </Field>
      <div className="actions form-actions">
        <ConfirmAction
          label="Verificar cuenta"
          className="primary"
          stepUp
          busy={busy}
          disabled={evidence.trim().length < EVIDENCE_MIN}
          consequence={`La cuenta ${account} pasa a ser la cuenta de pago del proveedor (la vigente queda reemplazada) y se podrá pagar 72 horas después. No se puede deshacer.`}
          onConfirm={async () => {
            if (await verify.run({ partyBankAccountId: id, evidence: evidence.trim() })) {
              onDone();
            }
          }}
        />
        <ReasonAction
          label="Rechazar"
          consequence={`La solicitud de la cuenta ${account} quedará rechazada; habrá que solicitar otra.`}
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

function BankAccounts({ partyId, foreign }: { partyId: string; foreign: boolean }) {
  const { companyId, can, isMine } = useSession();
  const { data, error, reload } = useLoad(
    () => query("/api/v1/companies/{companyId}/treasury/suppliers/{partyId}/bank-accounts", { path: { companyId, partyId } }),
    [companyId, partyId],
  );
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  // UX1-01a: requestedBy is the display name (the e-mail until the first sign-in brings one); either identifies the requester.
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
      {review && can("party_bank_account:verify") && !isMine(review.requestedBy) ? <VerifyForm version={review} onDone={reload} /> : null}
      {!review && can("party_bank_account:request") ? <RequestForm partyId={partyId} foreign={foreign} onDone={reload} /> : null}
      <div className="table-wrap"><table>
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
              <td className="wrap">{v.accountHolder}</td>
              <td>
                <StatusBadge status={v.status} />
              </td>
              <td className="wrap">
                {v.requestedBy ?? "—"} <span className="muted">{formatDateTime(v.requestedAt)}</span>
              </td>
              <td className="wrap">
                {v.verifiedBy ?? v.rejectedBy ?? "—"} <span className="muted">{v.verificationEvidence ?? v.rejectionReason ?? ""}</span>
              </td>
            </tr>
          ))}
        </tbody>
      </table></div>
    </>
  );
}

// IMP-02 (E-IMP-6, E-IMP-01-2): the supplier's phone and e-mails; the first e-mail is the principal one.
function Contact({ supplier, onDone }: { supplier: Schemas["SupplierView"]; onDone: () => void }) {
  const { can } = useSession();
  const save = useCommand(`supplier-contact:${supplier.supplierId}`, "/api/v1/companies/{companyId}/master-data/set-supplier-contact", `Contacto de ${supplier.legalName} guardado.`);
  const [editing, setEditing] = useState(false);
  const [phone, setPhone] = useState(supplier.phone ?? "");
  const [emails, setEmails] = useState(supplier.emails.join("\n"));
  const fe = useFieldErrors<"phone" | "emails">();
  if (!editing) {
    return (
      <div className="card" data-testid="supplier-contact">
        <strong>Teléfono: </strong>
        {supplier.phone ?? <span className="muted">sin registrar</span>} · <strong>Correos: </strong>
        {supplier.emails.length > 0 ? supplier.emails.join(", ") : <span className="muted">sin registrar</span>}{" "}
        {can("supplier:update") ? (
          <button type="button" onClick={() => setEditing(true)}>
            Editar contacto
          </button>
        ) : null}
      </div>
    );
  }
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const list = parseEmails(emails);
        if (!fe.check({ phone: phone.trim().length > 30 && "El teléfono tiene hasta 30 caracteres.", emails: emailsProblem(list) })) {
          return;
        }
        if (await save.run({ partyId: supplier.supplierId, expectedVersion: supplier.version, phone: phone.trim() === "" ? null : phone.trim(), emails: list })) {
          setEditing(false);
          onDone();
        }
      }}
    >
      <Field label="Teléfono" error={fe.errors.phone}>
        <input value={phone} onChange={(e) => setPhone(e.target.value)} />
      </Field>
      <Field label="Correos" wide error={fe.errors.emails} hint="Uno por línea, hasta diez. El primero es el principal.">
        <textarea rows={4} value={emails} onChange={(e) => setEmails(e.target.value)} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={save.busy}>
          Guardar contacto
        </button>
        <button type="button" onClick={() => setEditing(false)}>
          Cancelar
        </button>
      </div>
      <ErrorBox error={save.error} />
    </form>
  );
}

function Supplier() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("master_data:read") ? () => allSuppliers(companyId) : null,
    [companyId],
  );
  if (!can("master_data:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
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
          <dt>Saldo por pagar</dt>
          <dd>
            <Money value={supplier.openApAmount} currency />
          </dd>
        </div>
        <div>
          <dt>Plazo de pago</dt>
          <dd data-testid="supplier-terms">
            {supplier.paymentTermsDays === null ? <span className="muted">Sin plazo registrado</span> : supplier.paymentTermsDays === 0 ? "Al contado" : `${supplier.paymentTermsDays} días`}
          </dd>
        </div>
        <div>
          <dt>Cuenta bancaria</dt>
          <dd>
            <StatusBadge status={supplier.bankAccountState} />
          </dd>
        </div>
      </dl>
      <Contact key={supplier.version} supplier={supplier} onDone={reload} />
      {/* UX4-03 (C-34): where this supplier's documents are. */}
      <div className="actions" data-testid="supplier-links">
        {can("purchase_order:read") ? <Link href={`/compras/ordenes/?proveedor=${supplier.supplierId}`}>Órdenes de compra</Link> : null}
        {can("supplier_invoice:read") ? <Link href={`/cxp/facturas/?proveedor=${supplier.supplierId}`}>Facturas del proveedor</Link> : null}
        {can("payment:read") ? <Link href="/cxp/antiguedad/">Cuentas por pagar por antigüedad</Link> : null}
      </div>
      <h2>Cuentas bancarias</h2>
      {can("bank:read") ? (
        <BankAccounts partyId={supplier.supplierId} foreign={supplier.partyKind === "FOREIGN"} />
      ) : (
        <p className="muted" data-testid="bank-hidden">
          Por seguridad, los números de cuenta del proveedor solo los ven Tesorería, Contabilidad y Auditoría. Arriba puede ver si tiene una cuenta verificada para
          pagarle.
        </p>
      )}
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
