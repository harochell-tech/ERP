"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, NoPermission, StatusBadge } from "@/components/ui";
import { isDecimal, normalizeInput } from "@/lib/decimal";
import { parameterText } from "@/lib/lab";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// LAB1-01 (E-LAB1-01-3/11): Calidad › Parámetros. The lab's control values (maximum CV, minimum specimens, the age that counts as
// 28 days, the kg/cm² → MPa conversion, density classes and absorption limits, the initial age factors, the press) start with the
// validated Excel's values; Calidad (lab_spec:manage) changes them and the history says who changed what. Failure types below.

type Parameter = Schemas["LabParameterView"];

function ParameterRow({ parameter, canManage, onDone }: { parameter: Parameter; canManage: boolean; onDone: () => void }) {
  const save = useCommand(`lab-parameter:${parameter.code}`, "/api/v1/companies/{companyId}/manufacturing/set-lab-parameter", `${parameter.name}: guardado.`);
  const current = parameterText(parameter);
  const [value, setValue] = useState(current);
  const isNumber = parameter.kind === "NUMBER";
  const typed = isNumber ? normalizeInput(value) : value.trim();
  const valid = isNumber ? isDecimal(typed, parameter.whole ? 0 : 8) : typed.length > 0 && typed.length <= 120;
  return (
    <tr data-testid={`lab-parameter:${parameter.code}`}>
      <td className="wrap">{parameter.name}</td>
      <td>
        {canManage ? (
          <form
            className="inline-form"
            onSubmit={async (e) => {
              e.preventDefault();
              if (await save.run(isNumber ? { code: parameter.code, number: typed, text: null } : { code: parameter.code, number: null, text: typed })) {
                onDone();
              }
            }}
          >
            <input aria-label={parameter.name} inputMode={isNumber ? "decimal" : "text"} value={value} required onChange={(e) => setValue(e.target.value)} />
            <button type="submit" disabled={save.busy || !valid || typed === current}>
              Guardar
            </button>
            <ErrorBox error={save.error} />
          </form>
        ) : (
          current
        )}
      </td>
      <td className="muted">
        {isNumber ? `De ${parameterText({ kind: "NUMBER", number: parameter.min ?? null, text: null })} a ${parameterText({ kind: "NUMBER", number: parameter.max ?? null, text: null })}` : ""}
      </td>
      <td className="muted">{parameter.own ? `Cambiado por ${parameter.setBy ?? ""}` : "Valor inicial"}</td>
    </tr>
  );
}

function FailureTypes({ types, canManage, onDone }: { types: Schemas["FailureTypeView"][]; canManage: boolean; onDone: () => void }) {
  const define = useCommand("define-failure-type", "/api/v1/companies/{companyId}/manufacturing/define-failure-type", "Tipo de falla guardado.");
  const status = useCommand("failure-type-status", "/api/v1/companies/{companyId}/manufacturing/set-failure-type-status");
  const [v, setV] = useState({ code: "", name: "" });
  return (
    <>
      <h2>Tipos de falla</h2>
      {canManage ? (
        <form
          className="inline-form"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await define.run({ code: v.code.trim().toUpperCase(), name: v.name.trim() })) {
              setV({ code: "", name: "" });
              onDone();
            }
          }}
        >
          <Field label="Código del tipo de falla" hint="Use el de uno existente para cambiarle el nombre">
            <input value={v.code} required maxLength={30} placeholder="MIXTA" onChange={(e) => setV({ ...v, code: e.target.value })} />
          </Field>
          <Field label="Nombre del tipo de falla">
            <input value={v.name} required maxLength={80} onChange={(e) => setV({ ...v, name: e.target.value })} />
          </Field>
          <button type="submit" className="primary" disabled={define.busy}>
            Guardar tipo de falla
          </button>
          <ErrorBox error={define.error ?? status.error} />
        </form>
      ) : null}
      <div className="table-wrap">
        <table data-testid="failure-types">
          <thead>
            <tr>
              <th>Código</th>
              <th>Nombre</th>
              <th>Estado</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {types.map((t) => (
              <tr key={t.code} data-testid={`failure-type:${t.code}`}>
                <td className="mono">{t.code}</td>
                <td>{t.name}</td>
                <td>
                  <StatusBadge status={t.status} />
                </td>
                <td className="actions">
                  {canManage ? (
                    <button type="button" disabled={status.busy} onClick={async () => (await status.run({ code: t.code, status: t.status === "ACTIVE" ? "INACTIVE" : "ACTIVE" })) && onDone()}>
                      {t.status === "ACTIVE" ? "Desactivar" : "Activar"}
                    </button>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("lab:read");
  const { data, error, reload } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/manufacturing/lab/settings", { path: { companyId } }) : null, [companyId]);
  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const canManage = can("lab_spec:manage");
  const control = data.parameters.filter((p) => !p.code.startsWith("AGE_FACTOR_"));
  const factors = data.parameters.filter((p) => p.code.startsWith("AGE_FACTOR_"));
  const table = (rows: Parameter[], testId: string) => (
    <div className="table-wrap">
      <table data-testid={testId}>
        <thead>
          <tr>
            <th>Parámetro</th>
            <th>Valor</th>
            <th>Rango</th>
            <th>Origen</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((p) => (
            <ParameterRow key={`${p.code}:${p.number ?? p.text}`} parameter={p} canManage={canManage} onDone={reload} />
          ))}
        </tbody>
      </table>
    </div>
  );
  return (
    <>
      <h1>Parámetros del laboratorio</h1>
      <p className="muted">Valores de control del laboratorio. Empiezan con los del registro de ensayos validado; cada cambio queda en el historial.</p>
      {table(control, "lab-parameters")}
      <h2>Factores de edad iniciales</h2>
      <p className="muted">Resistencia a la edad indicada ÷ resistencia a 28 días. Solo sirven de alerta interna mientras no haya factores propios; nunca para certificar.</p>
      {table(factors, "lab-age-factors")}
      <FailureTypes types={data.failureTypes} canManage={canManage} onDone={reload} />
      <h2>Historial de cambios</h2>
      <div className="table-wrap">
        <table data-testid="lab-parameter-history">
          <thead>
            <tr>
              <th>Fecha</th>
              <th>Parámetro</th>
              <th>Valor</th>
              <th>Quién</th>
            </tr>
          </thead>
          <tbody>
            {data.history.map((h, i) => (
              <tr key={i}>
                <td>{new Date(h.setAt).toLocaleString("es-DO", { timeZone: "America/Santo_Domingo" })}</td>
                <td className="wrap">{h.name}</td>
                <td>{h.value}</td>
                <td>{h.setBy}</td>
              </tr>
            ))}
            {data.history.length === 0 ? (
              <tr>
                <td colSpan={4} className="muted">
                  Nadie ha cambiado un parámetro todavía.
                </td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
    </>
  );
}
