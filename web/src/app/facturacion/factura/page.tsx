"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { CopyField, RecordEcfForm } from "@/components/Ecf";
import { MoneyText, SalesHistory } from "@/components/SalesUx4";
import { LoadingIndicator } from "@/components/StateNotices";
import { AccountingStatus, ConfirmAction, ErrorBox, Field, FieldMessage, fieldAria, LineTable, Money, NoPermission, ReasonAction, StatusBadge, useFieldErrors } from "@/components/ui";
import { invoiceEncfPrefix } from "@/lib/authorizations";
import { formatQuantity, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate, formatDateTime, statusLabel, todayInDominicanRepublic } from "@/lib/labels";
import { sha256Hex } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { canSeeAccounting, creditNoteOffered, creditNoteStatusLabel, invoiceStatusLabel } from "@/lib/ux4bSales";
import { ecfTypeLabel } from "@/lib/ux4b";

type Invoice = Schemas["InvoiceDetail"];

const EXEMPT_ECF_TYPE = "44";

// VS3-10b (E-VS3-10-6/7): an invoice — issue (step-up), the fiscal package for the provider's portal, the e-CF record, void while
// never fiscalized (Controller), credit notes per line, and the customer's withholdings (recorded by Cobros, reversed by the
// Controller).

function Issue({ invoice, onDone }: { invoice: Invoice; onDone: () => void }) {
  const issue = useCommand(`issue-invoice:${invoice.header.invoiceId}`, "/api/v1/companies/{companyId}/sales/issue-invoice", `Factura ${invoice.header.invoiceNo} emitida y contabilizada.`);
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
            <option value="31">{ecfTypeLabel("31")}</option>
            <option value="32">{ecfTypeLabel("32")}</option>
          </select>
        </Field>
      )}
      <ConfirmAction
        label="Emitir factura"
        className="primary"
        stepUp
        busy={issue.busy}
        consequence={`La factura ${invoice.header.invoiceNo} se confirma y se contabiliza (cuenta por cobrar e ingreso); ya no se puede editar. Después se registra su e-CF del portal.`}
        onConfirm={async () =>
          (await issue.run({ invoiceId: invoice.header.invoiceId, expectedVersion: invoice.header.version, ecfType: exempt || ecfType === "" ? null : ecfType })) && onDone()
        }
      />
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
    return <LoadingIndicator error={error} />;
  }
  return (
    <>
      <h2>Paquete fiscal para el portal</h2>
      <div className="table-wrap"><table>
        <tbody>
          <CopyField label="Tipo de e-CF" value={data.ecfType} />
          <CopyField label="RNC del emisor" value={data.issuerRnc} />
          <CopyField label={data.ecfType === "32" ? "Cédula o RNC del receptor" : "RNC del receptor"} value={data.receiverRnc} />
          {data.receiverPassport ? <CopyField label="Pasaporte del receptor" value={data.receiverPassport} /> : null}
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
      </table></div>
    </>
  );
}

