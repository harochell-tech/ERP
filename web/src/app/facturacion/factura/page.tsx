"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { CopyField, RecordEcfForm } from "@/components/Ecf";
import { History } from "@/components/History";
import { AccountingStatus, ErrorBox, Field, Loading, Money, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { invoiceEncfPrefix } from "@/lib/authorizations";
import { formatQuantity, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime, statusLabel, todayInDominicanRepublic } from "@/lib/labels";
import { sha256Hex } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Invoice = Schemas["InvoiceDetail"];

const EXEMPT_ECF_TYPE = "44";

// VS3-10b (E-VS3-10-6/7): an invoice — issue (step-up), the fiscal package for the provider's portal, the e-CF record, void while
// never fiscalized (Controller), credit notes per line, and the customer's withholdings (recorded by Cobros, reversed by the
// Controller).

function Issue({ invoice, onDone }: { invoice: Invoice; onDone: () => void }) {
  const issue = useCommand(`issue-invoice:${invoice.header.invoiceId}`, "/api/v1/companies/{companyId}/sales/issue-invoice");
  const [ecfType, setEcfType] = useState("");
  // FIS1-05 (E-FIS1-05-9): an invoice under a fiscal authorization is always an e-CF 44; there is nothing to choose.
  const exempt = invoice.header.ecfType === EXEMPT_ECF_TYPE;
  return (
    <div className="inline-form">
      {exempt ? (
        <p data-testid="invoice-exemption">Exenta — CONFOTUR (e-CF 44, sin ITBIS). El certificado se valida al emitir.</p>
      ) : (
        <Field label="Tipo de e-CF">
          <select value={ecfType} onChange={(e) => setEcfType(e.target.value)}>
            <option value="">Automático (31 con RNC, 32 con cédula)</option>
            <option value="31">31 · Crédito fiscal</option>
            <option value="32">32 · Consumo</option>
          </select>
        </Field>
      )}
      <button
        type="button"
        className="primary"
        disabled={issue.busy}
        onClick={async () =>
          (await issue.run({ invoiceId: invoice.header.invoiceId, expectedVersion: invoice.header.version, ecfType: exempt || ecfType === "" ? null : ecfType })) && onDone()
        }
      >
        Emitir factura
      </button>
      <ErrorBox error={issue.error} />
    </div>
  );
}

/** FIS1-05 (E-FIS1-05-9): an issued e-CF 44 names its exemption (from the fiscal package, which exists once issued). */
function Exemption({ invoiceId }: { invoiceId: string }) {
  const { companyId } = useSession();
  const { data } = useLoad(() => query("/api/v1/companies/{companyId}/sales/invoices/{invoiceId}/fiscal-package", { path: { companyId, invoiceId } }), [companyId, invoiceId]);
  if (!data?.exemption) {
    return null;
  }
  return (
    <p data-testid="invoice-exemption">
      Exenta — {data.exemption.regime}, certificado <span className="mono">{data.exemption.certificateNo}</span> · proyecto {data.exemption.projectName}
    </p>
  );
}

function FiscalPackage({ invoiceId }: { invoiceId: string }) {
  const { companyId } = useSession();
  const { data, error } = useLoad(() => query("/api/v1/companies/{companyId}/sales/invoices/{invoiceId}/fiscal-package", { path: { companyId, invoiceId } }), [companyId, invoiceId]);
  if (data === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <h2>Paquete fiscal para el portal</h2>
      <table>
        <tbody>
          <CopyField label="Tipo de e-CF" value={data.ecfType} />
          <CopyField label="RNC del emisor" value={data.issuerRnc} />
          <CopyField label="RNC del receptor" value={data.receiverRnc} />
          <CopyField label="Receptor" value={data.receiverName} />
          <CopyField label="Fecha" value={data.invoiceDate} />
          <CopyField label="Vence" value={data.dueDate} />
          <CopyField label="Neto" value={data.netTotal} />
          <CopyField label="ITBIS" value={data.taxTotal} />
          <CopyField label="Total" value={data.total} />
          {data.exemption ? (
            <>
              <CopyField label="Régimen de exención" value={data.exemption.regime} />
              <CopyField label="Certificado de exención" value={data.exemption.certificateNo} />
              <CopyField label="Proyecto" value={data.exemption.projectName} />
              <CopyField label="Indicador de facturación" value={data.exemption.billingIndicator} />
            </>
          ) : null}
        </tbody>
      </table>
    </>
  );
}

function CreditNoteForm({ invoice, onDone }: { invoice: Invoice; onDone: () => void }) {
  const create = useCommand(`create-credit-note:${invoice.header.invoiceId}`, "/api/v1/companies/{companyId}/sales/create-credit-note");
  const [nets, setNets] = useState<Record<string, string>>({});
  const [category, setCategory] = useState("DESCUENTO");
  const [reason, setReason] = useState("");
  const [invalid, setInvalid] = useState<string | null>(null);
  return (
    <>
      <h3>Nueva nota de crédito</h3>
      <table>
        <thead>
          <tr>
            <th className="num">Línea</th>
            <th>Producto</th>
            <th className="num">Neto</th>
            <th className="num">Ya acreditado</th>
            <th className="num">Queda</th>
            <th className="num">Acreditar</th>
          </tr>
        </thead>
        <tbody>
          {invoice.creditable.map((l) => (
            <tr key={l.invoiceLineId}>
              <td className="num">{l.lineNo}</td>
              <td>{l.itemCode}</td>
              <td className="num">
                <Money value={l.netAmount} />
              </td>
              <td className="num">
                <Money value={l.creditedNet} />
              </td>
              <td className="num">
                <Money value={l.remainingNet} />
              </td>
              <td className="num">
                <input aria-label={`Acreditar línea ${l.lineNo}`} inputMode="decimal" value={nets[l.invoiceLineId] ?? ""} onChange={(e) => setNets({ ...nets, [l.invoiceLineId]: e.target.value })} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="inline-form">
        <Field label="Motivo">
          <select value={category} onChange={(e) => setCategory(e.target.value)}>
            <option value="DESCUENTO">Descuento</option>
            <option value="ERROR_DE_PRECIO">Error de precio</option>
            <option value="OTRO">Otro</option>
          </select>
        </Field>
        <Field label="Explicación">
          <input value={reason} onChange={(e) => setReason(e.target.value)} />
        </Field>
        <button
          type="button"
          disabled={create.busy}
          onClick={async () => {
            const lines = Object.entries(nets)
              .map(([invoiceLineId, net]) => ({ invoiceLineId, netAmount: normalizeInput(net) }))
              .filter((l) => l.netAmount !== "");
            if (lines.length === 0 || lines.some((l) => !isPositiveDecimal(l.netAmount, 2)) || reason.trim() === "") {
              setInvalid("Indique al menos un monto a acreditar (hasta 2 decimales) y la explicación.");
              return;
            }
            setInvalid(null);
            if (await create.run({ invoiceId: invoice.header.invoiceId, reasonCategory: category, reason: reason.trim(), lines })) {
              setNets({});
              setReason("");
              onDone();
            }
          }}
        >
          Crear nota de crédito
        </button>
      </div>
      <p className="muted">El ITBIS de la nota lo calcula el sistema con la tasa de la factura. La nota la emite otra persona de Facturación.</p>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={create.error} />
    </>
  );
}

function RecordWithholding({ invoice, onDone }: { invoice: Invoice; onDone: () => void }) {
  const record = useCommand(`record-withholding:${invoice.header.invoiceId}`, "/api/v1/companies/{companyId}/sales/record-customer-withholding");
  const [form, setForm] = useState({ kind: "ITBIS", amount: "", date: todayInDominicanRepublic(), certificateNo: "", evidenceRef: "", evidenceSha256: "" });
  const [invalid, setInvalid] = useState<string | null>(null);
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [key]: e.target.value });
  return (
    <form
      className="inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const amount = normalizeInput(form.amount);
        if (!isPositiveDecimal(amount, 2)) {
          setInvalid("Indique el monto retenido (hasta 2 decimales).");
          return;
        }
        setInvalid(null);
        if (
          await record.run({
            invoiceId: invoice.header.invoiceId,
            kind: form.kind,
            amount,
            withholdingDate: form.date,
            certificateNo: form.certificateNo.trim(),
            evidenceRef: form.evidenceRef.trim(),
            evidenceSha256: form.evidenceSha256.trim(),
          })
        ) {
          onDone();
        }
      }}
    >
      <Field label="Tipo">
        <select value={form.kind} onChange={set("kind")}>
          <option value="ITBIS">ITBIS</option>
          <option value="ISR">ISR</option>
        </select>
      </Field>
      <Field label="Monto">
        <input inputMode="decimal" value={form.amount} onChange={set("amount")} />
      </Field>
      <Field label="Fecha">
        <input type="date" value={form.date} onChange={set("date")} required />
      </Field>
      <Field label="Número de certificado">
        <input value={form.certificateNo} onChange={set("certificateNo")} required />
      </Field>
      <Field label="Certificado">
        <input
          type="file"
          aria-label="Certificado de retención"
          onChange={async (e) => {
            const file = e.target.files?.[0];
            if (file) {
              setForm({ ...form, evidenceRef: file.name, evidenceSha256: await sha256Hex(file) });
            }
          }}
        />
      </Field>
      <Field label="Referencia">
        <input value={form.evidenceRef} onChange={set("evidenceRef")} required />
      </Field>
      <Field label="SHA-256">
        <input value={form.evidenceSha256} onChange={set("evidenceSha256")} required pattern="[0-9a-fA-F]{64}" size={66} />
      </Field>
      <button type="submit" disabled={record.busy}>
        Registrar retención
      </button>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={record.error} />
    </form>
  );
}

