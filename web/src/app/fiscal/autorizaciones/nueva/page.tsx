"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { ErrorBox, Field, Loading, NoPermission } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// FIS1-05 (E-FIS1-05-3): register a CONFOTUR authorization, or edit it while DRAFT (fiscal_authorization:register). The scope is
// product × sale unit with the authorized quantity and net, as typed from the DGII certificate; the server validates everything
// (active customer with RNC, unique certificate, active finished goods). Products come from the price list in force (sales:read).

interface Line {
  itemId: string;
  uom: string;
  quantity: string;
  netAmount: string;
}

interface Values {
  partyId: string;
  certificateNo: string;
  issuedOn: string;
  validUntil: string;
  projectName: string;
  confoturResolutionNo: string;
  projectTermEndsOn: string;
  salesOrderId: string;
  lines: Line[];
}

const EMPTY_LINE: Line = { itemId: "", uom: "", quantity: "", netAmount: "" };

function OriginOrder({ partyId, value, onChange }: { partyId: string; value: string; onChange: (salesOrderId: string) => void }) {
  const { companyId } = useSession();
  const { data } = useLoad(partyId ? () => query("/api/v1/companies/{companyId}/sales/orders", { path: { companyId }, query: { partyId, limit: 200 } }) : null, [companyId, partyId]);
  const orders = partyId ? (data?.items ?? []) : [];
  return (
    <Field label="Pedido de origen (opcional)">
      <select aria-label="Pedido de origen (opcional)" value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Ninguno</option>
        {orders.map((o) => (
          <option key={o.salesOrderId} value={o.salesOrderId}>
            {o.orderNo} — {formatDate(o.orderDate)}
          </option>
        ))}
      </select>
    </Field>
  );
}