function CreditNoteForm({ invoice, onDone, onCancel }: { invoice: Invoice; onDone: () => void; onCancel: () => void }) {
  const create = useCommand(`create-credit-note:${invoice.header.invoiceId}`, "/api/v1/companies/{companyId}/sales/create-credit-note", (_r, doc) =>
    doc ? `Nota de crédito ${doc} creada en borrador sobre ${invoice.header.invoiceNo}.` : `Nota de crédito creada en borrador sobre ${invoice.header.invoiceNo}.`,
  );
  const [nets, setNets] = useState<Record<string, string>>({});
  const [category, setCategory] = useState("DESCUENTO");
  const [reason, setReason] = useState("");
  const [invalid, setInvalid] = useState<string | null>(null);
  const fe = useFieldErrors();
  return (
    <div className="card">
      <h3>Nueva nota de crédito</h3>
      <LineTable>
        <thead>
          <tr>
            <th className="num">Línea</th>
            <th>Producto</th>
            <th className="num">Neto (RD$)</th>
            <th className="num">Ya acreditado (RD$)</th>
            <th className="num">Queda (RD$)</th>
            <th className="num">Acreditar (RD$)</th>
          </tr>
        </thead>
        <tbody>
          {invoice.creditable.map((l) => {
            const lineError = fe.errors[`line-${l.invoiceLineId}`];
            return (
              <tr key={l.invoiceLineId}>
                <td className="num">{l.lineNo}</td>
                <td className="wrap">{l.itemCode}</td>
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
                  <input
                    aria-label={`Acreditar línea ${l.lineNo}`}
                    inputMode="decimal"
                    value={nets[l.invoiceLineId] ?? ""}
                    onChange={(e) => setNets({ ...nets, [l.invoiceLineId]: e.target.value })}
                    {...fieldAria(lineError, `credit-${l.invoiceLineId}`)}
                  />
                  <FieldMessage id={`credit-${l.invoiceLineId}`} error={lineError} />
                </td>
              </tr>
            );
          })}
        </tbody>
      </LineTable>
      <Field label="Motivo" required>
        <select value={category} onChange={(e) => setCategory(e.target.value)}>
          <option value="DESCUENTO">Descuento</option>
          <option value="ERROR_DE_PRECIO">Error de precio</option>
          <option value="OTRO">Otro</option>
        </select>
      </Field>
      <Field label="Explicación" required error={fe.errors.reason}>
        <input value={reason} onChange={(e) => setReason(e.target.value)} />
      </Field>
      <p className="muted">El ITBIS de la nota lo calcula el sistema con la tasa de la factura. La nota la emite otra persona de Facturación.</p>
      <div className="actions form-actions">
        <button type="button" onClick={onCancel}>
          Cancelar
        </button>
        <button
          type="button"
          className="primary"
          disabled={create.busy}
          onClick={async () => {
            const entered = Object.entries(nets)
              .map(([invoiceLineId, net]) => ({ invoiceLineId, netAmount: normalizeInput(net) }))
              .filter((l) => l.netAmount !== "");
            const lineErrors = Object.fromEntries(
              entered.filter((l) => !isPositiveDecimal(l.netAmount, 2)).map((l) => [`line-${l.invoiceLineId}`, "Monto mayor que cero, hasta 2 decimales."]),
            );
            if (!fe.check({ ...lineErrors, reason: reason.trim() === "" && "Escriba la explicación de la nota." })) {
              setInvalid(null);
              return;
            }
            if (entered.length === 0) {
              setInvalid("Indique al menos un monto a acreditar.");
              return;
            }
            setInvalid(null);
            if (await create.run({ invoiceId: invoice.header.invoiceId, reasonCategory: category, reason: reason.trim(), lines: entered })) {
              setNets({});
              setReason("");
              onDone();
            }
          }}
        >
          Crear nota de crédito
        </button>
        {invalid ? (
          <span className="error" role="alert">
            {invalid}
          </span>
        ) : null}
      </div>
      <ErrorBox error={create.error} />
    </div>
  );
}

