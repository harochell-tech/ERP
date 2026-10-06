"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, Loading, NoPermission, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { todayInDominicanRepublic } from "@/lib/labels";
import { sha256Hex } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { SearchSelect } from "@/components/SearchSelect";

interface Line {
  accountId: string;
  side: "debit" | "credit";
  amount: string;
  plantId: string;
  memo: string;
}

interface Values {
  postingDate: string;
  description: string;
  supportRef: string;
  supportSha256: string;
  supportFile: string;
  tax: boolean;
  autoReverse: boolean;
  lines: Line[];
}

const EMPTY_LINE: Line = { accountId: "", side: "debit", amount: "", plantId: "", memo: "" };
// GL amounts have 2 decimals (E-FIN1-01-4).
const SCALE = 2;

function fromDetail(j: Schemas["ManualJournalDetail"]): Values {
  return {
    postingDate: j.postingDate,
    description: j.description,
    supportRef: j.supportRef,
    supportSha256: j.supportSha256,
    supportFile: "",
    tax: j.closeComponent === "ACR-TAX",
    autoReverse: j.autoReverse,
    lines: j.lines.map((l) => {
      const debit = /[1-9]/.test(l.debit);
      return { accountId: l.accountId, side: debit ? "debit" : "credit", amount: debit ? l.debit : l.credit, plantId: l.plantId ?? "", memo: l.memo ?? "" };
    }),
  };
}

