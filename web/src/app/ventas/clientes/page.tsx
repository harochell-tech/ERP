"use client";

import Link from "next/link";
import { useState } from "react";
import { query } from "@/api/client";
import { PartyImportPanel } from "@/components/PartyImportPanel";
import { RncHint, useRncLookup } from "@/components/RncLookup";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { ERROR_MESSAGES } from "@/lib/errors";
import { batchSummary, type BatchItem } from "@/lib/partyImport";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { allCustomers } from "@/lib/paging";

// VS3-10a (E-VS3-10-3): customers — list and search (sales:read), create (customer:create, the Vendedor). Terms, activation and
// the credit exposure are on the customer's page.
// IMP-02 (E-IMP-1, E-IMP-7, E-IMP-01-6): import the ADM Cloud export (customer:import); for the selected drafts the Controller
// approves their terms (customer_terms:approve) and Crédito activates them (customer:activate), each with step-up.

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
  const [creating, setCreating] = useState(false);
  const { data, error, reload } = useLoad(
    can("sales:read") ? () => allCustomers(companyId, { status, search }) : null,
    [companyId, status, search],
  );
  const [importing, setImporting] = useState(false);
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set());
  const [batchNote, setBatchNote] = useState<string | null>(null);
  const approveMany = useCommand("approve-terms-batch", "/api/v1/companies/{companyId}/sales/approve-customer-terms-batch", (response) => {
    const result = response.result as unknown as { approved: number; items: BatchItem[] };
    return batchSummary(result.approved, result.items, "términos aprobados", ERROR_MESSAGES);
  });
  const activateMany = useCommand("activate-customers", "/api/v1/companies/{companyId}/sales/activate-customers", (response) => {
    const result = response.result as unknown as { activated: number; items: BatchItem[] };
    return batchSummary(result.activated, result.items, "clientes activados", ERROR_MESSAGES);
  });
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  const selectable = can("customer_terms:approve") || can("customer:activate");
  const drafts = (data?.items ?? []).filter((c) => c.customerStatus === "DRAFT").map((c) => c.partyId);
  // Only what is still a draft on screen is sent; at most 500 per command.
  const limit = Math.min(drafts.length, 500);
  const chosen = drafts.filter((id) => picked.has(id)).slice(0, 500);
  const toggle = (id: string, on: boolean) => {
    const next = new Set(picked);
    if (on) {
      next.add(id);
    } else {
      next.delete(id);
    }
    setPicked(next);
  };
  return (
    <>
      <div className="actions" style={{ justifyContent: "space-between" }}>
        <h1>Clientes</h1>
        {can("customer:create") && !creating ? (
          <button type="button" className="primary" onClick={() => setCreating(true)}>
            Nuevo cliente
          </button>
        ) : null}
      </div>
      {creating ? (
        <div className="card">
          <h2 style={{ marginTop: 0 }}>Nuevo cliente</h2>
          <p className="muted">
            Se crea en borrador con su RNC o cédula y razón social. Después, en su ficha, Crédito prepara sus términos de crédito y lo activa para
            tomarle pedidos.
          </p>
          <CreateCustomer
            onDone={() => {
              setCreating(false);
              reload();
            }}
          />
          <button type="button" onClick={() => setCreating(false)}>
            Cancelar
          </button>
        </div>
      ) : null}
      {can("customer:import") ? (
        importing ? (
          <PartyImportPanel kind="customers" onDone={reload} onClose={() => setImporting(false)} />
        ) : (
          <div className="actions">
            <button type="button" onClick={() => setImporting(true)}>
              Importar desde ADM Cloud
            </button>
          </div>
        )
      ) : null}
      <h2>Buscar clientes</h2>
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
      {selectable && drafts.length > 0 ? (
        <div className="actions" data-testid="customer-batch">
          <button type="button" onClick={() => setPicked(new Set(chosen.length === limit ? [] : drafts.slice(0, 500)))}>
            {chosen.length === limit ? "Quitar selección" : `Seleccionar los ${limit} borradores`}
          </button>
          {can("customer_terms:approve") ? (
            <ConfirmAction
              label={`Aprobar términos de los seleccionados (${chosen.length})`}
              stepUp
              busy={approveMany.busy}
              disabled={chosen.length === 0}
              consequence={`Se aprueban los términos de crédito en borrador de ${chosen.length} clientes, tal como están (días y límite). Los que usted mismo preparó se omiten: los aprueba otra persona.`}
              onConfirm={async () => {
                setBatchNote(null);
                const terms = await query("/api/v1/companies/{companyId}/sales/customer-terms", { path: { companyId }, query: { status: "DRAFT" } });
                const ids = terms.items.filter((t) => chosen.includes(t.partyId)).map((t) => t.termsVersionId);
                if (ids.length === 0) {
                  setBatchNote("Los clientes seleccionados no tienen términos en borrador que aprobar.");
                  return;
                }
                if (await approveMany.run({ termsVersionIds: ids })) {
                  reload();
                }
              }}
            />
          ) : null}
          {can("customer:activate") ? (
            <ConfirmAction
              label={`Activar seleccionados (${chosen.length})`}
              className="primary"
              stepUp
              busy={activateMany.busy}
              disabled={chosen.length === 0}
              consequence={`${chosen.length} clientes pasan de borrador a activos: se les podrá tomar pedidos. Los que aún no tienen términos aprobados se omiten.`}
              onConfirm={async () => {
                setBatchNote(null);
                if (await activateMany.run({ partyIds: chosen })) {
                  setPicked(new Set());
                  reload();
                }
              }}
            />
          ) : null}
          {batchNote ? <p className="notice">{batchNote}</p> : null}
          <ErrorBox error={approveMany.error ?? activateMany.error} />
        </div>
      ) : null}
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : data.items.length === 0 ? (
        <EmptyState title={search || status ? "Ningún cliente coincide con la búsqueda." : "Todavía no hay clientes."}>
          {can("customer:create") ? <p>Use «Nuevo cliente» para registrarlo con su RNC o cédula.</p> : null}
        </EmptyState>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              {selectable ? <th aria-label="Seleccionar" /> : null}
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
                {selectable ? (
                  <td>
                    {c.customerStatus === "DRAFT" ? (
                      <input type="checkbox" aria-label={`Seleccionar ${c.legalName}`} checked={picked.has(c.partyId)} onChange={(e) => toggle(c.partyId, e.target.checked)} />
                    ) : null}
                  </td>
                ) : null}
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