function RecordWithholding({ invoice, onDone }: { invoice: Invoice; onDone: () => void }) {
  const record = useCommand(`record-withholding:${invoice.header.invoiceId}`, "/api/v1/companies/{companyId}/sales/record-customer-withholding", () => `Retención registrada en ${invoice.header.invoiceNo}.`);
  const [form, setForm] = useState({ kind: "ITBIS", amount: "", date: todayInDominicanRepublic(), certificateNo: "", evidenceRef: "", evidenceSha256: "" });
  const fe = useFieldErrors<"amount" | "date" | "certificateNo" | "evidenceRef" | "evidenceSha256">();
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [key]: e.target.value });
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const amount = normalizeInput(form.amount);
        if (
          !fe.check({
            amount: !isPositiveDecimal(amount, 2) && "Indique el monto retenido (mayor que cero, hasta 2 decimales).",
            date: !form.date && "Indique la fecha de la retención.",
            certificateNo: form.certificateNo.trim() === "" && "Indique el número de certificado.",
            evidenceRef: form.evidenceRef.trim() === "" && "Elija el archivo del certificado o escriba su referencia.",
            evidenceSha256: !/^[0-9a-fA-F]{64}$/.test(form.evidenceSha256.trim()) && "El SHA-256 tiene 64 caracteres hexadecimales (se calcula al elegir el archivo).",
          })
        ) {
          return;
        }
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
      <Field label="Tipo" required>
        <select value={form.kind} onChange={set("kind")}>
          <option value="ITBIS">ITBIS</option>
          <option value="ISR">ISR</option>
        </select>
      </Field>
      <Field label="Monto" required error={fe.errors.amount}>
        <input inputMode="decimal" value={form.amount} onChange={set("amount")} />
      </Field>
      <Field label="Fecha" required error={fe.errors.date}>
        <input type="date" value={form.date} onChange={set("date")} />
      </Field>
      <Field label="Número de certificado" required error={fe.errors.certificateNo}>
        <input value={form.certificateNo} onChange={set("certificateNo")} />
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
      <Field label="Referencia" required error={fe.errors.evidenceRef}>
        <input value={form.evidenceRef} onChange={set("evidenceRef")} />
      </Field>
      <Field label="SHA-256" required error={fe.errors.evidenceSha256}>
        <input className="mono" value={form.evidenceSha256} onChange={set("evidenceSha256")} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={record.busy}>
          Registrar retención
        </button>
      </div>
      <ErrorBox error={record.error} />
    </form>
  );
}

