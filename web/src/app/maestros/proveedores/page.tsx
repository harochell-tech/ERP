"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
import { statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Supplier = Schemas["SupplierView"];

// E-B03-15-4: suppliers — list, create (supplier:create), edit (supplier:update), activate (supplier:activate).

function CreateSupplier({ onDone }: { onDone: () => void }) {
  const create = useCommand("create-supplier", "/api/v1/companies/{companyId}/master-data/create-supplier");
  const [rnc, setRnc] = useState("");
  const [legalName, setLegalName] = useState("");
  return (
    <form
      className="inline-form"
      onSubmit={async (e) => {
        e.preventDefault();
        if (await create.run({ rnc: rnc.trim(), legalName: legalName.trim() })) {
          setRnc("");
          setLegalName("");
          onDone();
        }
      }}
    >
      <Field label="RNC o cédula">
        <input value={rnc} onChange={(e) => setRnc(e.target.value)} required />
      </Field>
      <Field label="Razón social">
        <input value={legalName} onChange={(e) => setLegalName(e.target.value)} required />
      </Field>
      <button type="submit" disabled={create.busy}>
        Crear proveedor
      </button>
      <ErrorBox error={create.error} />
    </form>
  );
}

function SupplierRow({ supplier, onDone }: { supplier: Supplier; onDone: () => void }) {
  const { can } = useSession();
  const id = supplier.supplierId;
  const update = useCommand(`update-supplier:${id}`, "/api/v1/companies/{companyId}/master-data/update-supplier");
  const activate = useCommand(`activate-supplier:${id}`, "/api/v1/companies/{companyId}/master-data/activate-supplier");
  const [editing, setEditing] = useState(false);
  const [rnc, setRnc] = useState(supplier.rnc ?? "");
  const [legalName, setLegalName] = useState(supplier.legalName);
  const busy = update.busy || activate.busy;

  return (
    <tr>
      <td>
        {editing ? <input aria-label="RNC" value={rnc} onChange={(e) => setRnc(e.target.value)} /> : (supplier.rnc ?? "—")}
      </td>
      <td>
        {editing ? <input aria-label="Razón social" value={legalName} onChange={(e) => setLegalName(e.target.value)} /> : supplier.legalName}
      </td>
      <td>{statusLabel(supplier.status)}</td>
      <td className="actions">
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

  if (!can("master_data:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Proveedores</h1>
      {can("supplier:create") ? <CreateSupplier onDone={reload} /> : null}
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay proveedores.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>RNC</th>
              <th>Razón social</th>
              <th>Estado</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.items.map((s) => (
              <SupplierRow key={`${s.supplierId}:${s.version}`} supplier={s} onDone={reload} />
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
