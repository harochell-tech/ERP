"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { ConfirmAction, ErrorBox, Field, Loading, Money, NoPermission, ReasonAction, StatusBadge, useFieldErrors } from "@/components/ui";
import { AUTHORIZATION_DOCUMENT_KINDS, authorizationActions, documentKindLabel } from "@/lib/authorizations";
import { formatQuantity } from "@/lib/decimal";
import { formatDate, formatDateTime } from "@/lib/labels";
import { sha256Hex } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Authorization = Schemas["FiscalAuthorizationDetail"];

// FIS1-05 (E-FIS1-05-4/5/6): a CONFOTUR authorization — scope with authorized, consumed and available (the server's), documents,
// the invoices that consume it and its history; actions by status and permission: Crédito / Facturación edit, attach and submit;
// the Especialista fiscal verifies (step-up, never the registrar), returns or rejects, suspends and reactivates (step-up).

function Actions({ authorization, onDone }: { authorization: Authorization; onDone: () => void }) {
  const { can, state } = useSession();
  const h = authorization.header;
  const target = { authorizationId: h.authorizationId, expectedVersion: h.version };
  const userId = state.status === "ready" ? state.session.userId : "";
  const actions = authorizationActions(h.status, can, userId === authorization.registeredBy);
  const name = `Autorización ${h.certificateNo}`;
  const submit = useCommand(`submit-authorization:${h.authorizationId}`, "/api/v1/companies/{companyId}/tax/submit-for-verification", `${name} enviada a verificación.`);
  const verify = useCommand(`verify-authorization:${h.authorizationId}`, "/api/v1/companies/{companyId}/tax/verify-authorization", `${name} verificada: ya permite facturar con e-CF 44.`);
  const giveBack = useCommand(`return-authorization:${h.authorizationId}`, "/api/v1/companies/{companyId}/tax/return-authorization-to-draft", `${name} devuelta a borrador.`);
  const reject = useCommand(`reject-authorization:${h.authorizationId}`, "/api/v1/companies/{companyId}/tax/reject-authorization", `${name} rechazada.`);
  const suspend = useCommand(`suspend-authorization:${h.authorizationId}`, "/api/v1/companies/{companyId}/tax/suspend-authorization", `${name} suspendida.`);
  const reactivate = useCommand(`reactivate-authorization:${h.authorizationId}`, "/api/v1/companies/{companyId}/tax/reactivate-authorization", `${name} reactivada.`);
  const busy = submit.busy || verify.busy || giveBack.busy || reject.busy || suspend.busy || reactivate.busy;
  const after = (response: unknown) => response && onDone();
  return (
    <>
      <div className="actions">
        {actions.includes("EDIT") ? (
          <Link className="button" href={`/fiscal/autorizaciones/nueva/?id=${h.authorizationId}`}>
            Editar
          </Link>
        ) : null}
        {actions.includes("SUBMIT") ? (
          <button type="button" className="primary" disabled={busy} onClick={async () => after(await submit.run(target))}>
            Enviar a verificación
          </button>
        ) : null}
        {actions.includes("VERIFY") ? (
          <ConfirmAction
            label="Verificar"
            className="primary"
            title={`¿Verificar la ${name.toLowerCase()}?`}
            consequence={`Confirma que el certificado coincide con lo registrado. La autorización queda activa y las facturas de ${h.customerName} de los productos cubiertos se emiten con e-CF 44, sin ITBIS, hasta agotar lo autorizado.`}
            stepUp
            busy={busy}
            onConfirm={async () => after(await verify.run(target))}
          />
        ) : null}
        {actions.includes("RETURN") ? (
          <ReasonAction
            label="Devolver a borrador"
            consequence="La autorización vuelve a borrador para que quien la registró la corrija y la envíe de nuevo."
            busy={busy}
            onConfirm={async (reason) => after(await giveBack.run({ ...target, reason }))}
          />
        ) : null}
        {actions.includes("REJECT") ? (
          <ReasonAction
            label="Rechazar"
            consequence="La autorización queda rechazada y no se podrá usar para facturar. No se deshace."
            busy={busy}
            onConfirm={async (reason) => after(await reject.run({ ...target, reason }))}
          />
        ) : null}
        {actions.includes("SUSPEND") ? (
          <ReasonAction
            label="Suspender"
            consequence="Mientras esté suspendida no se podrá facturar con esta autorización (e-CF 44); se puede reactivar después."
            stepUp
            busy={busy}
            onConfirm={async (reason) => after(await suspend.run({ ...target, reason }))}
          />
        ) : null}
        {actions.includes("REACTIVATE") ? (
          <ReasonAction
            label="Reactivar"
            consequence="La autorización vuelve a permitir facturar con e-CF 44 lo que tenga disponible."
            stepUp
            busy={busy}
            onConfirm={async (reason) => after(await reactivate.run({ ...target, reason }))}
          />
        ) : null}
      </div>
      {h.status === "PENDING_VERIFICATION" && can("fiscal_authorization:verify") && !actions.includes("VERIFY") ? (
        <p className="muted">Usted registró esta autorización: la verifica otra persona.</p>
      ) : null}
      <ErrorBox error={submit.error ?? verify.error ?? giveBack.error ?? reject.error ?? suspend.error ?? reactivate.error} />
    </>
  );
}

