"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { addDays, formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { allSuppliers } from "@/lib/paging";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { SearchSelect, partyOption } from "@/components/SearchSelect";

// USD1-07a (E-USD1-07-4, E-USD1-04-1/2): Compras › DUA. Cuentas por pagar registers the customs declaration and it is posted at once:
// duties and other charges to «Importaciones por liquidar», the ITBIS paid at customs recoverable, the total owed to the DGA. The
// Controller reverses it while it is unpaid and in no live settlement.

const STATUS_LABELS: Readonly<Record<string, string>> = {
  POSTED: "Contabilizado",
  REVERSED: "Reversado",
};

/** «0», «1500», «1,500.00» → «1500.00» or null when not an amount ≥ 0 with 2 decimals. */
function amount(value: string, allowZero = true): string | null {
  const normalized = normalizeInput(value || "0");
  return (allowZero && /^0+(\.0{1,2})?$/.test(normalized)) || isPositiveDecimal(normalized, 2) ? normalized : null;
}

function RegisterDua({ onDone }: { onDone: () => void }) {
  const { companyId, plantName } = useSession();
  const register = useCommand("register-dua", "/api/v1/companies/{companyId}/procurement/register-customs-declaration");
  const masters = useLoad(async () => {
    const [suppliers, plants] = await Promise.all([
      allSuppliers(companyId, { status: "ACTIVE" }),
      query("/api/v1/companies/{companyId}/master-data/plants", {
        path: { companyId },
      }),
    ]);
    return {
      suppliers: suppliers.items.filter((s) => s.partyKind === "LOCAL"),
      plants: plants.items,
    };
  }, [companyId]);
  const today = todayInDominicanRepublic();
  const [v, setV] = useState({
    partyId: "",
    plantId: "",
    duaNo: "",
    duaDate: today,
    dueDate: addDays(today, 5),
    cif: "",
    duties: "",
    itbis: "",
    other: "",
  });
  const fe = useFieldErrors<string>();
  if (masters.data === null) {
    return <LoadingIndicator error={masters.error} />;
  }
  const set = (change: Partial<typeof v>) => setV((x) => ({ ...x, ...change }));
  return (
    <form
      className="card"
      noValidate
      data-testid="dua-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const values = {
          cif: amount(v.cif, false),
          duties: amount(v.duties),
          itbis: amount(v.itbis),
          other: amount(v.other),
        };
        if (
          !fe.check({
            partyId: !v.partyId && "Elija la DGA (proveedor local).",
            plantId: !v.plantId && "Elija la planta.",
            duaNo: !v.duaNo.trim() && "Indique el número del DUA.",
            duaDate: !v.duaDate && "Indique la fecha.",
            dueDate: !v.dueDate && "Indique el vencimiento.",
            cif: values.cif === null && "El valor CIF es mayor que cero, con hasta 2 decimales.",
            duties: values.duties === null && "Monto con hasta 2 decimales.",
            itbis: values.itbis === null && "Monto con hasta 2 decimales.",
            other: values.other === null && "Monto con hasta 2 decimales.",
          })
        ) {
          return;
        }
        const body = {
          partyId: v.partyId,
          plantId: v.plantId,
          duaNo: v.duaNo.trim(),
          duaDate: v.duaDate,
          dueDate: v.dueDate,
          cifAmount: values.cif!,
          dutiesAmount: values.duties!,
          itbisAmount: values.itbis!,
          otherAmount: values.other!,
        };
        if (await register.run(body, undefined, `DUA ${v.duaNo.trim()} registrado y contabilizado.`)) {
          set({
            duaNo: "",
            cif: "",
            duties: "",
            itbis: "",
            other: "",
          });
          onDone();
        }
      }}
    >
      <Field label="DGA (proveedor)" required error={fe.errors.partyId}>
        <SearchSelect
          aria-label="DGA"
          value={v.partyId}
          onChange={(partyId) => set({ partyId })}
          options={masters.data.suppliers.map((s) => partyOption(s.supplierId, s.legalName, s.rnc, s.country))}
        />
      </Field>
      <Field label="Planta" required error={fe.errors.plantId}>
        <select aria-label="Planta" value={v.plantId} onChange={(e) => set({ plantId: e.target.value })}>
          <option value="">—</option>
          {masters.data.plants.map((p) => (
            <option key={p.plantId} value={p.plantId}>
              {plantName(p.plantId, p.code)}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Número del DUA" required error={fe.errors.duaNo}>
        <input aria-label="Número del DUA" maxLength={40} value={v.duaNo} onChange={(e) => set({ duaNo: e.target.value })} />
      </Field>
      <Field label="Fecha" required error={fe.errors.duaDate}>
        <input type="date" aria-label="Fecha del DUA" value={v.duaDate} max={today} onChange={(e) => set({ duaDate: e.target.value })} />
      </Field>
      <Field label="Vence" required error={fe.errors.dueDate}>
        <input type="date" aria-label="Vencimiento del DUA" value={v.dueDate} onChange={(e) => set({ dueDate: e.target.value })} />
      </Field>
      <Field label="Valor CIF (RD$)" required error={fe.errors.cif}>
        <input aria-label="Valor CIF" inputMode="decimal" value={v.cif} onChange={(e) => set({ cif: e.target.value })} />
      </Field>
      <Field label="Aranceles (RD$)" error={fe.errors.duties}>
        <input aria-label="Aranceles" inputMode="decimal" value={v.duties} onChange={(e) => set({ duties: e.target.value })} />
      </Field>
      <Field label="ITBIS de aduana (RD$)" error={fe.errors.itbis}>
        <input aria-label="ITBIS de aduana" inputMode="decimal" value={v.itbis} onChange={(e) => set({ itbis: e.target.value })} />
      </Field>
      <Field label="Otros cargos (RD$)" error={fe.errors.other}>
        <input aria-label="Otros cargos" inputMode="decimal" value={v.other} onChange={(e) => set({ other: e.target.value })} />
      </Field>
      <p className="muted">Los aranceles y otros cargos esperan la liquidación del embarque; el ITBIS es adelantado. El total se le debe a la DGA.</p>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={register.busy}>
          Registrar DUA
        </button>
      </div>
      <ErrorBox error={register.error} />
    </form>
  );
}

function DuaRow({ dua, onDone }: { dua: Schemas["CustomsDeclarationView"]; onDone: () => void }) {
  const { can } = useSession();
  const reverse = useCommand(`reverse-dua:${dua.duaId}`, "/api/v1/companies/{companyId}/procurement/reverse-customs-declaration");
  const [reason, setReason] = useState("");
  const reversible = dua.status === "POSTED" && dua.settlementNo === null && dua.openAmount === dua.payable && can("supplier_invoice:reverse");
  return (
    <tr data-testid={`dua:${dua.duaNo}`}>
      <td>{dua.duaNo}</td>
      <td>{formatDate(dua.duaDate)}</td>
      <td className="num">
        <Money value={dua.dutiesAmount} />
      </td>
      <td className="num">
        <Money value={dua.itbisAmount} />
      </td>
      <td className="num">
        <Money value={dua.otherAmount} />
      </td>
      <td className="num">
        <Money value={dua.payable} />
      </td>
      <td className="num">{dua.openAmount === null ? "—" : <Money value={dua.openAmount} />}</td>
      <td>
        <StatusBadge status={dua.status} label={STATUS_LABELS[dua.status]} />
      </td>
      <td>{dua.settlementNo ?? <span className="muted">Sin liquidar</span>}</td>
      <td>
        {reversible ? (
          <div className="actions row-buttons">
            <input aria-label={`Motivo de la reversa del DUA ${dua.duaNo}`} placeholder="Motivo" value={reason} onChange={(e) => setReason(e.target.value)} />
            <ConfirmAction
              label="Reversar"
              danger
              stepUp
              busy={reverse.busy}
              disabled={reason.trim().length < 3}
              consequence="Se reversa el asiento del DUA y la deuda con la DGA queda en cero."
              onConfirm={async () =>
                (await reverse.run(
                  {
                    duaId: dua.duaId,
                    expectedVersion: dua.version,
                    reason: reason.trim(),
                  },
                  undefined,
                  `DUA ${dua.duaNo} reversado.`,
                )) && onDone()
              }
            />
          </div>
        ) : null}
        <ErrorBox error={reverse.error} />
      </td>
    </tr>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const [adding, setAdding] = useState(false);
  const list = useLoad(
    can("supplier_invoice:read") ? () => query("/api/v1/companies/{companyId}/procurement/customs-declarations", { path: { companyId } }) : null,
    [companyId],
  );
  if (!can("supplier_invoice:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>DUA (declaraciones de aduana)</h1>
      <p className="muted">
        Cada DUA queda «sin liquidar» hasta que una <Link href="/compras/liquidaciones/">liquidación de importación</Link> reparte sus aranceles en el costo del
        embarque.
      </p>
      {can("supplier_invoice:post") ? (
        adding ? (
          <RegisterDua
            onDone={() => {
              setAdding(false);
              list.reload();
            }}
          />
        ) : (
          <div className="actions">
            <button type="button" className="primary" onClick={() => setAdding(true)}>
              Registrar DUA
            </button>
          </div>
        )
      ) : null}
      {list.data === null ? (
        <LoadingIndicator error={list.error} />
      ) : list.data.items.length === 0 ? (
        <p className="muted">Todavía no hay DUA registrados.</p>
      ) : (
        <div className="table-wrap">
          <table data-testid="duas">
            <thead>
              <tr>
                <th>DUA</th>
                <th>Fecha</th>
                <th className="num">Aranceles</th>
                <th className="num">ITBIS</th>
                <th className="num">Otros</th>
                <th className="num">Total DGA</th>
                <th className="num">Saldo</th>
                <th>Estado</th>
                <th>Liquidación</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {list.data.items.map((d) => (
                <DuaRow key={`${d.duaId}:${d.version}`} dua={d} onDone={list.reload} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
