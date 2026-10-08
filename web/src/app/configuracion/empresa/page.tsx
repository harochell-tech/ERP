"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmDialog, ErrorBox, Field, Loading, NoPermission, useFieldErrors } from "@/components/ui";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Company = Schemas["CompanyView"];
type Plant = Schemas["CompanyPlantView"];

// UX2-02 (E-UX2-11): Configuración › Empresa. The RNC is shown and never edited (another RNC is another company; md.company_guard).
// The Controller (company:manage, step-up) changes the legal name and the plants' names, each after a confirmation.

function LegalNameForm({ company, onDone }: { company: Company; onDone: () => void }) {
  const update = useCommand<"/api/v1/companies/{companyId}/master-data/update-company-legal-name", { legalName: string }>(
    "update-company-legal-name",
    "/api/v1/companies/{companyId}/master-data/update-company-legal-name",
  );
  // After a step-up the typed name comes back (E-PR18b-6); the user presses again.
  const [legalName, setLegalName] = useState(update.restored?.legalName ?? company.legalName);
  const [confirming, setConfirming] = useState(false);
  const fe = useFieldErrors<"legalName">();
  const trimmed = legalName.trim();
  // The dialog sits outside the form: its own <form method="dialog"> must not bubble a submit into this one.
  return (
    <>
      <form
        noValidate
        onSubmit={(e) => {
          e.preventDefault();
          if (
            fe.check({
              legalName: trimmed === "" ? "Indique la razón social." : trimmed.length > 200 ? "Hasta 200 caracteres." : trimmed === company.legalName && "Es la razón social actual.",
            })
          ) {
            setConfirming(true);
          }
        }}
      >
        <Field label="Razón social" required error={fe.errors.legalName} wide>
          <input value={legalName} maxLength={200} onChange={(e) => setLegalName(e.target.value)} />
        </Field>
        <div className="actions form-actions">
          <button type="submit" className="primary" disabled={update.busy}>
            Guardar razón social
          </button>
        </div>
        <ErrorBox error={update.error} />
      </form>
      <ConfirmDialog
        open={confirming}
        title="¿Cambiar la razón social?"
        confirmLabel="Confirmar: Guardar razón social"
        stepUp
        busy={update.busy}
        onCancel={() => setConfirming(false)}
        onConfirm={async () => {
          setConfirming(false);
          if (await update.run({ legalName: trimmed }, { legalName: trimmed }, `Razón social cambiada a ${trimmed}.`)) {
            onDone();
          }
        }}
      >
        <p>
          La razón social pasa de «{company.legalName}» a «{trimmed}» en las pantallas y documentos que se emitan desde ahora; el RNC {company.rnc} no cambia. El cambio
          queda registrado con el nombre anterior.
        </p>
      </ConfirmDialog>
    </>
  );
}

type Contact = { address: string; tradeName: string; phone: string; email: string };

// E-VS4-03-2: the e-CF issuer's data — the address is required by Alanube; trade name, phone (809-555-1234) and e-mail are optional.
function ContactForm({ company, onDone }: { company: Company; onDone: () => void }) {
  const update = useCommand<"/api/v1/companies/{companyId}/master-data/update-company-contact", Contact>(
    "update-company-contact",
    "/api/v1/companies/{companyId}/master-data/update-company-contact",
  );
  const [v, setV] = useState<Contact>(
    update.restored ?? { address: company.address ?? "", tradeName: company.tradeName ?? "", phone: company.phone ?? "", email: company.email ?? "" },
  );
  const [confirming, setConfirming] = useState(false);
  const fe = useFieldErrors<keyof Contact>();
  const set = (key: keyof Contact) => (e: { target: { value: string } }) => setV({ ...v, [key]: e.target.value });
  const t = { address: v.address.trim(), tradeName: v.tradeName.trim(), phone: v.phone.trim(), email: v.email.trim() };
  return (
    <>
      <form
        noValidate
        onSubmit={(e) => {
          e.preventDefault();
          if (
            fe.check({
              address: t.address === "" ? "Indique la dirección." : t.address.length > 100 && "Hasta 100 caracteres.",
              tradeName: t.tradeName.length > 150 && "Hasta 150 caracteres.",
              phone: t.phone !== "" && !/^[0-9]{3}-[0-9]{3}-[0-9]{4}$/.test(t.phone) && "Escriba el teléfono así: 809-555-1234.",
              email: t.email !== "" && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(t.email) && "Revise el correo.",
            })
          ) {
            setConfirming(true);
          }
        }}
      >
        <Field label="Dirección" required error={fe.errors.address} wide hint="Va en cada e-CF como domicilio del emisor.">
          <input value={v.address} maxLength={100} onChange={set("address")} />
        </Field>
        <Field label="Nombre comercial" error={fe.errors.tradeName}>
          <input value={v.tradeName} maxLength={150} onChange={set("tradeName")} />
        </Field>
        <Field label="Teléfono" error={fe.errors.phone}>
          <input value={v.phone} maxLength={12} inputMode="tel" placeholder="809-555-1234" onChange={set("phone")} />
        </Field>
        <Field label="Correo" error={fe.errors.email}>
          <input value={v.email} maxLength={80} type="email" onChange={set("email")} />
        </Field>
        <div className="actions form-actions">
          <button type="submit" className="primary" disabled={update.busy}>
            Guardar datos del emisor
          </button>
        </div>
        <ErrorBox error={update.error} />
      </form>
      <ConfirmDialog
        open={confirming}
        title="¿Guardar los datos del emisor?"
        confirmLabel="Confirmar: Guardar datos del emisor"
        stepUp
        busy={update.busy}
        onCancel={() => setConfirming(false)}
        onConfirm={async () => {
          setConfirming(false);
          const body = { address: t.address, tradeName: t.tradeName || null, phone: t.phone || null, email: t.email || null };
          if (await update.run(body, v, "Datos del emisor guardados.")) {
            onDone();
          }
        }}
      >
        <p>Los e-CF que se emitan desde ahora llevan esta dirección{t.tradeName ? `, el nombre comercial «${t.tradeName}»` : ""} y estos datos de contacto. El cambio queda registrado.</p>
      </ConfirmDialog>
    </>
  );
}

