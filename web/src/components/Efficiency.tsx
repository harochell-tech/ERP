"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { itemLabel, useItems } from "@/components/Production";
import { ErrorBox, Field } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// MFG3-04 (E-MFG3-4…7, E-MFG3-01-2): pieces of the efficiency screens. The ratios come from the server (0–1 with 4 decimals); the
// screen only moves the decimal point to show them as a percentage — no arithmetic on the values.

/** "0.9242" → "92.42 %", "1" → "100 %", null → "—": the decimal point moved two places on the text. */
export function percent(ratio: string | null | undefined): string {
  if (ratio === null || ratio === undefined || ratio === "") {
    return "—";
  }
  const [int = "0", frac = ""] = ratio.split(".");
  const padded = frac.padEnd(2, "0");
  const whole = (int + padded.slice(0, 2)).replace(/^0+(?=\d)/, "");
  const rest = padded.slice(2).replace(/0+$/, "");
  return `${whole}${rest ? `.${rest}` : ""} %`;
}

export const STOPPAGE_REASONS: Record<string, string> = {
  falta_material: "Falta de material",
  fallo_mecanico: "Fallo mecánico",
  fallo_electrico: "Fallo eléctrico",
  corte_energia: "Corte de energía",
  falta_personal: "Falta de personal",
  mantenimiento: "Mantenimiento",
  cambio_molde: "Cambio de molde",
  otro: "Otro",
  SIN_RAZON: "Sin razón",
};

/** E-MFG3-5: the ideal cycle per machine and product; the plant manager sets a new one from a date. */
export function IdealCycles({ machines }: { machines: Schemas["MachineView"][] }) {
  const { companyId, can } = useSession();
  const items = useItems();
  const list = useLoad(() => query("/api/v1/companies/{companyId}/manufacturing/ideal-cycles", { path: { companyId } }), [companyId]);
  const set = useCommand("set-ideal-cycle", "/api/v1/companies/{companyId}/manufacturing/set-ideal-cycle", "Ciclo ideal guardado.");
  const [form, setForm] = useState({ machineId: "", itemId: "", validFrom: "", seconds: "" });
  const goods = (items.data ?? []).filter((i) => i.itemType === "FINISHED_GOOD");
  const ids = new Set(machines.map((m) => m.machineId));
  const rows = (list.data?.items ?? []).filter((c) => ids.has(c.machineId));
  return (
    <section data-testid="ideal-cycles">
      <h2>Ciclo ideal</h2>
      <p className="muted">Segundos por ciclo de cada máquina con cada producto, desde una fecha. Sin este dato no se calcula el rendimiento.</p>
      {can("production_master:manage") ? (
        <form
          className="inline-form"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await set.run({ machineId: form.machineId, itemId: form.itemId, validFrom: form.validFrom, seconds: form.seconds.trim() })) {
              setForm({ ...form, seconds: "" });
              list.reload();
            }
          }}
        >
          <Field label="Máquina">
            <select value={form.machineId} required onChange={(e) => setForm({ ...form, machineId: e.target.value })}>
              <option value="">Elija…</option>
              {machines.map((m) => (
                <option key={m.machineId} value={m.machineId}>
                  {m.code} — {m.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Producto (molde)">
            <select value={form.itemId} required onChange={(e) => setForm({ ...form, itemId: e.target.value })}>
              <option value="">Elija…</option>
              {goods.map((i) => (
                <option key={i.itemId} value={i.itemId}>
                  {itemLabel(i)}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Desde">
            <input type="date" value={form.validFrom} required onChange={(e) => setForm({ ...form, validFrom: e.target.value })} />
          </Field>
          <Field label="Segundos por ciclo">
            <input inputMode="decimal" value={form.seconds} required placeholder="11.5" onChange={(e) => setForm({ ...form, seconds: e.target.value })} />
          </Field>
          <button type="submit" className="primary" disabled={set.busy}>
            Guardar ciclo ideal
          </button>
          <ErrorBox error={set.error} />
        </form>
      ) : null}
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Máquina</th>
              <th>Producto</th>
              <th>Desde</th>
              <th className="num">Segundos por ciclo</th>
              <th>Vigente</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((c) => (
              <tr key={`${c.machineId}:${c.itemId}:${c.validFrom}`}>
                <td>{c.machineCode}</td>
                <td>{c.itemCode}</td>
                <td>{formatDate(c.validFrom)}</td>
                <td className="num">{c.seconds}</td>
                <td>{c.inForce ? "Sí" : ""}</td>
              </tr>
            ))}
            {rows.length === 0 ? (
              <tr>
                <td colSpan={5} className="muted">
                  Todavía no hay ciclos ideales.
                </td>
              </tr>
            ) : null}
          </tbody>
        </table>
      </div>
    </section>
  );
}