// FIN1-04 (E-FIN1-04-4…6): prepare an adjustment, or change a DRAFT (?id=). The form never adds amounts: the adjustment's page
// shows the server's totals and "Enviar" waits for a difference of 0.00.
// UX4-02 (A-13, E-UX3-8 (a)): the support's SHA-256 is hidden ("Huella del archivo verificada"), the reference is proposed from the
// file name, an edited draft shows its current support, and the fiscal checkbox reads without the component code.
function AdjustmentForm() {
  const { companyId, can, plantName } = useSession();
  const router = useRouter();
  const id = useSearchParams().get("id");
  const prepare = useCommand<"/api/v1/companies/{companyId}/finance/prepare-manual-journal", Values>("prepare-manual-journal", "/api/v1/companies/{companyId}/finance/prepare-manual-journal", (_, doc) => `Ajuste ${doc ?? ""} guardado en borrador.`);
  const update = useCommand<"/api/v1/companies/{companyId}/finance/update-manual-journal", Values>(`update-manual-journal:${id}`, "/api/v1/companies/{companyId}/finance/update-manual-journal", "Borrador del ajuste actualizado.");
  const restored = id ? update.restored : prepare.restored;
  const [values, setValues] = useState<Values | null>(
    () =>
      restored ??
      (id
        ? null
        : { postingDate: todayInDominicanRepublic(), description: "", supportRef: "", supportSha256: "", supportFile: "", tax: false, autoReverse: false, lines: [{ ...EMPTY_LINE }, { ...EMPTY_LINE, side: "credit" }] }),
  );
  const fe = useFieldErrors();
  const allowed = can("manual_journal:prepare") && can("configuration:read");

  const { data, error } = useLoad(
    allowed
      ? async () => {
          const [accounts, plants, journal] = await Promise.all([
            query("/api/v1/companies/{companyId}/finance/accounts", { path: { companyId } }),
            can("master_data:read") ? query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId } }) : Promise.resolve(null),
            id ? query("/api/v1/companies/{companyId}/finance/manual-journals/{manualJournalId}", { path: { companyId, manualJournalId: id } }) : Promise.resolve(null),
          ]);
          if (journal && values === null) {
            setValues(fromDetail(journal));
          }
          return { accounts: accounts.items.filter((a) => a.status === "ACTIVE" && !a.isControl), plants: plants?.items ?? null, journal };
        }
      : null,
    [companyId, allowed, id],
  );

  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null || values === null) {
    return <Loading error={error} />;
  }
  if (data.journal && data.journal.status !== "DRAFT") {
    return <p className="muted">Solo se modifica un ajuste en borrador.</p>;
  }

  const set = (change: Partial<Values>) => setValues({ ...values, ...change });
  const setLine = (index: number, change: Partial<Line>) => set({ lines: values.lines.map((line, i) => (i === index ? { ...line, ...change } : line)) });

  const chooseFile = async (file: File | undefined) => {
    if (file) {
      const keepRef = values.supportRef.trim() !== "" && values.supportRef !== values.supportFile && !(data.journal && values.supportRef === data.journal.supportRef);
      set({ supportSha256: await sha256Hex(file), supportFile: file.name, supportRef: keepRef ? values.supportRef : file.name });
    }
  };

  const submit = async () => {
    const lines = values.lines.map((l) => ({ ...l, amount: normalizeInput(l.amount) }));
    const found: Record<string, string | false> = {
      postingDate: !values.postingDate && "Indique la fecha contable.",
      description: !values.description.trim() && "Describa el ajuste.",
      supportFile: values.supportSha256.length !== 64 && "Elija el archivo del soporte: el sistema calcula su huella al elegirlo.",
      supportRef: !values.supportRef.trim() && "Indique la referencia del soporte.",
    };
    lines.forEach((l, index) => {
      found[`line-${index}-account`] = !l.accountId && "Elija la cuenta.";
      found[`line-${index}-amount`] = !isPositiveDecimal(l.amount, SCALE) && "Monto mayor que cero, hasta 2 decimales.";
    });
    if (!fe.check(found)) {
      return;
    }
    const body = {
      postingDate: values.postingDate,
      description: values.description.trim(),
      supportRef: values.supportRef.trim(),
      supportSha256: values.supportSha256,
      closeComponent: values.tax ? "ACR-TAX" : "ACR-NTX",
      autoReverse: values.autoReverse,
      lines: lines.map((l) => ({
        accountId: l.accountId,
        debit: l.side === "debit" ? l.amount : "0",
        credit: l.side === "credit" ? l.amount : "0",
        plantId: l.plantId || null,
        partyId: null,
        memo: l.memo.trim() || null,
      })),
    };
    const response =
      id && data.journal
        ? await update.run({ ...body, manualJournalId: id, expectedVersion: data.journal.version }, values, `Ajuste ${data.journal.journalNo} actualizado.`)
        : await prepare.run(body, values);
    if (response) {
      router.push(`/contabilidad/ajuste/?id=${id ?? response.resultRef}`);
    }
  };

  return (
    <>
      <h1>{id ? `Modificar ${data.journal?.journalNo ?? "ajuste"}` : "Nuevo ajuste"}</h1>
      <div>
        <Field label="Fecha contable" required error={fe.errors.postingDate}>
          <input type="date" aria-label="Fecha contable" value={values.postingDate} max={todayInDominicanRepublic()} onChange={(e) => set({ postingDate: e.target.value })} />
        </Field>
        <Field label="Descripción" required error={fe.errors.description}>
          <input aria-label="Descripción" maxLength={500} style={{ width: "min(32rem, 100%)" }} value={values.description} onChange={(e) => set({ description: e.target.value })} />
        </Field>
      </div>
      {values.supportSha256 && !values.supportFile && data.journal ? (
        <p className="evidence-verified" data-testid="support-current" title={`SHA-256 ${values.supportSha256}`}>
          <span className="badge tone-done">✓</span> Soporte actual: {values.supportRef} (huella verificada). Elija otro archivo solo si cambia el soporte.
        </p>
      ) : null}
      <div>
        <Field label="Archivo del soporte" required error={fe.errors.supportFile} hint="El archivo no se guarda en el sistema; conserve el original.">
          <input type="file" aria-label="Archivo del soporte" onChange={(e) => void chooseFile(e.target.files?.[0])} />
        </Field>
        <Field label="Referencia del soporte" required error={fe.errors.supportRef} hint="Se propone el nombre del archivo; puede cambiarla.">
          <input aria-label="Referencia del soporte" maxLength={200} value={values.supportRef} onChange={(e) => set({ supportRef: e.target.value })} />
        </Field>
      </div>
      {values.supportSha256 && values.supportFile ? (
        <p className="evidence-verified" data-testid="support-hash" title={`SHA-256 ${values.supportSha256}`}>
          <span className="badge tone-done">✓</span> Huella del archivo verificada: {values.supportFile}
        </p>
      ) : null}
      <div className="actions">
        <label>
          <input type="checkbox" checked={values.tax} onChange={(e) => set({ tax: e.target.checked })} /> Tiene efecto fiscal (ITBIS, retenciones u otro impuesto)
        </label>
        <label>
          <input type="checkbox" checked={values.autoReverse} onChange={(e) => set({ autoReverse: e.target.checked })} /> Reversar el día 1 del mes siguiente
        </label>
      </div>
      <LineTable>
        <thead>
          <tr>
            <th>Cuenta</th>
            <th>Lado</th>
            <th className="num">Monto (RD$)</th>
            {data.plants ? <th>Planta</th> : null}
            <th>Memo</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {values.lines.map((line, index) => (
            <tr key={index}>
              <td>
                <SearchSelect
                  aria-label={`Cuenta ${index + 1}`}
                  value={line.accountId}
                  onChange={(accountId) => setLine(index, { accountId })}
                  {...fieldAria(fe.errors[`line-${index}-account`], `line-${index}-account-message`, true)}
                  options={data.accounts.map((a) => ({ value: a.accountId, label: `${a.code} — ${a.name}` }))}
                />
                <FieldMessage id={`line-${index}-account-message`} error={fe.errors[`line-${index}-account`]} />
              </td>
              <td>
                <select aria-label={`Lado ${index + 1}`} value={line.side} onChange={(e) => setLine(index, { side: e.target.value === "credit" ? "credit" : "debit" })}>
                  <option value="debit">Débito</option>
                  <option value="credit">Crédito</option>
                </select>
              </td>
              <td className="num">
                <input
                  aria-label={`Monto ${index + 1}`}
                  inputMode="decimal"
                  value={line.amount}
                  onChange={(e) => setLine(index, { amount: e.target.value })}
                  {...fieldAria(fe.errors[`line-${index}-amount`], `line-${index}-amount-message`, true)}
                />
                <FieldMessage id={`line-${index}-amount-message`} error={fe.errors[`line-${index}-amount`]} />
              </td>
              {data.plants ? (
                <td>
                  <select aria-label={`Planta ${index + 1}`} value={line.plantId} onChange={(e) => setLine(index, { plantId: e.target.value })}>
                    <option value="">—</option>
                    {data.plants.map((p) => (
                      <option key={p.plantId} value={p.plantId}>
                        {plantName(p.plantId, p.code)}
                      </option>
                    ))}
                  </select>
                </td>
              ) : null}
              <td>
                <input aria-label={`Memo ${index + 1}`} maxLength={200} value={line.memo} onChange={(e) => setLine(index, { memo: e.target.value })} />
              </td>
              <td>
                {values.lines.length > 2 ? (
                  <button type="button" onClick={() => set({ lines: values.lines.filter((_, i) => i !== index) })}>
                    Quitar
                  </button>
                ) : null}
              </td>
            </tr>
          ))}
        </tbody>
      </LineTable>
      <p className="muted">Solo cuentas activas que no son de control (las de control se mueven solo con sus documentos).</p>
      <p className="notice" data-testid="totals-on-save">
        Los totales de débito y crédito y la diferencia (debe ser 0.00 para enviarlo) los calcula el sistema: aparecen al guardar el borrador.
      </p>
      <div className="actions form-actions">
        <button type="button" onClick={() => set({ lines: [...values.lines, { ...EMPTY_LINE }] })}>
          Agregar línea
        </button>
        <button type="button" className="primary" disabled={prepare.busy || update.busy} onClick={submit}>
          Guardar borrador
        </button>
      </div>
      <ErrorBox error={prepare.error ?? update.error} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <AdjustmentForm />
    </Suspense>
  );
}
