"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { RncHint, useRncLookup } from "@/components/RncLookup";
import { ErrorBox, Field, Loading, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-10-3): customers — list and search (sales:read), create (customer:create, the Vendedor). Terms, activation and
// the credit exposure are on the customer's page.

function CreateCustomer({ onDone }: { onDone: () => void }) {
  const create = useCommand("create-customer", "/api/v1/companies/{companyId}/sales/create-customer");
  const [rnc, setRnc] = useState("");
  const [legalName, setLegalName] = useState("");
  const fe = useFieldErrors<"rnc" | "legalName">();
  const registry = useRncLookup((name) => setLegalName((current) => (current.trim() ? current : name)));
  return (
    <form
      className="inline-form"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (!fe.check({ rnc: rnc.trim() === "" && "Indique el RNC o la cédula.", legalName: legalName.trim() === "" && "Indique la razón social." })) {
          return;
        }
        if (await create.run({ rnc: rnc.trim(), legalName: legalName.trim() }, undefined, `Cliente ${legalName.trim()} creado en borrador.`)) {
          setRnc("");
          setLegalName("");
          registry.clear();
          onDone();
        }
      }}
    >
      <Field label="RNC o cédula" required error={fe.errors.rnc}>
        <input value={rnc} onChange={(e) => setRnc(e.target.value)} onBlur={() => registry.lookUp(rnc)} />
      </Field>
      <Field label="Razón social" required error={fe.errors.legalName}>
        <input value={legalName} onChange={(e) => setLegalName(e.target.value)} />
      </Field>
      <button type="submit" className="primary" disabled={create.busy}>
        Crear cliente
      </button>
      <RncHint result={registry.result} />
      <ErrorBox error={create.error} />
    </form>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const [status, setStatus] = useState("");
  const [search, setSearch] = useState("");
  const { data, error, reload } = useLoad(
    can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/customers", { path: { companyId }, query: { status, search, limit: 200 } }) : null,
    [companyId, status, search],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Clientes</h1>
      {can("customer:create") ? <CreateCustomer onDone={reload} /> : null}
      <div className="inline-form">
        <Field label="Estado">
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Todos</option>
            <option value="DRAFT">Borrador</option>
            <option value="ACTIVE">Activos</option>
            <option value="BLOCKED">Bloqueados</option>
          </select>
        </Field>
        <Field label="Buscar (RNC o nombre)">
          <input value={search} onChange={(e) => setSearch(e.target.value)} />
        </Field>
      </div>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay clientes.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>RNC</th>
              <th>Razón social</th>
              <th>Estado</th>
              <th className="num">Días de crédito</th>
              <th className="num">Límite de crédito (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((c) => (
              <tr key={c.partyId}>
                <td className="mono">{c.rnc ?? "—"}</td>
                <td>
                  <Link href={`/ventas/cliente/?id=${c.partyId}`}>{c.legalName}</Link>
                </td>
                <td>
                  <StatusBadge status={c.customerStatus} />
                  {c.creditHold ? <StatusBadge status="BLOCKED" label="Crédito retenido" /> : null}
                </td>
                <td className="num">{c.paymentTermsDays ?? "—"}</td>
                <td className="num">
                  <Money value={c.creditLimit} />
                </td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