function PlantRow({ plant, canManage, onDone }: { plant: Plant; canManage: boolean; onDone: () => void }) {
  const update = useCommand<"/api/v1/companies/{companyId}/master-data/update-plant-name", { name: string }>(
    `update-plant-name:${plant.plantId}`,
    "/api/v1/companies/{companyId}/master-data/update-plant-name",
  );
  const [name, setName] = useState(update.restored?.name ?? plant.name ?? "");
  const [editing, setEditing] = useState(update.wasRestored);
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const trimmed = name.trim();
  return (
    <tr>
      <td className="mono">{plant.code}</td>
      <td className="wrap">
        {editing ? (
          <form
            className="inline-form"
            noValidate
            onSubmit={(e) => {
              e.preventDefault();
              const message = trimmed === "" ? "Indique el nombre." : trimmed.length > 100 ? "Hasta 100 caracteres." : trimmed === (plant.name ?? "") ? "Es el nombre actual." : null;
              setError(message);
              if (!message) {
                setConfirming(true);
              }
            }}
          >
            <Field label={`Nombre de la planta ${plant.code}`} required error={error}>
              <input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
            </Field>
            <button type="submit" className="primary" disabled={update.busy}>
              Guardar nombre
            </button>
            <button type="button" onClick={() => setEditing(false)}>
              Cancelar
            </button>
          </form>
        ) : (
          <span data-testid={`plant-name:${plant.code}`}>{plant.name ?? <span className="muted">Sin nombre</span>}</span>
        )}
        <ConfirmDialog
          open={confirming}
          title={`¿Cambiar el nombre de la planta ${plant.code}?`}
          confirmLabel="Confirmar: Guardar nombre"
          stepUp
          busy={update.busy}
          onCancel={() => setConfirming(false)}
          onConfirm={async () => {
            setConfirming(false);
            if (await update.run({ plantId: plant.plantId, name: trimmed }, { name: trimmed }, `Planta ${plant.code} renombrada: ${trimmed}.`)) {
              setEditing(false);
              onDone();
            }
          }}
        >
          <p>
            La planta {plant.code} se llamará «{trimmed}» en todas las pantallas{plant.name ? ` (antes «${plant.name}»)` : ""}. El código no cambia y el cambio queda
            registrado.
          </p>
        </ConfirmDialog>
        <ErrorBox error={update.error} />
      </td>
      <td>
        {canManage && !editing ? (
          <button type="button" onClick={() => setEditing(true)}>
            Cambiar nombre
          </button>
        ) : null}
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can, reload: reloadSession } = useSession();
  const allowed = can("configuration:read");
  const canManage = can("company:manage");
  const { data, error, reload } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/master-data/company", { path: { companyId } }) : null, [companyId]);

  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  // The header and the plant names across the screens come from the session: read it again after a change.
  const done = () => {
    reload();
    reloadSession();
  };
  return (
    <>
      <h1>Empresa</h1>
      <dl className="facts">
        <dt>RNC</dt>
        <dd>
          <span className="mono" data-testid="company-rnc">
            {data.rnc}
          </span>
          <div className="muted" style={{ fontSize: 13 }}>
            El RNC identifica a la empresa y no se cambia: otro RNC es otra empresa.
          </div>
        </dd>
        <dt>Razón social</dt>
        <dd data-testid="company-legal-name">{data.legalName}</dd>
      </dl>
      {canManage ? (
        <section className="card">
          <h2 style={{ marginTop: 0 }}>Cambiar la razón social</h2>
          <LegalNameForm key={data.legalName} company={data} onDone={done} />
        </section>
      ) : null}
      <h2>Datos del emisor de e-CF</h2>
      <dl className="facts">
        <dt>Dirección</dt>
        <dd data-testid="company-address">{data.address ?? <span className="muted">Sin dirección: los e-CF no se pueden emitir por Alanube hasta completarla.</span>}</dd>
        <dt>Nombre comercial</dt>
        <dd>{data.tradeName ?? "—"}</dd>
        <dt>Teléfono</dt>
        <dd>{data.phone ?? "—"}</dd>
        <dt>Correo</dt>
        <dd>{data.email ?? "—"}</dd>
      </dl>
      {canManage ? (
        <section className="card">
          <h2 style={{ marginTop: 0 }}>Cambiar los datos del emisor</h2>
          <ContactForm key={`${data.address ?? ""}|${data.tradeName ?? ""}|${data.phone ?? ""}|${data.email ?? ""}`} company={data} onDone={done} />
        </section>
      ) : null}
      <h2>Plantas</h2>
      <p className="muted">Cada planta se muestra en las pantallas como «Nombre (CÓDIGO)». Para abrir una planta nueva o una ubicación, pídalo al equipo de sistemas.</p>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Código</th>
              <th>Nombre</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.plants.map((p) => (
              <PlantRow key={`${p.plantId}:${p.name ?? ""}`} plant={p} canManage={canManage} onDone={done} />
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
