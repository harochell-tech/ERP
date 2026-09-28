"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { ErrorBox, Field, Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
import { isDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Customer = Schemas["CustomerDetail"];

// VS3-10a: a customer — contact data (customer:update), credit terms prepared by Crédito and approved by the Controller (never the
// preparer, step-up), activation by Crédito (step-up) and the credit exposure the order check uses (E-VS3-14).

function EditCustomer({ customer, onDone }: { customer: Customer; onDone: () => void }) {
  const update = useCommand(`update-customer:${customer.partyId}`, "/api/v1/companies/{companyId}/sales/update-customer");
  const [form, setForm] = useState({ rnc: customer.rnc ?? "", legalName: customer.legalName, phone: customer.phone ?? "", email: customer.email ?? "", address: customer.address ?? "" });
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm({ ...form, [key]: e.target.value });
  const draft = customer.customerStatus === "DRAFT";
  return (
    <form
      onSubmit={async (e) => {
        e.preventDefault();
        const optional = (v: string) => (v.trim() === "" ? null : v.trim());
        if (
          await update.run({
            partyId: customer.partyId,
            expectedVersion: customer.version,
            rnc: form.rnc.trim(),
            legalName: form.legalName.trim(),
            phone: optional(form.phone),
            email: optional(form.email),
            address: optional(form.address),
          })
        ) {
          onDone();
        }
      }}
    >
      <Field label="RNC o cédula">
        <input value={form.rnc} onChange={set("rnc")} disabled={!draft} required />
      </Field>
      <Field label="Razón social">
        <input value={form.legalName} onChange={set("legalName")} disabled={!draft} required />
      </Field>
      <Field label="Teléfono">
        <input value={form.phone} onChange={set("phone")} />
      </Field>
      <Field label="Correo">
        <input type="email" value={form.email} onChange={set("email")} />
      </Field>
      <Field label="Dirección">
        <input value={form.address} onChange={set("address")} />
      </Field>
      <button type="submit" disabled={update.busy}>
        Guardar datos
      </button>
      {!draft ? <p className="muted">El RNC y la razón social solo cambian mientras el cliente está en borrador.</p> : null}
      <ErrorBox error={update.error} />
    </form>
  );
}

function PrepareTerms({ partyId, onDone }: { partyId: string; onDone: () => void }) {
  const prepare = useCommand(`prepare-terms:${partyId}`, "/api/v1/companies/{companyId}/sales/prepare-customer-terms");
  const [days, setDays] = useState("30");
  const [limit, setLimit] = useState("");
  const [hold, setHold] = useState(false);
  const [invalid, setInvalid] = useState<string | null>(null);
  return (
    <form
      className="inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const creditLimit = normalizeInput(limit);
        if (!/^\d{1,3}$/.test(days) || !isDecimal(creditLimit, 2) || creditLimit.startsWith("-")) {
          setInvalid("Indique los días (0 a 999) y el límite (monto de hasta 2 decimales).");
          return;
        }
        setInvalid(null);
        if (await prepare.run({ partyId, paymentTermsDays: Number(days), creditLimit, creditHold: hold })) {
          onDone();
        }
      }}
    >
      <Field label="Días de crédito">
        <input inputMode="numeric" value={days} onChange={(e) => setDays(e.target.value)} />
      </Field>
      <Field label="Límite de crédito">
        <input inputMode="decimal" value={limit} onChange={(e) => setLimit(e.target.value)} />
      </Field>
      <label className="field">
        <span>Retener crédito</span>
        <input type="checkbox" checked={hold} onChange={(e) => setHold(e.target.checked)} />
      </label>
      <button type="submit" disabled={prepare.busy}>
        Preparar términos
      </button>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={prepare.error} />
    </form>
  );
}

function TermsRow({ terms, onDone }: { terms: Schemas["CustomerTermsView"]; onDone: () => void }) {
  const { can } = useSession();
  const approve = useCommand(`approve-terms:${terms.termsVersionId}`, "/api/v1/companies/{companyId}/sales/approve-customer-terms");
  return (
    <tr>
      <td className="num">{terms.version}</td>
      <td>{formatDate(terms.effectiveFrom)}</td>
      <td className="num">{terms.paymentTermsDays}</td>
      <td className="num">
        <Money value={terms.creditLimit} />
      </td>
      <td>{terms.creditHold ? "Sí" : "No"}</td>
      <td>
        <StatusBadge status={terms.status} />
      </td>
      <td>{terms.preparedBy ?? "—"}</td>
      <td>{terms.approvedBy ?? "—"}</td>
      <td className="actions">
        {terms.status === "DRAFT" && can("customer_terms:approve") ? (
          <button type="button" disabled={approve.busy} onClick={async () => (await approve.run({ termsVersionId: terms.termsVersionId })) && onDone()}>
            Aprobar
          </button>
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
    return <Loading error={error} />;
  }
  return (
    <table>
      <tbody>
        <tr>
          <th>CxC abierta</th>
          <td className="num">
            <Money value={data.openAr} />
          </td>
        </tr>
        <tr>
          <th>Pedidos confirmados sin entregar</th>
          <td className="num">
            <Money value={data.undeliveredOrders} />
          </td>
        </tr>
        <tr>
          <th>Entregado sin facturar</th>
          <td className="num">
            <Money value={data.deliveredUninvoiced} />
          </td>
        </tr>
        <tr>
          <th>Exposición</th>
          <td className="num">
            <Money value={data.exposure} testId="exposure" />
          </td>
        </tr>
        <tr>
          <th>Límite</th>
          <td className="num">
            <Money value={data.creditLimit} />
          </td>
        </tr>
        <tr>
          <th>Disponible</th>
          <td className="num">
            <Money value={data.available} />
          </td>
        </tr>
        <tr>
          <th>Días de atraso</th>
          <td className="num">{data.overdueDays}</td>
        </tr>
      </tbody>
    </table>
  );
}

function CustomerDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/customers/{partyId}", { path: { companyId, partyId: id } }) : null,
    [companyId, id],
  );
  const activate = useCommand(`activate-customer:${id}`, "/api/v1/companies/{companyId}/sales/activate-customer");
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
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
          <button type="button" disabled={activate.busy} onClick={async () => (await activate.run({ partyId: data.partyId, expectedVersion: data.version })) && reload()}>
            Activar cliente
          </button>
          <ErrorBox error={activate.error} />
        </div>
      ) : null}
      {can("customer:update") ? <EditCustomer key={data.version} customer={data} onDone={reload} /> : null}
      <h2>Exposición de crédito</h2>
      <Exposure partyId={data.partyId} />
      <h2>Términos de crédito</h2>
      {can("customer_terms:prepare") ? <PrepareTerms partyId={data.partyId} onDone={reload} /> : null}
      {data.terms.length === 0 ? (
        <p className="muted">Sin términos todavía.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th className="num">Versión</th>
              <th>Vigente desde</th>
              <th className="num">Días</th>
              <th className="num">Límite</th>
              <th>Retenido</th>
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
        </table>
      )}
      <History history={data.history} />
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
