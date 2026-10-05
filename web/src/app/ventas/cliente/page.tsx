"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { SalesHistory } from "@/components/SalesUx4";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { isDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { emailsProblem, parseEmails } from "@/lib/partyImport";
import { useLoad } from "@/lib/useQuery";

type Customer = Schemas["CustomerDetail"];

// VS3-10a: a customer — contact data (customer:update), credit terms prepared by Crédito and approved by the Controller (never the
// preparer, step-up), activation by Crédito (step-up) and the credit exposure the order check uses (E-VS3-14).

function EditCustomer({ customer, onDone }: { customer: Customer; onDone: () => void }) {
  const update = useCommand(`update-customer:${customer.partyId}`, "/api/v1/companies/{companyId}/sales/update-customer", `Datos del cliente ${customer.legalName} guardados.`);
  const [form, setForm] = useState({ rnc: customer.rnc ?? "", legalName: customer.legalName, phone: customer.phone ?? "", emails: customer.emails.join("\n"), address: customer.address ?? "" });
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [key]: e.target.value });
  const draft = customer.customerStatus === "DRAFT";
  const fe = useFieldErrors<"rnc" | "legalName" | "emails">();
  return (
    <form
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const emails = parseEmails(form.emails);
        if (
          !fe.check({
            rnc: form.rnc.trim() === "" && "Indique el RNC o la cédula.",
            legalName: form.legalName.trim() === "" && "Indique la razón social.",
            emails: emailsProblem(emails),
          })
        ) {
          return;
        }
        const optional = (v: string) => (v.trim() === "" ? null : v.trim());
        if (
          await update.run({
            partyId: customer.partyId,
            expectedVersion: customer.version,
            rnc: form.rnc.trim(),
            legalName: form.legalName.trim(),
            phone: optional(form.phone),
            email: null,
            emails,
            address: optional(form.address),
          })
        ) {
          onDone();
        }
      }}
    >
      <Field label="RNC o cédula" required error={fe.errors.rnc}>
        <input value={form.rnc} onChange={set("rnc")} disabled={!draft} />
      </Field>
      <Field label="Razón social" required error={fe.errors.legalName}>
        <input value={form.legalName} onChange={set("legalName")} disabled={!draft} />
      </Field>
      <Field label="Teléfono">
        <input value={form.phone} onChange={set("phone")} />
      </Field>
      <Field label="Correos" wide error={fe.errors.emails} hint="Uno por línea, hasta diez. El primero es el principal.">
        <textarea rows={3} value={form.emails} onChange={set("emails")} />
      </Field>
      <Field label="Dirección">
        <input value={form.address} onChange={set("address")} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={update.busy}>
          Guardar datos
        </button>
      </div>
      {!draft ? <p className="muted">El RNC y la razón social solo cambian mientras el cliente está en borrador.</p> : null}
      <ErrorBox error={update.error} />
    </form>
  );
}

