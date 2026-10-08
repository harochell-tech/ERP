"use client";

import { useState } from "react";
import { query } from "@/api/client";
import { ConfirmAction, ErrorBox, Field, Loading, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { ECF_TYPES, SERIES_STATUSES, encfDigits } from "@/lib/ecf";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS4-04 (E-VS4-04-1, E-VS4-01-4): Fiscal › Rangos e-NCF. The Especialista fiscal registers the range the DGII authorized (at the
// cut-over, from the first number the previous provider did not use) and discards a wrong draft; the Controller approves it — the
// type's range in force closes — and closes a range in force. Core never gives a number back.

type Prepare = { ecfType: string; from: string; to: string; validUntil: string; dgiiAuthorization: string };

function PrepareForm({ onDone }: { onDone: () => void }) {
  const prepare = useCommand<"/api/v1/companies/{companyId}/ecf/prepare-ecf-series", Prepare>("prepare-ecf-series", "/api/v1/companies/{companyId}/ecf/prepare-ecf-series");
  const [v, setV] = useState<Prepare>(prepare.restored ?? { ecfType: "31", from: "", to: "", validUntil: "", dgiiAuthorization: "" });
  const fe = useFieldErrors<keyof Prepare>();
  const set = (key: keyof Prepare) => (e: { target: { value: string } }) => setV({ ...v, [key]: e.target.value });
  const from = encfDigits(v.from, v.ecfType);
  const to = encfDigits(v.to, v.ecfType);
  return (
    <form
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        if (
          !fe.check({
            from: !from && `Escriba el primer número: E${v.ecfType} y 10 dígitos, o solo el número.`,
            to: !to ? `Escriba el último número: E${v.ecfType} y 10 dígitos, o solo el número.` : from !== null && to < from && "El último número no puede ser menor que el primero.",
            validUntil: !v.validUntil && "Indique la fecha de vencimiento que dio la DGII.",
            dgiiAuthorization: v.dgiiAuthorization.trim().length > 60 && "Hasta 60 caracteres.",
          })
        ) {
          return;
        }
        const body = {
          ecfType: v.ecfType,
          from: Number(from),
          to: Number(to),
          validUntil: v.validUntil,
          dgiiAuthorization: v.dgiiAuthorization.trim() || null,
        };
        if (await prepare.run(body, v, `Rango E${v.ecfType}${from} a E${v.ecfType}${to} registrado; falta la aprobación del Controller.`)) {
          setV({ ecfType: v.ecfType, from: "", to: "", validUntil: "", dgiiAuthorization: "" });
          onDone();
        }
      }}
    >
      <Field label="Tipo de e-CF" required>
        <select value={v.ecfType} onChange={set("ecfType")}>
          {Object.entries(ECF_TYPES).map(([code, label]) => (
            <option key={code} value={code}>
              {label}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Primer número" required error={fe.errors.from} hint="El primero que el proveedor anterior no usó.">
        <input className="mono" value={v.from} placeholder={`E${v.ecfType}0000000001`} onChange={set("from")} />
      </Field>
      <Field label="Último número" required error={fe.errors.to}>
        <input className="mono" value={v.to} placeholder={`E${v.ecfType}0000001000`} onChange={set("to")} />
      </Field>
      <Field label="Vence el" required error={fe.errors.validUntil}>
        <input type="date" value={v.validUntil} onChange={set("validUntil")} />
      </Field>
      <Field label="Autorización de la DGII" error={fe.errors.dgiiAuthorization}>
        <input value={v.dgiiAuthorization} maxLength={60} onChange={set("dgiiAuthorization")} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={prepare.busy}>
          Registrar rango
        </button>
      </div>
      <ErrorBox error={prepare.error} />
    </form>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("fiscal_report:read");
  const { data, error, reload } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/ecf/series", { path: { companyId } }) : null, [companyId]);
  const approve = useCommand("approve-ecf-series", "/api/v1/companies/{companyId}/ecf/approve-ecf-series", () => "Rango aprobado: es el vigente de su tipo.");
  const discard = useCommand("discard-ecf-series", "/api/v1/companies/{companyId}/ecf/discard-ecf-series", () => "Rango descartado.");
  const close = useCommand("close-ecf-series", "/api/v1/companies/{companyId}/ecf/close-ecf-series", () => "Rango cerrado.");
  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <h1>Rangos e-NCF</h1>
      <p className="muted">
        Los números que la DGII autorizó por tipo de e-CF. Cada factura o nota de crédito toma el siguiente número del rango vigente de su tipo y ese número nunca se reutiliza. Un rango
        nuevo lo registra Fiscal y lo aprueba el Controller; al aprobarlo, el rango vigente de ese tipo se cierra.
      </p>
      {can("ecf_series:prepare") ? (
        <section className="card">
          <h2 style={{ marginTop: 0 }}>Registrar un rango</h2>
          <PrepareForm onDone={reload} />
        </section>
      ) : null}
      <div className="table-wrap">
        <table data-testid="ecf-series">
          <thead>
            <tr>
              <th>Tipo</th>
              <th>Desde</th>
              <th>Hasta</th>
              <th>Próximo</th>
              <th className="num">Quedan</th>
              <th>Vence</th>
              <th>Estado</th>
              <th>Registró / aprobó</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.items.length === 0 ? (
              <tr>
                <td colSpan={9} className="muted">
                  Sin rangos: las facturas siguen por el camino manual hasta que se apruebe el primero.
                </td>
              </tr>
            ) : null}
            {data.items.map((s) => (
              <tr key={s.seriesId} data-testid={`series:${s.from}`}>
                <td>{ECF_TYPES[s.ecfType] ?? s.ecfType}</td>
                <td className="mono">{s.from}</td>
                <td className="mono">{s.to}</td>
                <td className="mono">{s.status === "ACTIVE" ? s.next : "—"}</td>
                <td className="num">{s.status === "ACTIVE" || s.status === "DRAFT" ? s.remaining : "—"}</td>
                <td>{formatDate(s.validUntil)}</td>
                <td>
                  <StatusBadge status={s.status === "ACTIVE" ? "ACTIVE" : s.status === "DRAFT" ? "PENDING_APPROVAL" : "INACTIVE"} label={SERIES_STATUSES[s.status] ?? s.status} />
                </td>
                <td className="wrap">
                  {s.preparedBy ?? "—"}
                  {s.approvedBy ? ` / ${s.approvedBy}` : ""}
                </td>
                <td>
                  {s.status === "DRAFT" && can("ecf_series:approve") ? (
                    <ConfirmAction
                      label="Aprobar"
                      stepUp
                      busy={approve.busy}
                      consequence={`El rango ${s.from} a ${s.to} pasa a ser el vigente de su tipo y el rango vigente anterior se cierra.`}
                      onConfirm={async () => (await approve.run({ seriesId: s.seriesId, expectedVersion: s.version })) && reload()}
                    />
                  ) : null}
                  {s.status === "DRAFT" && can("ecf_series:prepare") ? (
                    <ConfirmAction
                      label="Descartar"
                      busy={discard.busy}
                      consequence="El rango registrado se descarta y no se usa."
                      onConfirm={async () => (await discard.run({ seriesId: s.seriesId, expectedVersion: s.version })) && reload()}
                    />
                  ) : null}
                  {s.status === "ACTIVE" && can("ecf_series:approve") ? (
                    <ConfirmAction
                      label="Cerrar"
                      danger
                      stepUp
                      busy={close.busy}
                      consequence={`No se emiten más e-CF con este rango; sus ${s.remaining} números sin usar quedan para anularlos ante la DGII.`}
                      onConfirm={async () => (await close.run({ seriesId: s.seriesId, expectedVersion: s.version })) && reload()}
                    />
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <ErrorBox error={approve.error ?? discard.error ?? close.error} />
    </>
  );
}