function WithholdingRow({ w, onDone }: { w: Schemas["InvoiceWithholdingView"]; onDone: () => void }) {
  const { can } = useSession();
  const reverse = useCommand(`reverse-withholding:${w.withholdingId}`, "/api/v1/companies/{companyId}/sales/reverse-customer-withholding");
  return (
    <tr>
      <td>{w.kind}</td>
      <td className="num">
        <Money value={w.amount} />
      </td>
      <td>{formatDate(w.withholdingDate)}</td>
      <td className="mono">{w.certificateNo}</td>
      <td>
        <StatusBadge status={w.status} />
        {w.reversalReason ? <div className="muted">{w.reversalReason}</div> : null}
      </td>
      <td>
        {w.status === "ACTIVE" && can("customer_withholding:reverse") ? (
          <ReasonAction label="Reversar" busy={reverse.busy} onConfirm={async (reason) => (await reverse.run({ withholdingId: w.withholdingId, expectedVersion: w.version, reason })) && onDone()} />
        ) : null}
        <ErrorBox error={reverse.error} />
      </td>
    </tr>
  );
}

function InvoiceDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/invoices/{invoiceId}", { path: { companyId, invoiceId: id } }) : null,
    [companyId, id],
  );
  const record = useCommand(`record-ecf:${id}`, "/api/v1/companies/{companyId}/sales/record-external-fiscal-document");
  const voidInvoice = useCommand(`void-invoice:${id}`, "/api/v1/companies/{companyId}/sales/void-unfiscalized-invoice");
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const h = data.header;
  const issued = h.commercialStatus !== "DRAFT";
  const open = h.commercialStatus === "CONFIRMED" || h.commercialStatus === "PARTIALLY_PAID";
  const pendingEcf = issued && h.commercialStatus !== "VOIDED" && h.fiscalStatus === "PENDING_EXTERNAL";
  const creditable = h.fiscalStatus === "ACCEPTED_EXTERNAL" && (open || h.commercialStatus === "PAID");
  return (
    <>
      <p>
        <Link href="/facturacion/facturas/">← Facturas</Link>
      </p>
      <h1>
        Factura {h.invoiceNo} <StatusBadge status={h.commercialStatus} testId="invoice-status" /> <StatusBadge status={h.fiscalStatus} testId="invoice-fiscal-status" />
      </h1>
      <p>
        {h.customerName} · e-CF {h.ecfType} {h.encf ? `· e-NCF ${h.encf}` : ""} · fecha {formatDate(h.invoiceDate)} · vence {formatDate(h.dueDate)} · contabilidad{" "}
        <AccountingStatus status={h.accountingStatus} eventId={data.postingEventId} />
      </p>
      {data.voidReason ? <p className="muted">Anulada: {data.voidReason}</p> : null}
      {issued && h.ecfType === EXEMPT_ECF_TYPE ? <Exemption invoiceId={h.invoiceId} /> : null}
      {h.commercialStatus === "DRAFT" && can("invoice:issue") ? <Issue invoice={data} onDone={reload} /> : null}

      <table>
        <thead>
          <tr>
            <th className="num">#</th>
            <th>Conduce</th>
            <th>Producto</th>
            <th className="num">Cantidad</th>
            <th className="num">Precio</th>
            <th className="num">Neto</th>
            <th className="num">ITBIS</th>
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => (
            <tr key={l.lineNo}>
              <td className="num">{l.lineNo}</td>
              <td className="mono">{l.deliveryNo}</td>
              <td>
                {l.itemCode} — {l.itemDescription}
              </td>
              <td className="num">
                {formatQuantity(l.quantity)} {l.uom}
              </td>
              <td className="num">
                <Money value={l.unitPrice} />
              </td>
              <td className="num">
                <Money value={l.netAmount} />
              </td>
              <td className="num">
                <Money value={l.itbis} />
              </td>
            </tr>
          ))}
          <tr>
            <th colSpan={5}>Totales</th>
            <td className="num">
              <Money value={h.netTotal} />
            </td>
            <td className="num">
              <Money value={h.taxTotal} />
            </td>
          </tr>
          <tr>
            <th colSpan={6}>Total · abierto</th>
            <td className="num">
              <Money value={h.total} testId="invoice-total" /> · <Money value={h.openAmount} testId="invoice-open" />
            </td>
          </tr>
        </tbody>
      </table>

      {pendingEcf ? <FiscalPackage invoiceId={h.invoiceId} /> : null}
      {pendingEcf && can("fiscal_document:record") ? (
        <>
          <h2>Registrar el e-CF emitido en el portal</h2>
          <RecordEcfForm
            prefix={invoiceEncfPrefix(h.ecfType)}
            busy={record.busy}
            error={record.error}
            onSubmit={async (v) => (await record.run({ invoiceId: h.invoiceId, expectedVersion: h.version, ...v })) && reload()}
          />
        </>
      ) : null}
      {data.fiscalRecord ? (
        <p>
          e-CF {data.fiscalRecord.encf} emitido {formatDateTime(data.fiscalRecord.issuedAt)} · código {data.fiscalRecord.securityCode} · {data.fiscalRecord.evidenceRef} · registró{" "}
          {data.fiscalRecord.recordedBy ?? "—"}
        </p>
      ) : null}
      {h.commercialStatus === "CONFIRMED" && h.fiscalStatus === "PENDING_EXTERNAL" && can("invoice:void") ? (
        <div className="actions">
          <ReasonAction label="Anular factura (nunca fiscalizada)" busy={voidInvoice.busy} onConfirm={async (reason) => (await voidInvoice.run({ invoiceId: h.invoiceId, expectedVersion: h.version, reason })) && reload()} />
          <ErrorBox error={voidInvoice.error} />
        </div>
      ) : null}

      <h2>Notas de crédito</h2>
      {data.creditNotes.length === 0 ? (
        <p className="muted">Sin notas de crédito.</p>
      ) : (
        <ul>
          {data.creditNotes.map((n) => (
            <li key={n.creditNoteId}>
              <Link href={`/facturacion/nota/?id=${n.creditNoteId}`}>{n.creditNoteNo}</Link> — <Money value={n.total} /> — {statusLabel(n.commercialStatus)} · {statusLabel(n.fiscalStatus)}
            </li>
          ))}
        </ul>
      )}
      {creditable && can("credit_note:create") ? <CreditNoteForm invoice={data} onDone={reload} /> : null}

      <h2>Retenciones del cliente</h2>
      {data.withholdings.length === 0 ? (
        <p className="muted">Sin retenciones.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Tipo</th>
              <th className="num">Monto</th>
              <th>Fecha</th>
              <th>Certificado</th>
              <th>Estado</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.withholdings.map((w) => (
              <WithholdingRow key={`${w.withholdingId}:${w.version}`} w={w} onDone={reload} />
            ))}
          </tbody>
        </table>
      )}
      {open && can("customer_withholding:record") ? <RecordWithholding invoice={data} onDone={reload} /> : null}
      <History history={data.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <InvoiceDetail />
    </Suspense>
  );
}