function AttachDocument({ authorizationId, onDone }: { authorizationId: string; onDone: () => void }) {
  const attach = useCommand(`attach-authorization-document:${authorizationId}`, "/api/v1/companies/{companyId}/tax/attach-authorization-document");
  const [form, setForm] = useState({ kind: "CERTIFICADO_DGII", evidenceRef: "", evidenceSha256: "" });
  const fe = useFieldErrors<"evidenceRef" | "evidenceSha256">();
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [key]: e.target.value });
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const valid = fe.check({
          evidenceRef: form.evidenceRef.trim() === "" && "Adjunte el archivo o escriba su referencia.",
          evidenceSha256: !/^[0-9a-fA-F]{64}$/.test(form.evidenceSha256.trim()) && "El SHA-256 tiene 64 caracteres hexadecimales (se calcula al adjuntar el archivo).",
        });
        if (!valid) {
          return;
        }
        const message = `Documento adjuntado: ${documentKindLabel(form.kind)} (${form.evidenceRef.trim()}).`;
        if (await attach.run({ authorizationId, kind: form.kind, evidenceRef: form.evidenceRef.trim(), evidenceSha256: form.evidenceSha256.trim().toLowerCase() }, undefined, message)) {
          setForm({ kind: form.kind, evidenceRef: "", evidenceSha256: "" });
          onDone();
        }
      }}
    >
      <Field label="Tipo de documento" required>
        <select aria-label="Tipo de documento" value={form.kind} onChange={set("kind")}>
          {Object.entries(AUTHORIZATION_DOCUMENT_KINDS).map(([kind, label]) => (
            <option key={kind} value={kind}>
              {label}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Archivo">
        <input
          type="file"
          aria-label="Archivo del documento"
          onChange={async (e) => {
            const file = e.target.files?.[0];
            if (file) {
              setForm({ ...form, evidenceRef: file.name, evidenceSha256: await sha256Hex(file) });
            }
          }}
        />
      </Field>
      <Field label="Referencia" required error={fe.errors.evidenceRef}>
        <input value={form.evidenceRef} onChange={set("evidenceRef")} />
      </Field>
      <Field label="SHA-256" required error={fe.errors.evidenceSha256} wide>
        <input className="mono" value={form.evidenceSha256} onChange={set("evidenceSha256")} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={attach.busy}>
          Adjuntar documento
        </button>
      </div>
      <ErrorBox error={attach.error} />
    </form>
  );
}

function AuthorizationDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/tax/fiscal-authorizations/{authorizationId}", { path: { companyId, authorizationId: id } }) : null,
    [companyId, id],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const h = data.header;
  const actions = authorizationActions(h.status, can, false);
  return (
    <>
      <p>
        <Link href="/fiscal/autorizaciones/">← Autorizaciones fiscales</Link>
      </p>
      <h1>
        Autorización {h.certificateNo} <StatusBadge status={h.status} testId="authorization-status" />
      </h1>
      <dl className="facts">
        <dt>Cliente</dt>
        <dd>
          {h.customerName} ({h.customerRnc})
        </dd>
        <dt>Régimen</dt>
        <dd>{h.regime}</dd>
        <dt>Proyecto</dt>
        <dd>{h.projectName}</dd>
        <dt>Resolución CONFOTUR</dt>
        <dd>{data.confoturResolutionNo}</dd>
        <dt>Emitido el</dt>
        <dd>{formatDate(h.issuedOn)}</dd>
        <dt>Vigente hasta</dt>
        <dd>{formatDate(h.validUntil)}</dd>
        <dt>Fin del plazo del proyecto</dt>
        <dd>{formatDate(data.projectTermEndsOn)}</dd>
        {data.salesOrderId ? (
          <>
            <dt>Pedido de origen</dt>
            <dd>
              <Link href={`/ventas/pedido/?id=${data.salesOrderId}`}>Ver pedido</Link>
            </dd>
          </>
        ) : null}
        <dt>Neto autorizado · consumido (RD$)</dt>
        <dd>
          <Money value={h.netAuthorized} /> · <Money value={h.netConsumed} testId="authorization-net-consumed" />
        </dd>
      </dl>
      <Actions authorization={data} onDone={reload} />

      <h2>Alcance</h2>
      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th className="num">#</th>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Autorizado</th>
            <th className="num">Consumido</th>
            <th className="num">Disponible</th>
            <th className="num">Neto autorizado (RD$)</th>
            <th className="num">Neto consumido (RD$)</th>
            <th className="num">Neto disponible (RD$)</th>
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => (
            <tr key={l.lineNo}>
              <td className="num">{l.lineNo}</td>
              <td>
                {l.itemCode} — {l.itemName}
              </td>
              <td>{l.uom}</td>
              <td className="num">{formatQuantity(l.qtyAuthorized)}</td>
              <td className="num">{formatQuantity(l.qtyConsumed)}</td>
              <td className="num">{formatQuantity(l.qtyAvailable)}</td>
              <td className="num">
                <Money value={l.netAuthorized} />
              </td>
              <td className="num">
                <Money value={l.netConsumed} />
              </td>
              <td className="num">
                <Money value={l.netAvailable} testId={`authorization-net-available:${l.lineNo}`} />
              </td>
            </tr>
          ))}
        </tbody>
      </table></div>

      <h2>Documentos</h2>
      {data.documents.length === 0 ? (
        <p className="muted">Sin documentos. El certificado de exención de la DGII es obligatorio para enviar a verificación.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Tipo</th>
              <th>Referencia</th>
              <th>SHA-256</th>
              <th>Adjuntado</th>
            </tr>
          </thead>
          <tbody>
            {data.documents.map((d) => (
              <tr key={d.documentId}>
                <td>{documentKindLabel(d.kind)}</td>
                <td className="wrap">{d.evidenceRef}</td>
                <td className="muted mono" title={d.evidenceSha256}>
                  {d.evidenceSha256.slice(0, 12)}…
                </td>
                <td>{formatDateTime(d.addedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
      {actions.includes("ATTACH") ? <AttachDocument authorizationId={h.authorizationId} onDone={reload} /> : null}

      <h2>Facturas que la consumen</h2>
      {data.consumptions.length === 0 ? (
        <p className="muted">Ninguna factura la ha consumido.</p>
      ) : (
        <div className="table-wrap"><table data-testid="authorization-consumptions">
          <thead>
            <tr>
              <th>Factura</th>
              <th className="num">Línea</th>
              <th className="num">Cantidad</th>
              <th className="num">Neto (RD$)</th>
              <th>Movimiento</th>
              <th>Fecha</th>
            </tr>
          </thead>
          <tbody>
            {data.consumptions.map((c) => (
              <tr key={c.consumptionId}>
                <td className="mono">
                  <Link href={`/facturacion/factura/?id=${c.invoiceId}`}>{c.invoiceNo}</Link>
                </td>
                <td className="num">{c.lineNo}</td>
                <td className="num">{formatQuantity(c.quantity)}</td>
                <td className="num">
                  <Money value={c.net} />
                </td>
                <td>{c.release ? "Devolución" : "Consumo"}</td>
                <td>{formatDateTime(c.at)}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
      <History history={data.history.map((x) => ({ statusKind: "DOCUMENT", command: "", by: null, ...x }))} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <AuthorizationDetail />
    </Suspense>
  );
}
