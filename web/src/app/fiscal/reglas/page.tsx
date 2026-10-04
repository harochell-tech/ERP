"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ConfirmAction, ErrorBox, Field, FieldMessage, fieldAria, LineTable, Loading, NoPermission, StatusBadge, SuffixInput, useFieldErrors } from "@/components/ui";
import { DEFINITION_HELP, DEFINITION_TEMPLATES, FISCAL_KIND_LABELS, FISCAL_RULE_KINDS, parseJson, ruleKindRunsTests, type FiscalRuleKind } from "@/lib/configuration";
import {
  buildFiscalDefinition,
  describeFiscalDefinition,
  effectsFor,
  initialCases,
  ISR_WITHHOLDING_TYPES,
  isrWithholdingTypeLabel,
  ITEM_CATEGORIES,
  itemCategoryLabel,
  parseFiscalDefinition,
  PARTY_TYPE_LABELS,
  RAW_MATERIAL_CATEGORIES,
  rowsToCases,
  TAX_EFFECT_LABELS,
  TAX_TYPE_EFFECTS,
  validateCases,
  validateFiscalForm,
  WITHHOLDING_BASE_LABELS,
  WITHHOLDING_PARTY_TYPES,
  WITHHOLDING_SCOPE_LABELS,
  type CaseRow,
  type FiscalRuleForm,
  type TaxComponentRow,
} from "@/lib/fiscalRuleForm";
import { GOODS_TYPES } from "@/lib/fiscalReports";
import { formatDate, formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type RuleVersion = Schemas["FiscalRuleVersionView"];
type Source = Schemas["FiscalSourceView"];

// E-B03-15-3/4 / UX2-02 (E-UX2-8): the fiscal gate in the UI. The analyst configures a version with a guided form per kind (the rate
// in %, effect, exempt categories, party types, base, ISR withholding type, 606 classes) that builds exactly the JSON the Tax Engine
// accepts — kept visible and editable under "Ver JSON (avanzado)" — links a source and runs the regression cases from a table; the
// specialist (someone else, step-up) activates a READY version. The database enforces the gate.

function templateForm(kind: FiscalRuleKind): FiscalRuleForm {
  const parsed = parseFiscalDefinition(kind, DEFINITION_TEMPLATES[kind]);
  // The templates always parse; the fallback only satisfies the type.
  return parsed.form ?? { taxCode: "", ratePercent: "", effect: "", exemptItemCategories: [], base: "", partyTypes: [], isrWithholdingType: "", classes: {}, amount: "" };
}

function toggle(list: readonly string[], value: string, on: boolean): string[] {
  return on ? [...list, value] : list.filter((v) => v !== value);
}

function CheckGroup({
  legend,
  options,
  selected,
  onChange,
  error,
  id,
}: {
  legend: string;
  options: readonly { code: string; label: string }[];
  selected: readonly string[];
  onChange: (next: string[]) => void;
  error?: string;
  id: string;
}) {
  return (
    <fieldset className="checks" aria-describedby={error ? id : undefined} aria-invalid={error ? true : undefined}>
      <legend>{legend}</legend>
      {options.map((o) => (
        <label key={o.code}>
          <input type="checkbox" checked={selected.includes(o.code)} onChange={(e) => onChange(toggle(selected, o.code, e.target.checked))} />
          {o.label}
        </label>
      ))}
      <FieldMessage id={id} error={error} />
    </fieldset>
  );
}

function GuidedFields({ kind, form, onChange, errors }: { kind: FiscalRuleKind; form: FiscalRuleForm; onChange: (form: FiscalRuleForm) => void; errors: Partial<Record<string, string>> }) {
  const set = (patch: Partial<FiscalRuleForm>) => onChange({ ...form, ...patch });
  if (kind === "CONSUMER_ID_THRESHOLD") {
    // CF1-05 (E-CF1-05-7): one amount, the accountant's, with its official source linked afterwards.
    return (
      <Field label="Monto desde el cual se identifica al comprador (RD$, con ITBIS)" required error={errors.amount}>
        <input aria-label="Monto de identificación del consumidor" inputMode="decimal" value={form.amount} onChange={(e) => set({ amount: e.target.value })} />
      </Field>
    );
  }
  if (kind === "PURCHASE_TAX_TYPE") {
    return <TaxTypeFields form={form} onChange={onChange} errors={errors} />;
  }
  if (kind === "REPORT_606_CLASSIFICATION") {
    return (
      <>
        {RAW_MATERIAL_CATEGORIES.map((category) => (
          <Field key={category} label={`Tipo del 606 para ${itemCategoryLabel(category).toLowerCase()}`} required error={errors[`class-${category}`]}>
            <select
              aria-label={`Tipo del 606 para ${itemCategoryLabel(category).toLowerCase()}`}
              value={form.classes[category] ?? ""}
              onChange={(e) => set({ classes: { ...form.classes, [category]: e.target.value } })}
            >
              <option value="">Elegir…</option>
              {Object.entries(GOODS_TYPES).map(([code, name]) => (
                <option key={code} value={code}>
                  {code} {name}
                </option>
              ))}
            </select>
          </Field>
        ))}
      </>
    );
  }
  return (
    <>
      <Field label="Código del impuesto" required error={errors.taxCode} hint="Mayúsculas, dígitos y guion bajo: ITBIS, RET_ITBIS, RET_ISR…">
        <input value={form.taxCode} onChange={(e) => set({ taxCode: e.target.value.toUpperCase() })} />
      </Field>
      <Field label="Tasa (%)" required error={errors.ratePercent} hint="En porcentaje: 18 = 18 %. Confírmela contra la fuente oficial.">
        <SuffixInput suffix="%" value={form.ratePercent} placeholder="18" onChange={(v) => set({ ratePercent: v })} />
      </Field>
      {kind === "PURCHASE_WITHHOLDING" ? (
        <>
          <Field label="Base de la retención" required error={errors.base}>
            <select aria-label="Base de la retención" value={form.base} onChange={(e) => set({ base: e.target.value })}>
              <option value="">Elegir…</option>
              {Object.entries(WITHHOLDING_BASE_LABELS).map(([code, label]) => (
                <option key={code} value={code}>
                  {label}
                </option>
              ))}
            </select>
          </Field>
          <CheckGroup
            id="party-types-message"
            legend="Se retiene a"
            options={WITHHOLDING_PARTY_TYPES.map((code) => ({ code, label: PARTY_TYPE_LABELS[code] ?? code }))}
            selected={form.partyTypes}
            onChange={(partyTypes) => set({ partyTypes })}
            error={errors.partyTypes}
          />
          <Field
            label="Tipo de retención de ISR (606)"
            error={errors.isrWithholdingType}
            hint={form.base === "NET" ? "Con base en el monto neto la retención es de ISR: indique su tipo (campo 17 del 606). Sin él, el 606 lo deja en blanco y TAX-606 lo advierte." : "Solo para retenciones de ISR (base en el monto neto)."}
          >
            <select aria-label="Tipo de retención de ISR (606)" value={form.isrWithholdingType} onChange={(e) => set({ isrWithholdingType: e.target.value })}>
              <option value="">Ninguno</option>
              {ISR_WITHHOLDING_TYPES.map((t) => (
                <option key={t} value={t}>
                  {isrWithholdingTypeLabel(t)}
                </option>
              ))}
            </select>
          </Field>
          <CheckGroup
            id="applies-to-message"
            legend="Aplica a (ninguna marcada = a todas las líneas)"
            options={Object.entries(WITHHOLDING_SCOPE_LABELS).map(([code, label]) => ({ code, label }))}
            selected={form.appliesTo ?? []}
            onChange={(appliesTo) => set({ appliesTo })}
          />
        </>
      ) : (
        <>
          <Field label="Efecto" required error={errors.effect}>
            <select aria-label="Efecto" value={form.effect} onChange={(e) => set({ effect: e.target.value })}>
              <option value="">Elegir…</option>
              {effectsFor(kind).map((code) => (
                <option key={code} value={code}>
                  {TAX_EFFECT_LABELS[code] ?? code}
                </option>
              ))}
            </select>
          </Field>
          <CheckGroup
            id="exempt-message"
            legend="Categorías exentas"
            options={ITEM_CATEGORIES}
            selected={form.exemptItemCategories}
            onChange={(exemptItemCategories) => set({ exemptItemCategories })}
          />
        </>
      )}
    </>
  );
}

/** GAS1-07 (E-GAS-07-5): a purchase tax type — its name in the lines' list and the taxes it charges on the net. */
function TaxTypeFields({ form, onChange, errors }: { form: FiscalRuleForm; onChange: (form: FiscalRuleForm) => void; errors: Partial<Record<string, string>> }) {
  const components = form.components ?? [];
  const setComponents = (next: TaxComponentRow[]) => onChange({ ...form, components: next });
  const setRow = (i: number, patch: Partial<TaxComponentRow>) => setComponents(components.map((c, k) => (k === i ? { ...c, ...patch } : c)));
  return (
    <>
      <Field label="Nombre del tipo" required error={errors.label} hint="Como se verá en la lista de cada línea: «ITBIS 18 %», «Telecomunicaciones», «Exento».">
        <input aria-label="Nombre del tipo" maxLength={60} value={form.label ?? ""} onChange={(e) => onChange({ ...form, label: e.target.value })} />
      </Field>
      <LineTable testId="tax-type-components">
        <thead>
          <tr>
            <th>Impuesto</th>
            <th>Tasa (%)</th>
            <th>Va a</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {components.length === 0 ? (
            <tr>
              <td colSpan={4} className="muted">
                Sin impuestos: el tipo es exento.
              </td>
            </tr>
          ) : null}
          {components.map((c, i) => (
            <tr key={i}>
              <td>
                <input
                  aria-label={`Impuesto ${i + 1}`}
                  value={c.taxCode}
                  onChange={(e) => setRow(i, { taxCode: e.target.value.toUpperCase() })}
                  {...fieldAria(errors[`component-${i}-taxCode`], `component-${i}-taxCode-message`, true)}
                />
                <FieldMessage id={`component-${i}-taxCode-message`} error={errors[`component-${i}-taxCode`]} />
              </td>
              <td>
                <SuffixInput suffix="%" value={c.ratePercent} placeholder="18" onChange={(v) => setRow(i, { ratePercent: v })} />
                <FieldMessage id={`component-${i}-ratePercent-message`} error={errors[`component-${i}-ratePercent`]} />
              </td>
              <td>
                <select aria-label={`Va a ${i + 1}`} value={c.effect} onChange={(e) => setRow(i, { effect: e.target.value })}>
                  <option value="">Elegir…</option>
                  {TAX_TYPE_EFFECTS.map((code) => (
                    <option key={code} value={code}>
                      {TAX_EFFECT_LABELS[code] ?? code}
                    </option>
                  ))}
                </select>
                <FieldMessage id={`component-${i}-effect-message`} error={errors[`component-${i}-effect`]} />
              </td>
              <td>
                <button type="button" onClick={() => setComponents(components.filter((_, k) => k !== i))}>
                  Quitar
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </LineTable>
      <div className="actions">
        <button type="button" onClick={() => setComponents([...components, { taxCode: "", ratePercent: "", effect: "" }])}>
          Agregar impuesto
        </button>
      </div>
    </>
  );
}

function ConfigureVersion({ onDone }: { onDone: () => void }) {
  const configure = useCommand("configure-fiscal-rule", "/api/v1/companies/{companyId}/tax/configure-fiscal-rule-version", (r) => {
    const result = r.result as unknown as { ruleCode?: string; version?: number } | null;
    return result?.ruleCode ? `Regla ${result.ruleCode} versión ${result.version ?? ""} configurada.` : "Versión de la regla fiscal configurada.";
  });
  const [ruleCode, setRuleCode] = useState("");
  const [ruleKind, setRuleKind] = useState<FiscalRuleKind>("PURCHASE_ITBIS");
  const [form, setForm] = useState(() => templateForm("PURCHASE_ITBIS"));
  const [definition, setDefinition] = useState(DEFINITION_TEMPLATES.PURCHASE_ITBIS);
  // True while the JSON was typed by hand and the form cannot show it; the JSON is then what is sent.
  const [jsonAhead, setJsonAhead] = useState(false);
  const [effectiveFrom, setEffectiveFrom] = useState("");
  const parsedJson = parseFiscalDefinition(ruleKind, definition);
  const fe = useFieldErrors();

  const changeForm = (next: FiscalRuleForm) => {
    setForm(next);
    setDefinition(buildFiscalDefinition(ruleKind, next));
    setJsonAhead(false);
  };

  return (
    <form
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const formErrors = jsonAhead ? {} : validateFiscalForm(ruleKind, form);
        const valid = fe.check({
          ruleCode: ruleCode.trim() === "" && "Indique el código de la regla.",
          effectiveFrom: !effectiveFrom && "Indique desde cuándo rige.",
          definition: parseJson(definition).error,
          ...formErrors,
        });
        if (valid && (await configure.run({ ruleCode: ruleCode.trim(), ruleKind, definition, effectiveFrom }))) {
          onDone();
        }
      }}
    >
      <Field label="Código de la regla" required error={fe.errors.ruleCode}>
        <input value={ruleCode} onChange={(e) => setRuleCode(e.target.value)} placeholder="ITBIS_COMPRAS" />
      </Field>
      <Field label="Tipo" required>
        <select
          aria-label="Tipo"
          value={ruleKind}
          onChange={(e) => {
            const kind = e.target.value as FiscalRuleKind;
            setRuleKind(kind);
            setForm(templateForm(kind));
            setDefinition(DEFINITION_TEMPLATES[kind]);
            setJsonAhead(false);
          }}
        >
          {FISCAL_RULE_KINDS.map((k) => (
            <option key={k} value={k}>
              {FISCAL_KIND_LABELS[k]}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Vigente desde" required error={fe.errors.effectiveFrom}>
        <input type="date" value={effectiveFrom} onChange={(e) => setEffectiveFrom(e.target.value)} />
      </Field>
      <div>
        <GuidedFields kind={ruleKind} form={form} onChange={changeForm} errors={fe.errors} />
      </div>
      {(DEFINITION_HELP[ruleKind] ?? []).map((line) => (
        <p key={line} className="muted">
          {line}
        </p>
      ))}
      <details>
        <summary>Ver JSON (avanzado)</summary>
        <Field
          label="Definición (JSON)"
          wide
          error={fe.errors.definition ?? (jsonAhead ? parsedJson.error : null)}
          hint="Es lo que se envía. Si lo edita a mano, el formulario se actualiza cuando puede mostrarlo."
        >
          <textarea
            className="mono"
            rows={8}
            value={definition}
            onChange={(e) => {
              const text = e.target.value;
              setDefinition(text);
              const parsed = parseFiscalDefinition(ruleKind, text);
              if (parsed.form) {
                setForm(parsed.form);
                setJsonAhead(false);
              } else {
                setJsonAhead(true);
              }
            }}
          />
        </Field>
      </details>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={configure.busy}>
          Configurar versión
        </button>
      </div>
      <ErrorBox error={configure.error} />
    </form>
  );
}

const PARTY_OPTIONS = Object.entries(PARTY_TYPE_LABELS);
const EFFECT_OPTIONS = Object.entries(TAX_EFFECT_LABELS);

/** E-UX2-8: the regression cases as a table; it produces the same `cases` payload as the former JSON. */
function CasesEditor({ rows, onChange, errors }: { rows: CaseRow[]; onChange: (rows: CaseRow[]) => void; errors: Record<string, string> }) {
  const setRow = (i: number, patch: Partial<CaseRow>) => onChange(rows.map((r, k) => (k === i ? { ...r, ...patch } : r)));
  const cell = (key: string) => ({ ...fieldAria(errors[key], `${key}-message`, true) });
  return (
    <>
      <LineTable testId="cases-table">
        <thead>
          <tr>
            <th>Caso</th>
            <th>Contraparte</th>
            <th>Categoría</th>
            <th>Neto</th>
            <th>ITBIS facturado</th>
            <th>Impuestos esperados</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i}>
              <td>
                <input aria-label={`Caso ${i + 1}: nombre`} value={r.caseId} onChange={(e) => setRow(i, { caseId: e.target.value })} {...cell(`case-${i}-caseId`)} />
                <FieldMessage id={`case-${i}-caseId-message`} error={errors[`case-${i}-caseId`]} />
              </td>
              <td>
                <select aria-label={`Caso ${i + 1}: contraparte`} value={r.partyType} onChange={(e) => setRow(i, { partyType: e.target.value })} {...cell(`case-${i}-partyType`)}>
                  <option value="">Elegir…</option>
                  {PARTY_OPTIONS.map(([code, label]) => (
                    <option key={code} value={code}>
                      {label}
                    </option>
                  ))}
                </select>
                <FieldMessage id={`case-${i}-partyType-message`} error={errors[`case-${i}-partyType`]} />
              </td>
              <td>
                <select aria-label={`Caso ${i + 1}: categoría`} value={r.itemCategory} onChange={(e) => setRow(i, { itemCategory: e.target.value })} {...cell(`case-${i}-itemCategory`)}>
                  <option value="">Elegir…</option>
                  {ITEM_CATEGORIES.map((c) => (
                    <option key={c.code} value={c.code}>
                      {c.label}
                    </option>
                  ))}
                </select>
                <FieldMessage id={`case-${i}-itemCategory-message`} error={errors[`case-${i}-itemCategory`]} />
              </td>
              <td>
                <input aria-label={`Caso ${i + 1}: neto`} inputMode="decimal" value={r.netAmount} onChange={(e) => setRow(i, { netAmount: e.target.value })} {...cell(`case-${i}-netAmount`)} />
                <FieldMessage id={`case-${i}-netAmount-message`} error={errors[`case-${i}-netAmount`]} />
              </td>
              <td>
                <input aria-label={`Caso ${i + 1}: ITBIS facturado`} inputMode="decimal" value={r.itbisAmount} onChange={(e) => setRow(i, { itbisAmount: e.target.value })} {...cell(`case-${i}-itbisAmount`)} />
                <FieldMessage id={`case-${i}-itbisAmount-message`} error={errors[`case-${i}-itbisAmount`]} />
              </td>
              <td>
                {r.expected.length === 0 ? <div className="muted">Ninguno (no aplica impuesto)</div> : null}
                {r.expected.map((x, j) => {
                  const setExpected = (patch: Partial<CaseRow["expected"][number]>) => setRow(i, { expected: r.expected.map((y, k) => (k === j ? { ...y, ...patch } : y)) });
                  const key = `case-${i}-expected-${j}`;
                  return (
                    <div key={j} className="inline-form" style={{ marginBottom: 6 }}>
                      <input aria-label={`Caso ${i + 1}, impuesto ${j + 1}: código`} style={{ width: 110 }} value={x.taxCode} onChange={(e) => setExpected({ taxCode: e.target.value.toUpperCase() })} {...cell(`${key}-taxCode`)} />
                      <input aria-label={`Caso ${i + 1}, impuesto ${j + 1}: monto`} style={{ width: 110 }} inputMode="decimal" placeholder="Monto" value={x.amount} onChange={(e) => setExpected({ amount: e.target.value })} {...cell(`${key}-amount`)} />
                      <select aria-label={`Caso ${i + 1}, impuesto ${j + 1}: efecto`} value={x.effect} onChange={(e) => setExpected({ effect: e.target.value })} {...cell(`${key}-effect`)}>
                        <option value="">Efecto…</option>
                        {EFFECT_OPTIONS.map(([code, label]) => (
                          <option key={code} value={code}>
                            {label}
                          </option>
                        ))}
                      </select>
                      <button type="button" className="link" onClick={() => setRow(i, { expected: r.expected.filter((_, k) => k !== j) })}>
                        Quitar
                      </button>
                      <FieldMessage id={`${key}-taxCode-message`} error={errors[`${key}-taxCode`]} />
                      <FieldMessage id={`${key}-amount-message`} error={errors[`${key}-amount`]} />
                      <FieldMessage id={`${key}-effect-message`} error={errors[`${key}-effect`]} />
                    </div>
                  );
                })}
                <button type="button" className="link" onClick={() => setRow(i, { expected: [...r.expected, { taxCode: "", amount: "", effect: "" }] })}>
                  Agregar impuesto
                </button>
              </td>
              <td>
                <button type="button" className="link" onClick={() => onChange(rows.filter((_, k) => k !== i))}>
                  Quitar caso
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </LineTable>
      <button
        type="button"
        onClick={() =>
          onChange([...rows, { caseId: `caso-${rows.length + 1}`, partyType: "COMPANY", itemCategory: "CEMENTO", netAmount: "", itbisAmount: "", expected: [] }])
        }
      >
        Agregar caso
      </button>
    </>
  );
}

function VersionActions({ ruleCode, ruleKind, version, sources, onDone }: { ruleCode: string; ruleKind: string; version: RuleVersion; sources: readonly Source[]; onDone: () => void }) {
  const { can, isMine } = useSession();
  const id = version.ruleVersionId;
  const label = `${ruleCode} versión ${version.version}`;
  // The activator is never the configurer (E-PR03-4 a); the screen does not offer it. UX1-01b (E-UX1-01-3): configuredBy is the
  // configurer's display name (the e-mail until a sign-in brings it), so both are compared.
  const configuredByMe = isMine(version.configuredBy);
  const link = useCommand(`link-source:${id}`, "/api/v1/companies/{companyId}/tax/link-fiscal-source", `Fuente vinculada a la regla ${label}.`);
  const test = useCommand(`run-tests:${id}`, "/api/v1/companies/{companyId}/tax/run-fiscal-rule-tests", (r) =>
    (r.result as unknown as { passed?: boolean } | null)?.passed ? `Pruebas de la regla ${label}: pasaron.` : `Pruebas de la regla ${label}: fallaron; revise los casos.`,
  );
  const activate = useCommand(`activate-rule:${id}`, "/api/v1/companies/{companyId}/tax/activate-fiscal-rule-version", `Regla ${label} activada.`);
  const unlinked = sources.filter((s) => !version.sources.some((l) => l.sourceId === s.sourceId));
  const [sourceId, setSourceId] = useState(unlinked[0]?.sourceId ?? "");
  const [rows, setRows] = useState<CaseRow[]>(() => initialCases(ruleKind, version.definition));
  const [caseErrors, setCaseErrors] = useState<Record<string, string>>({});
  const pending = version.status === "BLOCKED_PENDING_SOURCE" || version.status === "READY";
  const done = (response: unknown) => response && onDone();

  if (!pending) {
    return null;
  }
  return (
    <div>
      {can("fiscal_rule:configure") && unlinked.length > 0 ? (
        <div className="inline-form">
          <select aria-label="Fuente" value={sourceId} onChange={(e) => setSourceId(e.target.value)}>
            {unlinked.map((s) => (
              <option key={s.sourceId} value={s.sourceId}>
                {s.documentTitle} ({s.environment === "PRODUCTION" ? "oficial" : "prueba"})
              </option>
            ))}
          </select>
          <button type="button" disabled={link.busy || sourceId === ""} onClick={async () => done(await link.run({ ruleVersionId: id, sourceId }))}>
            Vincular fuente
          </button>
        </div>
      ) : null}
      {!ruleKindRunsTests(ruleKind) ? (
        <p className="muted">Sin pruebas de regresión: queda lista para activar con su fuente oficial.</p>
      ) : can("fiscal_rule:configure") ? (
        <details>
          <summary>Correr pruebas de regresión</summary>
          <p className="muted">Escriba cada caso y los impuestos que dicta la fuente oficial; la prueba compara lo que calcula la regla con lo esperado.</p>
          <CasesEditor rows={rows} onChange={setRows} errors={caseErrors} />
          <details>
            <summary>Ver JSON de los casos</summary>
            <pre className="mono">{JSON.stringify(rowsToCases(rows), null, 2)}</pre>
          </details>
          <div className="actions">
            <button
              type="button"
              disabled={test.busy || rows.length === 0}
              onClick={async () => {
                const errors = validateCases(rows, ruleKind);
                setCaseErrors(errors);
                if (Object.keys(errors).length === 0) {
                  done(await test.run({ ruleVersionId: id, cases: rowsToCases(rows) }));
                }
              }}
            >
              Correr pruebas
            </button>
          </div>
        </details>
      ) : null}
      {version.status === "READY" && can("fiscal_rule:activate") && !configuredByMe ? (
        <ConfirmAction
          label="Activar versión"
          className="primary"
          title={`¿Activar la regla ${label}?`}
          consequence={`Desde ${formatDate(version.effectiveFrom)} la regla ${label} determina los impuestos de los documentos que se contabilicen; la versión activa anterior del mismo tipo queda reemplazada. No se deshace: un cambio exige otra versión.`}
          stepUp
          busy={activate.busy}
          onConfirm={async () => done(await activate.run({ ruleVersionId: id }))}
        />
      ) : null}
      <ErrorBox error={link.error ?? test.error ?? activate.error} />
    </div>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("configuration:read");
  const rules = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/tax/fiscal-rules", { path: { companyId } }) : null, [companyId]);
  const sources = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/tax/fiscal-sources", { path: { companyId } }) : null, [companyId]);

  if (!allowed) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Reglas fiscales</h1>
      {can("fiscal_rule:configure") ? (
        <details>
          <summary>Configurar una versión</summary>
          <ConfigureVersion onDone={rules.reload} />
        </details>
      ) : null}
      {rules.data === null || sources.data === null ? (
        <Loading error={rules.error ?? sources.error} />
      ) : rules.data.items.length === 0 ? (
        <p className="muted">No hay reglas configuradas.</p>
      ) : (
        rules.data.items.map((rule) => (
          <section key={rule.ruleId}>
            <h2>
              {rule.code} — {FISCAL_KIND_LABELS[rule.ruleKind] ?? rule.ruleKind}
            </h2>
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th className="num">Versión</th>
                    <th>Estado</th>
                    <th>Vigencia</th>
                    <th>Definición</th>
                    <th>Fuentes</th>
                    <th>Última prueba</th>
                    <th>Configurada / activada por</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {rule.versions.map((v) => (
                    <tr key={`${v.ruleVersionId}:${v.rowVersion}`}>
                      <td className="num">{v.version}</td>
                      <td>
                        <StatusBadge status={v.status} />
                      </td>
                      <td>
                        {formatDate(v.effectiveFrom)}
                        {v.effectiveTo ? ` – ${formatDate(v.effectiveTo)}` : ""}
                      </td>
                      <td className="wrap" style={{ minWidth: 240 }}>
                        <ul className="plain-list" data-testid={`definition:${rule.code}:${v.version}`}>
                          {describeFiscalDefinition(rule.ruleKind, v.definition).map((line) => (
                            <li key={line}>{line}</li>
                          ))}
                        </ul>
                        <details>
                          <summary>Ver JSON</summary>
                          <code>{v.definition}</code>
                        </details>
                      </td>
                      <td>{v.sources.length === 0 ? "—" : v.sources.map((s) => s.documentTitle).join(", ")}</td>
                      <td>
                        {v.latestTestRun
                          ? `${v.latestTestRun.passed ? "Pasó" : "Falló"} (${v.latestTestRun.cases} casos, ${formatDateTime(v.latestTestRun.executedAt)})`
                          : ruleKindRunsTests(rule.ruleKind)
                            ? "—"
                            : "No aplica"}
                      </td>
                      <td>
                        {v.configuredBy ?? "—"} / {v.activatedBy ?? "—"}
                      </td>
                      <td>
                        <VersionActions ruleCode={rule.code} ruleKind={rule.ruleKind} version={v} sources={sources.data?.items ?? []} onDone={rules.reload} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        ))
      )}
    </>
  );
}