function PrepareTerms({ partyId, current, onDone }: { partyId: string; current: Schemas["CustomerTermsView"] | undefined; onDone: () => void }) {
  const prepare = useCommand(`prepare-terms:${partyId}`, "/api/v1/companies/{companyId}/sales/prepare-customer-terms", "Términos de crédito preparados; falta la aprobación del Controller.");
  // V-16: start from the terms in force (days, limit, hold), so a change only touches what changes.
  const [days, setDays] = useState(current ? String(current.paymentTermsDays) : "30");
  const [limit, setLimit] = useState(current?.creditLimit ?? "");
  const [hold, setHold] = useState(current?.creditHold ?? false);
  // PRS-05 (E-PRS-05-4, E-PRS-02-3): the customer's price list rides its terms; it keeps the one in force unless Crédito changes it.
  const [list, setList] = useState(current?.priceListId ?? "");
  const { companyId } = useSession();
  const lists = useLoad(() => query("/api/v1/companies/{companyId}/sales/price-list-headers", { path: { companyId } }), [companyId]);
  const fe = useFieldErrors<"days" | "limit">();
  return (
    <form
      className="inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const creditLimit = normalizeInput(limit);
        if (
          !fe.check({
            days: !/^\d{1,3}$/.test(days) && "Indique los días de crédito (0 a 999).",
            limit: (!isDecimal(creditLimit, 2) || creditLimit.startsWith("-")) && "Indique el límite: un monto de cero o más, hasta 2 decimales.",
          })
        ) {
          return;
        }
        if (await prepare.run({ partyId, paymentTermsDays: Number(days), creditLimit, creditHold: hold, priceListId: list || null })) {
          onDone();
        }
      }}
    >
      <Field label="Días de crédito" required error={fe.errors.days}>
        <input inputMode="numeric" value={days} onChange={(e) => setDays(e.target.value)} />
      </Field>
      <Field label="Límite de crédito" required error={fe.errors.limit} hint={current ? "En RD$. Se propone el límite vigente." : "En RD$."}>
        <input inputMode="decimal" value={limit} onChange={(e) => setLimit(e.target.value)} />
      </Field>
      <Field label="Lista de precios" hint="Lo que no esté en ella se cobra con «General»; el flete sale solo de ella.">
        <select aria-label="Lista de precios" value={list} onChange={(e) => setList(e.target.value)}>
          {current ? null : <option value="">General</option>}
          {(lists.data?.items ?? [])
            .filter((l) => l.status === "ACTIVE" || l.priceListId === list)
            .map((l) => (
              <option key={l.priceListId} value={l.priceListId}>
                {l.name}
              </option>
            ))}
        </select>
      </Field>
      <label className="field">
        <span>Retener crédito</span>
        <input type="checkbox" checked={hold} onChange={(e) => setHold(e.target.checked)} />
        <span className="field-hint" style={{ fontWeight: 400 }}>Con el crédito retenido, ningún pedido del cliente se confirma solo: todos pasan a Crédito para aprobación.</span>
      </label>
      <button type="submit" className="primary" disabled={prepare.busy}>
        Preparar términos
      </button>
      <p className="muted" style={{ flexBasis: "100%" }}>
        «Preparar términos» guarda una propuesta en borrador; no cambia nada hasta que el Controller la apruebe. Desde ese día rige para los pedidos
        nuevos.
      </p>
      <ErrorBox error={prepare.error} />
    </form>
  );
}

function TermsRow({ terms, onDone }: { terms: Schemas["CustomerTermsView"]; onDone: () => void }) {
  const { can } = useSession();
  const approve = useCommand(
    `approve-terms:${terms.termsVersionId}`,
    "/api/v1/companies/{companyId}/sales/approve-customer-terms",
    `Términos de crédito versión ${terms.version} aprobados.`,
  );
  return (
    <tr>
      <td className="num">{terms.version}</td>
      <td>{formatDate(terms.effectiveFrom)}</td>
      <td className="num">{terms.paymentTermsDays}</td>
      <td className="num">
        <Money value={terms.creditLimit} />
      </td>
      <td>{terms.creditHold ? "Sí" : "No"}</td>
      <td data-testid={`terms-list:${terms.version}`}>{terms.priceListName}</td>
      <td>
        <StatusBadge status={terms.status} />
      </td>
      <td className="wrap">{terms.preparedBy ?? "—"}</td>
      <td className="wrap">{terms.approvedBy ?? "—"}</td>
      <td className="actions">
        {terms.status === "DRAFT" && can("customer_terms:approve") ? (
          <ConfirmAction
            label="Aprobar"
            title="¿Aprobar los términos de crédito?"
            busy={approve.busy}
            stepUp
            consequence={`Los términos (${terms.paymentTermsDays} días de crédito) entran en vigor hoy y reemplazan a los vigentes; la evaluación de crédito de los pedidos los usa desde ahora.`}
            onConfirm={async () => (await approve.run({ termsVersionId: terms.termsVersionId })) && onDone()}
          />
        ) : null}
        <ErrorBox error={approve.error} />
      </td>
    </tr>
  );
}

