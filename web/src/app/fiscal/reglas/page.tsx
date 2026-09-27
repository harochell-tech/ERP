"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
import { CASES_TEMPLATE, DEFINITION_TEMPLATES, FISCAL_KIND_LABELS, FISCAL_RULE_KINDS, parseJson, type FiscalRuleKind } from "@/lib/configuration";
import { formatDate, formatDateTime, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type RuleVersion = Schemas["FiscalRuleVersionView"];
type Source = Schemas["FiscalSourceView"];
type TestCase = Schemas["FiscalTestCase"];

// E-B03-15-3/4: the fiscal gate in the UI. The analyst configures a version (JSON definition from a template), links a source
// and runs the regression cases; the specialist (someone else, step-up) activates a READY version. The database enforces the gate.

function ConfigureVersion({ onDone }: { onDone: () => void }) {
  const configure = useCommand("configure-fiscal-rule", "/api/v1/companies/{companyId}/tax/configure-fiscal-rule-version");
  const [ruleCode, setRuleCode] = useState("");
  const [ruleKind, setRuleKind] = useState<FiscalRuleKind>("PURCHASE_ITBIS");
  const [definition, setDefinition] = useState(DEFINITION_TEMPLATES.PURCHASE_ITBIS);
  const [effectiveFrom, setEffectiveFrom] = useState("");
  const parsed = parseJson<unknown>(definition);

  return (
    <form
      onSubmit={async (e) => {
        e.preventDefault();
        if (parsed.error === null && (await configure.run({ ruleCode: ruleCode.trim(), ruleKind, definition, effectiveFrom }))) {
          onDone();
        }
      }}
    >
      <Field label="Código de la regla">
        <input value={ruleCode} onChange={(e) => setRuleCode(e.target.value)} required placeholder="ITBIS_COMPRAS" />
      </Field>
      <Field label="Tipo">
        <select
          value={ruleKind}
          onChange={(e) => {
            const kind = e.target.value as FiscalRuleKind;
            setRuleKind(kind);
            setDefinition(DEFINITION_TEMPLATES[kind]);
          }}
        >
          {FISCAL_RULE_KINDS.map((k) => (
            <option key={k} value={k}>
              {FISCAL_KIND_LABELS[k]}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Definición (JSON; confirme la tasa contra la fuente oficial)">
        <textarea rows={7} cols={70} value={definition} onChange={(e) => setDefinition(e.target.value)} />
      </Field>
      {parsed.error ? <p className="error">{parsed.error}</p> : null}
      <Field label="Vigente desde">
        <input type="date" value={effectiveFrom} onChange={(e) => setEffectiveFrom(e.target.value)} required />
      </Field>
      <button type="submit" disabled={configure.busy || parsed.error !== null}>
        Configurar versión
      </button>
      <ErrorBox error={configure.error} />
    </form>
  );
}

function VersionActions({ version, sources, onDone }: { version: RuleVersion; sources: readonly Source[]; onDone: () => void }) {
  const { can, state } = useSession();
  const id = version.ruleVersionId;
  // The activator is never the configurer (E-PR03-4 a); the screen does not offer it.
  const configuredByMe = state.status === "ready" && version.configuredBy === state.session.email;
  const link = useCommand(`link-source:${id}`, "/api/v1/companies/{companyId}/tax/link-fiscal-source");
  const test = useCommand(`run-tests:${id}`, "/api/v1/companies/{companyId}/tax/run-fiscal-rule-tests");
  const activate = useCommand(`activate-rule:${id}`, "/api/v1/companies/{companyId}/tax/activate-fiscal-rule-version");
  const unlinked = sources.filter((s) => !version.sources.some((l) => l.sourceId === s.sourceId));
  const [sourceId, setSourceId] = useState(unlinked[0]?.sourceId ?? "");
  const [cases, setCases] = useState(CASES_TEMPLATE);
  const parsed = parseJson<TestCase[]>(cases);
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
      {can("fiscal_rule:configure") ? (
        <details>
          <summary>Correr pruebas de regresión</summary>
          <textarea aria-label="Casos de prueba" rows={10} cols={70} value={cases} onChange={(e) => setCases(e.target.value)} />
          {parsed.error ? <p className="error">{parsed.error}</p> : null}
          <button type="button" disabled={test.busy || parsed.value === null} onClick={async () => parsed.value && done(await test.run({ ruleVersionId: id, cases: parsed.value }))}>
            Correr pruebas
          </button>
        </details>
      ) : null}
      {version.status === "READY" && can("fiscal_rule:activate") && !configuredByMe ? (
        <button type="button" disabled={activate.busy} onClick={async () => done(await activate.run({ ruleVersionId: id }))}>
          Activar versión
        </button>
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
                    <td>{statusLabel(v.status)}</td>
                    <td>
                      {formatDate(v.effectiveFrom)}
                      {v.effectiveTo ? ` – ${formatDate(v.effectiveTo)}` : ""}
                    </td>
                    <td>
                      <code>{v.definition}</code>
                    </td>
                    <td>{v.sources.length === 0 ? "—" : v.sources.map((s) => s.documentTitle).join(", ")}</td>
                    <td>
                      {v.latestTestRun
                        ? `${v.latestTestRun.passed ? "Pasó" : "Falló"} (${v.latestTestRun.cases} casos, ${formatDateTime(v.latestTestRun.executedAt)})`
                        : "—"}
                    </td>
                    <td>
                      {v.configuredBy ?? "—"} / {v.activatedBy ?? "—"}
                    </td>
                    <td>
                      <VersionActions version={v} sources={sources.data?.items ?? []} onDone={rules.reload} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>
        ))
      )}
    </>
  );
}