function AuthorizationForm() {
  const { companyId, can } = useSession();
  const router = useRouter();
  const editId = useSearchParams().get("id");
  const formId = editId ? `edit-authorization:${editId}` : "register-authorization";
  const register = useCommand<"/api/v1/companies/{companyId}/tax/register-fiscal-authorization", Values>(formId, "/api/v1/companies/{companyId}/tax/register-fiscal-authorization");
  const update = useCommand<"/api/v1/companies/{companyId}/tax/update-draft-authorization", Values>(formId, "/api/v1/companies/{companyId}/tax/update-draft-authorization");
  const [values, setValues] = useState<Values | null>(() => register.restored ?? update.restored ?? null);
  const [invalid, setInvalid] = useState<string | null>(null);
  const allowed = can("fiscal_authorization:register") && can("sales:read");

  const { data, error } = useLoad(
    allowed
      ? async () => {
          const [customers, lists, authorization] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/customers", { path: { companyId }, query: { status: "ACTIVE", limit: 200 } }),
            query("/api/v1/companies/{companyId}/sales/price-lists", { path: { companyId } }),
            editId ? query("/api/v1/companies/{companyId}/tax/fiscal-authorizations/{authorizationId}", { path: { companyId, authorizationId: editId } }) : Promise.resolve(null),
          ]);
          const active = lists.items.find((l) => l.status === "ACTIVE");
          const prices = active
            ? (await query("/api/v1/companies/{companyId}/sales/price-lists/{priceListVersionId}", { path: { companyId, priceListVersionId: active.priceListVersionId } })).lines
            : [];
          // The products of the price list in force, plus those already in the draft's scope.
          const products = new Map(prices.map((p) => [`${p.itemId}|${p.uom}`, `${p.itemCode} — ${p.itemDescription} (${p.uom})`]));
          for (const l of authorization?.lines ?? []) {
            products.set(`${l.itemId}|${l.uom}`, `${l.itemCode} — ${l.itemName} (${l.uom})`);
          }
          return { customers: customers.items.filter((c) => c.rnc), products: [...products.entries()], authorization };
        }
      : null,
    [companyId, allowed, editId],
  );

  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const authorization = data.authorization;
  if (authorization && authorization.header.status !== "DRAFT") {
    return <p className="muted">Solo se edita una autorización en borrador.</p>;
  }
  const current: Values =
    values ??
    (authorization
      ? {
          partyId: authorization.header.partyId,
          certificateNo: authorization.header.certificateNo,
          issuedOn: authorization.header.issuedOn,
          validUntil: authorization.header.validUntil ?? "",
          projectName: authorization.header.projectName,
          confoturResolutionNo: authorization.confoturResolutionNo,
          projectTermEndsOn: authorization.projectTermEndsOn ?? "",
          salesOrderId: authorization.salesOrderId ?? "",
          lines: authorization.lines.map((l) => ({ itemId: l.itemId, uom: l.uom, quantity: l.qtyAuthorized, netAmount: l.netAuthorized })),
        }
      : { partyId: "", certificateNo: "", issuedOn: "", validUntil: "", projectName: "", confoturResolutionNo: "", projectTermEndsOn: "", salesOrderId: "", lines: [{ ...EMPTY_LINE }] });
  const set = (change: Partial<Values>) => setValues({ ...current, ...change });
  const setText = (key: "certificateNo" | "issuedOn" | "validUntil" | "projectName" | "confoturResolutionNo" | "projectTermEndsOn") => (e: { target: { value: string } }) =>
    set({ [key]: e.target.value });
  const setLine = (index: number, change: Partial<Line>) => set({ lines: current.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) });

  const submit = async () => {
    const lines = current.lines.map((l) => ({ itemId: l.itemId, uom: l.uom, quantity: normalizeInput(l.quantity), netAmount: normalizeInput(l.netAmount) }));
    if (!current.partyId || current.certificateNo.trim() === "" || !current.issuedOn || current.projectName.trim() === "" || current.confoturResolutionNo.trim() === "") {
      setInvalid("Complete cliente, número de certificado, fecha de emisión, proyecto y resolución CONFOTUR.");
      return;
    }
    if (lines.some((l) => !l.itemId || !isPositiveDecimal(l.quantity, 6) || !isPositiveDecimal(l.netAmount, 2))) {
      setInvalid("Cada línea necesita producto, cantidad mayor que cero (hasta 6 decimales) y neto mayor que cero (hasta 2 decimales).");
      return;
    }
    setInvalid(null);
    const header = {
      certificateNo: current.certificateNo.trim(),
      issuedOn: current.issuedOn,
      validUntil: current.validUntil === "" ? null : current.validUntil,
      projectName: current.projectName.trim(),
      confoturResolutionNo: current.confoturResolutionNo.trim(),
      projectTermEndsOn: current.projectTermEndsOn === "" ? null : current.projectTermEndsOn,
      salesOrderId: current.salesOrderId === "" ? null : current.salesOrderId,
      lines,
    };
    const response = authorization
      ? await update.run({ authorizationId: authorization.header.authorizationId, expectedVersion: authorization.header.version, ...header }, current)
      : await register.run({ partyId: current.partyId, ...header }, current);
    if (response) {
      router.push(`/fiscal/autorizacion/?id=${authorization ? authorization.header.authorizationId : response.resultRef}`);
    }
  };

  return (
    <>
      <h1>{authorization ? `Editar autorización ${authorization.header.certificateNo}` : "Registrar autorización fiscal"}</h1>
      <p className="muted">Régimen CONFOTUR. Copie los datos del certificado de exención emitido por la DGII; el certificado se adjunta después, en la autorización.</p>
      <div>
        <Field label="Cliente">
          <select aria-label="Cliente" value={current.partyId} disabled={authorization !== null} onChange={(e) => set({ partyId: e.target.value, salesOrderId: "" })}>
            <option value="">—</option>
            {data.customers.map((c) => (
              <option key={c.partyId} value={c.partyId}>
                {c.legalName} ({c.rnc})
              </option>
            ))}
          </select>
        </Field>
        <Field label="Número de certificado">
          <input value={current.certificateNo} onChange={setText("certificateNo")} />
        </Field>
        <Field label="Emitido el">
          <input type="date" value={current.issuedOn} onChange={setText("issuedOn")} />
        </Field>
        <Field label="Vigente hasta">
          <input type="date" value={current.validUntil} onChange={setText("validUntil")} />
        </Field>
        <Field label="Proyecto">
          <input value={current.projectName} onChange={setText("projectName")} />
        </Field>
        <Field label="Resolución CONFOTUR">
          <input value={current.confoturResolutionNo} onChange={setText("confoturResolutionNo")} />
        </Field>
        <Field label="Fin del plazo del proyecto (opcional)">
          <input type="date" value={current.projectTermEndsOn} onChange={setText("projectTermEndsOn")} />
        </Field>
        <OriginOrder partyId={current.partyId} value={current.salesOrderId} onChange={(salesOrderId) => set({ salesOrderId })} />
      </div>
      <h2>Alcance</h2>
      <table>
        <thead>
          <tr>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Cantidad autorizada</th>
            <th className="num">Neto autorizado</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {current.lines.map((line, index) => (
            <tr key={index}>
              <td>
                <select
                  aria-label={`Producto ${index + 1}`}
                  value={line.itemId && line.uom ? `${line.itemId}|${line.uom}` : ""}
                  onChange={(e) => {
                    const [itemId = "", uom = ""] = e.target.value.split("|");
                    setLine(index, { itemId, uom });
                  }}
                >
                  <option value="">—</option>
                  {data.products.map(([value, label]) => (
                    <option key={value} value={value}>
                      {label}
                    </option>
                  ))}
                </select>
              </td>
              <td>{line.uom}</td>
              <td className="num">
                <input aria-label={`Cantidad ${index + 1}`} inputMode="decimal" value={line.quantity} onChange={(e) => setLine(index, { quantity: e.target.value })} />
              </td>
              <td className="num">
                <input aria-label={`Neto ${index + 1}`} inputMode="decimal" value={line.netAmount} onChange={(e) => setLine(index, { netAmount: e.target.value })} />
              </td>
              <td>
                {current.lines.length > 1 ? (
                  <button type="button" onClick={() => set({ lines: current.lines.filter((_, i) => i !== index) })}>
                    Quitar
                  </button>
                ) : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="actions">
        <button type="button" onClick={() => set({ lines: [...current.lines, { ...EMPTY_LINE }] })}>
          Agregar línea
        </button>
        <button type="button" className="primary" disabled={register.busy || update.busy} onClick={submit}>
          {authorization ? "Guardar borrador" : "Registrar autorización"}
        </button>
      </div>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={register.error ?? update.error} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <AuthorizationForm />
    </Suspense>
  );
}