function Exposure({ partyId }: { partyId: string }) {
  const { companyId } = useSession();
  const { data, error } = useLoad(() => query("/api/v1/companies/{companyId}/sales/customers/{partyId}/exposure", { path: { companyId, partyId } }), [companyId, partyId]);
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  return (
    <div className="table-wrap"><table>
      <tbody>
        <tr>
          <th>Facturas pendientes de cobro</th>
          <td className="num">
            <Money value={data.openAr} currency />
          </td>
        </tr>
        <tr>
          <th>Pedidos confirmados sin entregar</th>
          <td className="num">
            <Money value={data.undeliveredOrders} currency />
          </td>
        </tr>
        <tr>
          <th>Entregado sin facturar</th>
          <td className="num">
            <Money value={data.deliveredUninvoiced} currency />
          </td>
        </tr>
        <tr>
          <th>Crédito usado (total)</th>
          <td className="num">
            <Money value={data.exposure} testId="exposure" currency />
          </td>
        </tr>
        <tr>
          <th>Límite</th>
          <td className="num">
            <Money value={data.creditLimit} currency />
          </td>
        </tr>
        <tr>
          <th>Disponible</th>
          <td className="num">
            <Money value={data.available} currency />
          </td>
        </tr>
        <tr>
          <th>Días de atraso (factura más vencida)</th>
          <td className="num">{data.overdueDays}</td>
        </tr>
      </tbody>
    </table></div>
  );
}

function CustomerDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/customers/{partyId}", { path: { companyId, partyId: id } }) : null,
    [companyId, id],
  );
  const activate = useCommand(`activate-customer:${id}`, "/api/v1/companies/{companyId}/sales/activate-customer", "Cliente activado.");
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  return (
    <>
      <p>
        <Link href="/ventas/clientes/">← Clientes</Link>
      </p>
      <h1>
        {data.legalName} <StatusBadge status={data.customerStatus} testId="customer-status" />
      </h1>
      <p className="muted">RNC {data.rnc ?? "—"}</p>
      {data.customerStatus === "DRAFT" && can("customer:activate") ? (
        <div className="actions">
          <ConfirmAction
            label="Activar cliente"
            className="primary"
            busy={activate.busy}
            stepUp
            consequence={`${data.legalName} queda activo: se le podrán tomar pedidos y facturar, y su RNC y razón social ya no se podrán cambiar.`}
            onConfirm={async () => (await activate.run({ partyId: data.partyId, expectedVersion: data.version })) && reload()}
          />
          <ErrorBox error={activate.error} />
        </div>
      ) : null}
      {can("customer:update") ? <EditCustomer key={data.version} customer={data} onDone={reload} /> : null}
      <h2>Crédito usado</h2>
      <p className="muted">Lo que el cliente ya debe o tiene comprometido; la evaluación de crédito de un pedido lo suma al monto del pedido.</p>
      <Exposure partyId={data.partyId} />
      <h2>Términos de crédito y lista de precios</h2>
      {data.terms.find((t) => t.status === "ACTIVE") ? (
        <p data-testid="customer-price-list">
          Compra con la lista <strong>{data.terms.find((t) => t.status === "ACTIVE")?.priceListName}</strong>.
        </p>
      ) : null}
      {can("customer_terms:prepare") ? (
        <PrepareTerms key={data.terms.find((t) => t.status === "ACTIVE")?.termsVersionId ?? "none"} partyId={data.partyId} current={data.terms.find((t) => t.status === "ACTIVE")} onDone={reload} />
      ) : null}
      {data.terms.length === 0 ? (
        <EmptyState title="Sin términos de crédito todavía.">
          <p>Sin términos aprobados, los pedidos de este cliente no se pueden confirmar: Crédito los prepara y el Controller los aprueba.</p>
        </EmptyState>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th className="num">Versión</th>
              <th>Vigente desde</th>
              <th className="num">Días</th>
              <th className="num">Límite (RD$)</th>
              <th>Retenido</th>
              <th>Lista de precios</th>
              <th>Estado</th>
              <th>Preparó</th>
              <th>Aprobó</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.terms.map((t) => (
              <TermsRow key={`${t.termsVersionId}:${t.status}`} terms={t} onDone={reload} />
            ))}
          </tbody>
        </table></div>
      )}
      <SalesHistory history={data.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <CustomerDetail />
    </Suspense>
  );
}
