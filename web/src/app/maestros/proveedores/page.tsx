"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { RncHint, useRncLookup } from "@/components/RncLookup";
import { ErrorBox, Field, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { matchesSearch } from "@/lib/ux4b";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Supplier = Schemas["SupplierView"];

// E-B03-15-4: suppliers — list, create (supplier:create), edit (supplier:update), activate (supplier:activate).

function CreateSupplier({ onDone }: { onDone: () => void }) {
  const create = useCommand("create-supplier", "/api/v1/companies/{companyId}/master-data/create-supplier");
  const [rnc, setRnc] = useState("");
  const [legalName, setLegalName] = useState("");
  const registry = useRncLookup((name) => setLegalName((current) => (current.trim() ? current : name)));
  const fe = useFieldErrors<"rnc" | "legalName">();
  return (
    <form
      className="card"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (!fe.check({ rnc: rnc.trim() === "" && "Indique el RNC o la cédula.", legalName: legalName.trim() === "" && "Indique la razón social." })) {
          return;
        }
        if (await create.run({ rnc: rnc.trim(), legalName: legalName.trim() }, undefined, `Proveedor ${legalName.trim()} creado en borrador.`)) {
          setRnc("");
          setLegalName("");
          registry.clear();
          onDone();
        }
      }}
    >
      <Field label="RNC o cédula" required error={fe.errors.rnc}>
        <input value={rnc} inputMode="numeric" onChange={(e) => setRnc(e.target.value)} onBlur={() => registry.lookUp(rnc)} />
      </Field>
      <Field label="Razón social" required error={fe.errors.legalName}>
        <input value={legalName} onChange={(e) => setLegalName(e.target.value)} />
      </Field>
      <RncHint result={registry.result} />
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={create.busy}>
          Crear proveedor
        </button>
      </div>
      <ErrorBox error={create.error} />
    </form>
  );
}

function SupplierRow({ supplier, onDone }: { supplier: Supplier; onDone: () => void }) {
  const { can } = useSession();
  const id = supplier.supplierId;
  const update = useCommand(`update-supplier:${id}`, "/api/v1/companies/{companyId}/master-data/update-supplier", `Proveedor ${supplier.legalName} actualizado.`);
  const activate = useCommand(`activate-supplier:${id}`, "/api/v1/companies/{companyId}/master-data/activate-supplier", `Proveedor ${supplier.legalName} activado.`);
  const [editing, setEditing] = useState(false);
  const [rnc, setRnc] = useState(supplier.rnc ?? "");
  const [legalName, setLegalName] = useState(supplier.legalName);
  const busy = update.busy || activate.busy;

  return (
    <tr>
      <td>
        {editing ? <input aria-label="RNC" value={rnc} onChange={(e) => setRnc(e.target.value)} /> : (supplier.rnc ?? "—")}
      </td>
      <td className="wrap">
        {editing ? (
          <input aria-label="Razón social" value={legalName} onChange={(e) => setLegalName(e.target.value)} />
        ) : (
          <Link href={`/maestros/proveedor/?id=${id}`}>{supplier.legalName}</Link>
        )}
      </td>
      <td>
        <StatusBadge status={supplier.status} />
      </td>
      <td>
        <StatusBadge status={supplier.bankAccountState} />
      </td>
      <td className="num">
        <Money value={supplier.openApAmount} />
      </td>
      <td>
        {/* UX4-03 (C-33): the row's buttons side by side in one aligned group; the error under them. */}
        <div className="actions row-buttons">
        {editing ? (
          <>
            <button
              type="button"
              disabled={busy}
              onClick={async () => {
                if (await update.run({ partyId: id, expectedVersion: supplier.version, rnc: rnc.trim(), legalName: legalName.trim() })) {
                  setEditing(false);
                  onDone();
                }
              }}
            >
              Guardar
            </button>
            <button type="button" onClick={() => setEditing(false)}>
              Cancelar
            </button>
          </>
        ) : (
          <>
            {can("supplier:update") ? (
              <button type="button" onClick={() => setEditing(true)}>
                Editar
              </button>
            ) : null}
            {supplier.status !== "ACTIVE" && can("supplier:activate") ? (
              <button type="button" disabled={busy} onClick={async () => (await activate.run({ partyId: id, expectedVersion: supplier.version })) && onDone()}>
                Activar
              </button>
            ) : null}
          </>
        )}
        </div>
        <ErrorBox error={update.error ?? activate.error} />
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const { data, error, reload } = useLoad(
    can("master_data:read") ? () => query("/api/v1/companies/{companyId}/master-data/suppliers", { path: { companyId }, query: { limit: 200 } }) : null,
    [companyId],
  );

  const [search, setSearch] = useState("");
  const [creating, setCreating] = useState(false);
  if (!can("master_data:read")) {
    return <NoPermission />;
  }
  const shown = data?.items.filter((s) => matchesSearch(search, s.legalName, s.rnc)) ?? [];
  return (
    <>
      <h1>Proveedores</h1>
      {can("supplier:create") ? (
        creating ? (
          <CreateSupplier
            onDone={() => {
              setCreating(false);
              reload();
            }}
          />
        ) : (
          <div className="actions">
            <button type="button" className="primary" onClick={() => setCreating(true)}>
              Nuevo proveedor
            </button>
          </div>
        )
      ) : null}
      <div className="inline-form" role="search">
        <Field label="Buscar proveedor">
          <input type="search" placeholder="Razón social o RNC" value={search} onChange={(e) => setSearch(e.target.value)} />
        </Field>
      </div>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : data.items.length === 0 ? (
        <EmptyState title="Aún no hay proveedores.">
          <p>{can("supplier:create") ? "Registre el primero con «Nuevo proveedor»: el RNC se busca en el padrón de la DGII." : "Quien tenga el permiso de crear proveedores los registra aquí."}</p>
        </EmptyState>
      ) : shown.length === 0 ? (
        <EmptyState title="Ningún proveedor coincide con la búsqueda.">
          <p>Pruebe con otra parte de la razón social o con el RNC.</p>
        </EmptyState>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>RNC</th>
              <th>Razón social</th>
              <th>Estado</th>
              <th>Cuenta bancaria</th>
              <th className="num">Saldo por pagar (RD$)</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {shown.map((s) => (
              <SupplierRow key={`${s.supplierId}:${s.version}`} supplier={s} onDone={reload} />
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
