"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { todayInDominicanRepublic } from "@/lib/labels";
import { sha256Hex } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

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
function AdjustmentForm() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const id = useSearchParams().get("id");
  const prepare = useCommand<"/api/v1/companies/{companyId}/finance/prepare-manual-journal", Values>("prepare-manual-journal", "/api/v1/companies/{companyId}/finance/prepare-manual-journal");
  const update = useCommand<"/api/v1/companies/{companyId}/finance/update-manual-journal", Values>(`update-manual-journal:${id}`, "/api/v1/companies/{companyId}/finance/update-manual-journal");
  const restored = id ? update.restored : prepare.restored;
  const [values, setValues] = useState<Values | null>(
    () =>
      restored ??
      (id
        ? null
        : { postingDate: todayInDominicanRepublic(), description: "", supportRef: "", supportSha256: "", supportFile: "", tax: false, autoReverse: false, lines: [{ ...EMPTY_LINE }, { ...EMPTY_LINE, side: "credit" }] }),
  );
  const [invalid, setInvalid] = useState<string | null>(null);
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
      set({ supportSha256: await sha256Hex(file), supportFile: file.name, supportRef: values.supportRef || file.name });
    }
  };

  const submit = async () => {
    const lines = values.lines.map((l) => ({ ...l, amount: normalizeInput(l.amount) }));
    if (!values.postingDate || !values.description.trim() || !values.supportRef.trim() || values.supportSha256.length !== 64) {
      setInvalid("Indique fecha, descripción, referencia del soporte y elija el archivo del soporte.");
      return;
    }
    if (lines.length < 2 || lines.some((l) => !l.accountId || !isPositiveDecimal(l.amount, SCALE))) {
      setInvalid("Cada línea necesita cuenta y un monto mayor que cero con hasta 2 decimales; un ajuste tiene al menos dos líneas.");
      return;
    }
    setInvalid(null);
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
        ? await update.run({ ...body, manualJournalId: id, expectedVersion: data.journal.version }, values)
        : await prepare.run(body, values);
    if (response) {
      router.push(`/contabilidad/ajuste/?id=${id ?? response.resultRef}`);
    }
  };

  return (
    <>
      <h1>{id ? `Modificar ${data.journal?.journalNo ?? "ajuste"}` : "Nuevo ajuste"}</h1>
      <div>
        <Field label="Fecha contable">
          <input type="date" aria-label="Fecha contable" value={values.postingDate} max={todayInDominicanRepublic()} onChange={(e) => set({ postingDate: e.target.value })} />
        </Field>
        <Field label="Descripción">
          <input aria-label="Descripción" maxLength={500} size={50} value={values.description} onChange={(e) => set({ description: e.target.value })} />
        </Field>
      </div>
      <div>
        <Field label="Archivo del soporte">
          <input type="file" aria-label="Archivo del soporte" onChange={(e) => void chooseFile(e.target.files?.[0])} />
        </Field>
        <Field label="Referencia del soporte">
          <input aria-label="Referencia del soporte" maxLength={200} value={values.supportRef} onChange={(e) => set({ supportRef: e.target.value })} />
        </Field>
      </div>
      <p className="muted mono" data-testid="support-hash">
        {values.supportSha256 ? `SHA-256 ${values.supportSha256}${values.supportFile ? ` (${values.supportFile})` : ""}` : "El archivo no se sube: se guarda su huella SHA-256."}
      </p>
      <div className="actions">
        <label>
          <input type="checkbox" checked={values.tax} onChange={(e) => set({ tax: e.target.checked })} /> Tiene efecto fiscal (componente ACR-TAX)
        </label>
        <label>
          <input type="checkbox" checked={values.autoReverse} onChange={(e) => set({ autoReverse: e.target.checked })} /> Reversar el día 1 del mes siguiente
        </label>
      </div>
      <table>
        <thead>
          <tr>
            <th>Cuenta</th>
            <th>Lado</th>
            <th className="num">Monto</th>
            {data.plants ? <th>Planta</th> : null}
            <th>Memo</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {values.lines.map((line, index) => (
            <tr key={index}>
              <td>
                <select aria-label={`Cuenta ${index + 1}`} value={line.accountId} onChange={(e) => setLine(index, { accountId: e.target.value })}>
                  <option value="">—</option>
                  {data.accounts.map((a) => (
                    <option key={a.accountId} value={a.accountId}>
                      {a.code} — {a.name}
                    </option>
                  ))}
                </select>
              </td>
              <td>
                <select aria-label={`Lado ${index + 1}`} value={line.side} onChange={(e) => setLine(index, { side: e.target.value === "credit" ? "credit" : "debit" })}>
                  <option value="debit">Débito</option>
                  <option value="credit">Crédito</option>
                </select>
              </td>
              <td className="num">
                <input aria-label={`Monto ${index + 1}`} inputMode="decimal" value={line.amount} onChange={(e) => setLine(index, { amount: e.target.value })} />
              </td>
              {data.plants ? (
                <td>
                  <select aria-label={`Planta ${index + 1}`} value={line.plantId} onChange={(e) => setLine(index, { plantId: e.target.value })}>
                    <option value="">—</option>
                    {data.plants.map((p) => (
                      <option key={p.plantId} value={p.plantId}>
                        {p.code}
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
      </table>
      <p className="muted">Solo cuentas activas que no son de control. Los totales y la diferencia los calcula el sistema al guardar.</p>
      <div className="actions">
        <button type="button" onClick={() => set({ lines: [...values.lines, { ...EMPTY_LINE }] })}>
          Agregar línea
        </button>
        <button type="button" className="primary" disabled={prepare.busy || update.busy} onClick={submit}>
          Guardar borrador
        </button>
      </div>
      {invalid ? <div className="error">{invalid}</div> : null}
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
