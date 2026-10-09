"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useRef, useState } from "react";
import { type Schemas } from "@/api/client";
import { PartyImportPanel } from "@/components/PartyImportPanel";
import { RncHint, useRncLookup } from "@/components/RncLookup";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { matchesSearch } from "@/lib/ux4b";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { allSuppliers } from "@/lib/paging";

type Supplier = Schemas["SupplierView"];

// E-B03-15-4: suppliers — list, create (supplier:create), edit (supplier:update), activate (supplier:activate).
// IMP-02 (E-IMP-1, E-IMP-7): import the ADM Cloud export (supplier:import) and activate the selected drafts (supplier:activate).

/** OCR1-03 (E-OCR1-01-4): from a received document, the form opens with its RNC and the registry's name. */
function CreateSupplier({ onDone, initialRnc = "" }: { onDone: () => void; initialRnc?: string }) {
  const create = useCommand("create-supplier", "/api/v1/companies/{companyId}/master-data/create-supplier");
  const [rnc, setRnc] = useState(initialRnc);
  const [legalName, setLegalName] = useState("");
  const registry = useRncLookup((name) => setLegalName((current) => (current.trim() ? current : name)));
  const { lookUp } = registry;
  const lookedUp = useRef(false);
  useEffect(() => {
    if (initialRnc && !lookedUp.current) {
      lookedUp.current = true;
      void lookUp(initialRnc);
    }
  }, [initialRnc, lookUp]);
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

// USD1-07a (E-USD1-07-2, E-USD1-03-9): a foreign supplier — legal name, country (two letters) and its tax id abroad; no RNC.
function CreateForeignSupplier({ onDone }: { onDone: () => void }) {
  const create = useCommand("create-foreign-supplier", "/api/v1/companies/{companyId}/master-data/create-foreign-supplier");
  const [legalName, setLegalName] = useState("");
  const [country, setCountry] = useState("");
  const [taxId, setTaxId] = useState("");
  const fe = useFieldErrors<"legalName" | "country">();
  return (
    <form
      className="card"
      noValidate
      data-testid="foreign-supplier-form"
      onSubmit={async (e) => {
        e.preventDefault();
        if (
          !fe.check({
            legalName: legalName.trim() === "" && "Indique la razón social.",
            country: !/^[A-Za-z]{2}$/.test(country.trim()) && "Indique el país con su código de 2 letras (US, CN, ES…).",
          })
        ) {
          return;
        }
        const body = { legalName: legalName.trim(), country: country.trim().toUpperCase(), foreignTaxId: taxId.trim() || null };
        if (await create.run(body, undefined, `Proveedor del exterior ${legalName.trim()} creado en borrador.`)) {
          setLegalName("");
          setCountry("");
          setTaxId("");
          onDone();
        }
      }}
    >
      <p className="muted">Sus órdenes y facturas son en dólares, sin NCF, ITBIS ni retenciones.</p>
      <Field label="Razón social" required error={fe.errors.legalName}>
        <input value={legalName} onChange={(e) => setLegalName(e.target.value)} />
      </Field>
      <Field label="País (código de 2 letras)" required error={fe.errors.country}>
        <input value={country} maxLength={2} onChange={(e) => setCountry(e.target.value)} />
      </Field>
      <Field label="Identificación fiscal del exterior (opcional)">
        <input value={taxId} maxLength={40} onChange={(e) => setTaxId(e.target.value)} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={create.busy}>
          Crear proveedor del exterior
        </button>
      </div>
      <ErrorBox error={create.error} />
    </form>
  );
}

function SupplierRow({ supplier, onDone, selected, onSelect }: { supplier: Supplier; onDone: () => void; selected?: boolean; onSelect?: (on: boolean) => void }) {
  const { can } = useSession();
  const id = supplier.supplierId;
  const update = useCommand(`update-supplier:${id}`, "/api/v1/companies/{companyId}/master-data/update-supplier", `Proveedor ${supplier.legalName} actualizado.`);
  const updateForeign = useCommand(
    `update-foreign-supplier:${id}`,
    "/api/v1/companies/{companyId}/master-data/update-foreign-supplier-draft",
    `Proveedor ${supplier.legalName} actualizado.`,
  );
  const foreign = supplier.partyKind === "FOREIGN";
  const [country, setCountry] = useState(supplier.country ?? "");
  const [taxId, setTaxId] = useState(supplier.foreignTaxId ?? "");
  const activate = useCommand(`activate-supplier:${id}`, "/api/v1/companies/{companyId}/master-data/activate-supplier", `Proveedor ${supplier.legalName} activado.`);
  const [editing, setEditing] = useState(false);
  const [rnc, setRnc] = useState(supplier.rnc ?? "");
  const [legalName, setLegalName] = useState(supplier.legalName);
  const busy = update.busy || updateForeign.busy || activate.busy;

  return (
    <tr>
      {onSelect ? (
        <td>
          {supplier.status === "DRAFT" ? (
            <input type="checkbox" aria-label={`Seleccionar ${supplier.legalName}`} checked={selected ?? false} onChange={(e) => onSelect(e.target.checked)} />
          ) : null}
        </td>
      ) : null}
      <td>
        {editing && foreign ? (
          <>
            <input aria-label="País" maxLength={2} value={country} onChange={(e) => setCountry(e.target.value)} />
            <input aria-label="Identificación fiscal del exterior" maxLength={40} value={taxId} onChange={(e) => setTaxId(e.target.value)} />
          </>
        ) : editing ? (
          <input aria-label="RNC" value={rnc} onChange={(e) => setRnc(e.target.value)} />
        ) : foreign ? (
          <span title="Proveedor del exterior">{`${supplier.country ?? ""} · ${supplier.foreignTaxId ?? "sin identificación"}`}</span>
        ) : (
          (supplier.rnc ?? "—")
        )}
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
                const saved = foreign
                  ? await updateForeign.run({
                      partyId: id,
                      expectedVersion: supplier.version,
                      legalName: legalName.trim(),
                      country: country.trim().toUpperCase(),
                      foreignTaxId: taxId.trim() || null,
                    })
                  : await update.run({ partyId: id, expectedVersion: supplier.version, rnc: rnc.trim(), legalName: legalName.trim() });
                if (saved) {
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
        <ErrorBox error={update.error ?? updateForeign.error ?? activate.error} />
      </td>
    </tr>
  );
}

function Suppliers() {
  const { companyId, can } = useSession();
  const fromDocument = useSearchParams().get("rnc") ?? "";
  const { data, error, reload } = useLoad(
    can("master_data:read") ? () => allSuppliers(companyId) : null,
    [companyId],
  );

  const [search, setSearch] = useState("");
  const [creating, setCreating] = useState<"LOCAL" | "FOREIGN" | null>(fromDocument ? "LOCAL" : null);
  const [importing, setImporting] = useState(false);
  const [onlyDrafts, setOnlyDrafts] = useState(false);
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set());
  const activateMany = useCommand("activate-suppliers", "/api/v1/companies/{companyId}/master-data/activate-suppliers", (response) => {
    const result = response.result as unknown as { activated: number; skipped: number };
    return `${result.activated} proveedores activados${result.skipped > 0 ? `; ${result.skipped} no estaban en borrador` : ""}.`;
  });
  if (!can("master_data:read")) {
    return <NoPermission />;
  }
  const shown = data?.items.filter((s) => (!onlyDrafts || s.status === "DRAFT") && matchesSearch(search, s.legalName, s.rnc)) ?? [];
  const selectable = can("supplier:activate");
  const drafts = shown.filter((s) => s.status === "DRAFT").map((s) => s.supplierId);
  // Only what is still a draft on screen is sent (the list may have changed since it was ticked); at most 500 per command.
  const chosen = drafts.filter((id) => picked.has(id)).slice(0, 500);
  return (
    <>
      <h1>Proveedores</h1>
      {can("supplier:create") ? (
        creating === "LOCAL" ? (
          <CreateSupplier
            initialRnc={fromDocument}
            onDone={() => {
              setCreating(null);
              reload();
            }}
          />
        ) : creating === "FOREIGN" ? (
          <CreateForeignSupplier
            onDone={() => {
              setCreating(null);
              reload();
            }}
          />
        ) : (
          <div className="actions">
            <button type="button" className="primary" onClick={() => setCreating("LOCAL")}>
              Nuevo proveedor
            </button>
            <button type="button" onClick={() => setCreating("FOREIGN")}>
              Nuevo proveedor del exterior
            </button>
          </div>
        )
      ) : null}
      {can("supplier:import") ? (
        importing ? (
          <PartyImportPanel kind="suppliers" onDone={reload} onClose={() => setImporting(false)} />
        ) : (
          <div className="actions">
            <button type="button" onClick={() => setImporting(true)}>
              Importar desde ADM Cloud
            </button>
          </div>
        )
      ) : null}
      <div className="inline-form" role="search">
        <Field label="Buscar proveedor">
          <input type="search" placeholder="Razón social o RNC" value={search} onChange={(e) => setSearch(e.target.value)} />
        </Field>
        <label className="field">
          <span>Solo borradores</span>
          <input type="checkbox" checked={onlyDrafts} onChange={(e) => setOnlyDrafts(e.target.checked)} />
        </label>
      </div>
      {selectable && drafts.length > 0 ? (
        <div className="actions" data-testid="supplier-batch">
          <button type="button" onClick={() => setPicked(new Set(chosen.length === Math.min(drafts.length, 500) ? [] : drafts.slice(0, 500)))}>
            {chosen.length === Math.min(drafts.length, 500) ? "Quitar selección" : `Seleccionar los ${Math.min(drafts.length, 500)} borradores`}
          </button>
          <ConfirmAction
            label={`Activar seleccionados (${chosen.length})`}
            className="primary"
            stepUp
            busy={activateMany.busy}
            disabled={chosen.length === 0}
            consequence={`${chosen.length} proveedores pasan de borrador a activos: se les podrá comprar y registrar facturas. Su RNC y razón social ya no se podrán cambiar.`}
            onConfirm={async () => {
              if (await activateMany.run({ partyIds: chosen })) {
                setPicked(new Set());
                reload();
              }
            }}
          />
          <ErrorBox error={activateMany.error} />
        </div>
      ) : null}
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
              {selectable ? <th aria-label="Seleccionar" /> : null}
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
              <SupplierRow
                key={`${s.supplierId}:${s.version}`}
                supplier={s}
                onDone={reload}
                selected={picked.has(s.supplierId)}
                onSelect={
                  selectable
                    ? (on) => {
                        const next = new Set(picked);
                        if (on) {
                          next.add(s.supplierId);
                        } else {
                          next.delete(s.supplierId);
                        }
                        setPicked(next);
                      }
                    : undefined
                }
              />
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Suppliers />
    </Suspense>
  );
}