function WithholdingRow({ w, onDone }: { w: Schemas["InvoiceWithholdingView"]; onDone: () => void }) {
  const { can } = useSession();
  const reverse = useCommand(`reverse-withholding:${w.withholdingId}`, "/api/v1/companies/{companyId}/sales/reverse-customer-withholding", `Retención ${w.kind} (certificado ${w.certificateNo}) reversada.`);
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
          <ReasonAction label="Reversar" stepUp consequence="La retención se reversa con un asiento contrario y el saldo de la factura vuelve a subir por su monto. No se puede deshacer." busy={reverse.busy} onConfirm={async (reason) => (await reverse.run({ withholdingId: w.withholdingId, expectedVersion: w.version, reason })) && onDone()} />
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
  const record = useCommand(`record-ecf:${id}`, "/api/v1/companies/{companyId}/sales/record-external-fiscal-document", () => `e-CF registrado en la factura ${data?.header.invoiceNo ?? ""}.`);
  const voidInvoice = useCommand(`void-invoice:${id}`, "/api/v1/companies/{companyId}/sales/void-unfiscalized-invoice", () => `Factura ${data?.header.invoiceNo ?? ""} anulada.`);
  const [creditNoteOpen, setCreditNoteOpen] = useState(false);
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const h = data.header;
  const issued = h.commercialStatus !== "DRAFT";
  const open = h.commercialStatus === "CONFIRMED" || h.commercialStatus === "PARTIALLY_PAID";
  const pendingEcf = issued && h.commercialStatus !== "VOIDED" && h.fiscalStatus === "PENDING_EXTERNAL";
  // V-31: a credit note only while something is still owed on an invoice whose e-CF was accepted.
  const creditable = creditNoteOffered(h.commercialStatus, h.fiscalStatus);
  return (
    <>
      <p>
        <Link href="/facturacion/facturas/">← Facturas</Link>
      </p>
      <h1>
        Factura {h.invoiceNo} <StatusBadge status={h.commercialStatus} label={invoiceStatusLabel(h.commercialStatus)} testId="invoice-status" />{" "}
        <StatusBadge status={h.fiscalStatus} testId="invoice-fiscal-status" />
      </h1>
      {/* V-30: the header as a few labelled facts instead of one dense line. */}
      <div className="doc-facts">
        <div>
          <span>Cliente</span>
          <span>{h.customerName}</span>
        </div>
        <div>
          <span>Comprobante</span>
          <span>
            e-CF {ecfTypeLabel(h.ecfType)}
            {h.encf ? <span className="mono"> · {h.encf}</span> : null}
          </span>
        </div>
        <div>
          <span>Fecha · vence</span>
          <span>
            {formatDate(h.invoiceDate)} · {formatDate(h.dueDate)}
          </span>
        </div>
        <div>
          <span>Total de la factura</span>
          <span>
            <MoneyText value={h.total ?? h.netTotal} />
          </span>
        </div>
        <div>
          <span>Pendiente de cobro</span>
          <span>
            <MoneyText value={h.openAmount} />
          </span>
        </div>
        {canSeeAccounting(can) ? (
          <div>
            <span>Contabilidad</span>
            <span>
              <AccountingStatus status={h.accountingStatus} eventId={data.postingEventId} />
            </span>
          </div>
        ) : null}
      </div>
      {data.voidReason ? <p className="muted">Anulada: {data.voidReason}</p> : null}
      {issued && h.ecfType === EXEMPT_ECF_TYPE ? <Exemption invoiceId={h.invoiceId} /> : null}
      {h.commercialStatus === "DRAFT" && can("invoice:issue") ? <Issue invoice={data} onDone={reload} /> : null}

      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th className="num">#</th>
            <th>Conduce</th>
            <th>Producto</th>
            <th className="num">Cantidad</th>
            <th className="num">Precio (RD$)</th>
            <th className="num">Neto (RD$)</th>
            <th className="num">ITBIS (RD$)</th>
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => (
            <tr key={l.lineNo}>
              <td className="num">{l.lineNo}</td>
              <td className="mono">{l.deliveryNo}</td>
              <td>
                {l.itemCode} — {l.itemDescription}
                {l.lineKind === "FREIGHT" ? <div className="muted" data-testid={`invoice-line-freight:${l.lineNo}`}>Flete · exento de ITBIS</div> : null}
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
            <th colSpan={6}>Total con ITBIS (RD$)</th>
            <td className="num">
              <Money value={h.total} testId="invoice-total" />
            </td>
          </tr>
          <tr>
            <th colSpan={6}>Pendiente de cobro (RD$)</th>
            <td className="num">
              <Money value={h.openAmount} testId="invoice-open" />
            </td>
          </tr>
        </tbody>
      </table></div>

      {pendingEcf ? <FiscalPackage invoiceId={h.invoiceId} /> : null}
      {pendingEcf && can("fiscal_document:record") ? (
        <>
          <h2>Registrar el e-CF emitido en el portal</h2>
          <RecordEcfForm
            consumer={h.ecfType === "32"}
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
          <ReasonAction label="Anular factura (nunca fiscalizada)" stepUp consequence="La factura queda anulada y su asiento se reversa; los conduces vuelven a quedar por facturar. No se puede deshacer." busy={voidInvoice.busy} onConfirm={async (reason) => (await voidInvoice.run({ invoiceId: h.invoiceId, expectedVersion: h.version, reason })) && reload()} />
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
              <Link href={`/facturacion/nota/?id=${n.creditNoteId}`}>{n.creditNoteNo}</Link> — <MoneyText value={n.total} /> — {creditNoteStatusLabel(n.commercialStatus)} ·{" "}
              {statusLabel(n.fiscalStatus)}
            </li>
          ))}
        </ul>
      )}
      {creditable && can("credit_note:create") ? (
        creditNoteOpen ? (
          <CreditNoteForm invoice={data} onDone={() => { setCreditNoteOpen(false); reload(); }} onCancel={() => setCreditNoteOpen(false)} />
        ) : (
          <div className="actions">
            <button type="button" onClick={() => setCreditNoteOpen(true)}>
              Nueva nota de crédito
            </button>
          </div>
        )
      ) : null}

      <h2>Retenciones del cliente</h2>
      {data.withholdings.length === 0 ? (
        <p className="muted">Sin retenciones.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Tipo</th>
              <th className="num">Monto (RD$)</th>
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
        </table></div>
      )}
      {open && can("customer_withholding:record") ? <RecordWithholding invoice={data} onDone={reload} /> : null}
      <SalesHistory history={data.history} label={invoiceStatusLabel} />
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
