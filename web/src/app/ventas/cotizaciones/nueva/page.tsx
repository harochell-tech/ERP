"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { PreviewTotals, useSalesPreview } from "@/components/SalesUx4";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, Money, NoPermission, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { compareDecimals, DEFAULT_QUOTE_VALIDITY_DAYS, isSpecialPrice } from "@/lib/quotes";
import { addDays, DELIVERY_TERMS, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { allCustomers } from "@/lib/paging";

// QUO1-04 (E-QUO1-04-3): create a quote, or edit it while DRAFT (quote:manage). Customers DRAFT or ACTIVE (only an ACTIVE one is
// converted later); products of the price list in force, each line with its list price and an optional quoted price (empty = the
// list's). A price below the list is flagged "Precio especial: requiere aprobación" (an exact comparison of decimal strings); the
// nets and the total are the server's.
// UX4-03 (V-11, E-UX4-3): while typing, the server prices the draft (POST preview): net per line, net total, estimated ITBIS, total.

interface Line {
  itemId: string;
  uom: string;
  quantity: string;
  unitPrice: string;
}

interface Values {
  partyId: string;
  plantId: string;
  validUntil: string;
  deliveryTermCode: string;
  siteAddress: string;
  customerRef: string;
  notes: string;
  lines: Line[];
}

const EMPTY_LINE: Line = { itemId: "", uom: "", quantity: "", unitPrice: "" };

const optional = (text: string) => (text.trim() === "" ? null : text.trim());

function QuoteForm() {
  const { companyId, can, scope, plantName } = useSession();
  const router = useRouter();
  const editId = useSearchParams().get("id");
  const formId = editId ? `edit-quote:${editId}` : "create-quote";
  const create = useCommand<"/api/v1/companies/{companyId}/sales/create-quote", Values>(formId, "/api/v1/companies/{companyId}/sales/create-quote", (_, doc) => (doc ? `Cotización ${doc} creada en borrador.` : "Cotización creada en borrador."));
  const update = useCommand<"/api/v1/companies/{companyId}/sales/update-draft-quote", Values>(formId, "/api/v1/companies/{companyId}/sales/update-draft-quote", (_, doc) => (doc ? `Borrador de la cotización ${doc} guardado.` : "Borrador de la cotización guardado."));
  const [values, setValues] = useState<Values | null>(() => create.restored ?? update.restored ?? null);
  const fe = useFieldErrors<string>();
  const allowed = can("quote:manage") && can("sales:read");
  const permission = scope("quote:manage");

  const { data, error } = useLoad(
    allowed
      ? async () => {
          const [customers, plants, lists, quote] = await Promise.all([
            allCustomers(companyId),
            query("/api/v1/companies/{companyId}/sales/plants", { path: { companyId } }),
            query("/api/v1/companies/{companyId}/sales/price-lists", { path: { companyId } }),
            editId ? query("/api/v1/companies/{companyId}/sales/quotes/{quoteId}", { path: { companyId, quoteId: editId } }) : Promise.resolve(null),
          ]);
          const active = lists.items.find((l) => l.status === "ACTIVE");
          const prices = active
            ? (await query("/api/v1/companies/{companyId}/sales/price-lists/{priceListVersionId}", { path: { companyId, priceListVersionId: active.priceListVersionId } })).lines
            : [];
          return {
            customers: customers.items.filter((c) => c.customerStatus === "DRAFT" || c.customerStatus === "ACTIVE"),
            plants: plants.items.filter((p) => permission.companyWide || permission.plants.includes(p.plantId)),
            prices,
            quote,
          };
        }
      : null,
    [companyId, allowed, editId],
  );
  const previewSource =
    values ??
    (data?.quote
      ? { plantId: data.quote.plantId, lines: data.quote.lines.map((l) => ({ itemId: l.itemId, uom: l.uom, quantity: l.quantity, unitPrice: compareDecimals(l.unitPrice, l.listPrice) === 0 ? "" : l.unitPrice })) }
      : null);
  const preview = useSalesPreview("quote", previewSource?.plantId ?? "", previewSource?.lines ?? [], values?.partyId ?? data?.quote?.header.partyId ?? "");

  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const quote = data.quote;
  if (quote && quote.header.status !== "DRAFT") {
    return <p className="muted">Solo se edita una cotización en borrador.</p>;
  }
  if (data.prices.length === 0) {
    return (
      <p className="muted">
        Todavía no hay una lista de precios aprobada, así que no se puede cotizar. El Controller la prepara en Maestros › Lista de precios y otra
        persona autorizada la aprueba.
      </p>
    );
  }
  const current: Values =
    values ??
    (quote
      ? {
          partyId: quote.header.partyId,
          plantId: quote.plantId,
          validUntil: quote.header.validUntil,
          deliveryTermCode: quote.header.deliveryTermCode,
          siteAddress: quote.siteAddress ?? "",
          customerRef: quote.customerRef ?? "",
          notes: quote.notes ?? "",
          // A line at its list price stays "the list's" (empty); any other quoted price is kept as typed.
          lines: quote.lines.map((l) => ({ itemId: l.itemId, uom: l.uom, quantity: l.quantity, unitPrice: compareDecimals(l.unitPrice, l.listPrice) === 0 ? "" : l.unitPrice })),
        }
      : {
          partyId: "",
          plantId: data.plants[0]?.plantId ?? "",
          validUntil: addDays(todayInDominicanRepublic(), DEFAULT_QUOTE_VALIDITY_DAYS),
          deliveryTermCode: "PICKUP_AT_PLANT",
          siteAddress: "",
          customerRef: "",
          notes: "",
          lines: [{ ...EMPTY_LINE }],
        });
  const set = (change: Partial<Values>) => setValues({ ...current, ...change });
  const setLine = (index: number, change: Partial<Line>) => set({ lines: current.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) });
  const customerStatus = data.customers.find((c) => c.partyId === current.partyId)?.customerStatus;

  const submit = async () => {
    const lines = current.lines.map((l) => ({ itemId: l.itemId, uom: l.uom, quantity: normalizeInput(l.quantity), unitPrice: normalizeInput(l.unitPrice) }));
    const found: Record<string, string | false> = {
      partyId: !current.partyId && "Elija el cliente.",
      plantId: !current.plantId && "Elija la planta.",
      validUntil: !current.validUntil && "Indique la fecha de vigencia.",
      siteAddress: current.deliveryTermCode === "DELIVERED_OWN_TRANSPORT" && current.siteAddress.trim() === "" && "Una entrega en obra necesita la dirección de la obra.",
    };
    lines.forEach((l, i) => {
      found[`line-${i}-item`] = !l.itemId && "Elija el producto.";
      found[`line-${i}-quantity`] = !isPositiveDecimal(l.quantity, 6) && "Indique una cantidad mayor que cero (hasta 6 decimales).";
      found[`line-${i}-price`] = l.unitPrice !== "" && !isPositiveDecimal(l.unitPrice, 4) && "El precio, si se indica, es mayor que cero (hasta 4 decimales).";
    });
    if (!fe.check(found)) {
      return;
    }
    const header = {
      plantId: current.plantId,
      validUntil: current.validUntil,
      deliveryTermCode: current.deliveryTermCode,
      siteAddress: current.deliveryTermCode === "DELIVERED_OWN_TRANSPORT" ? optional(current.siteAddress) : null,
      customerRef: optional(current.customerRef),
      notes: optional(current.notes),
      lines: lines.map((l) => ({ itemId: l.itemId, uom: l.uom, quantity: l.quantity, unitPrice: l.unitPrice === "" ? null : l.unitPrice })),
    };
    const response = quote
      ? await update.run({ quoteId: quote.header.quoteId, expectedVersion: quote.header.version, ...header }, current)
      : await create.run({ partyId: current.partyId, ...header }, current);
    if (response) {
      router.push(`/ventas/cotizacion/?id=${quote ? quote.header.quoteId : response.resultRef}`);
    }
  };

  return (
    <>
      <h1>{quote ? `Editar cotización ${quote.header.quoteNo}` : "Nueva cotización"}</h1>
      <div>
        <Field label="Cliente" required error={fe.errors.partyId}>
          <select aria-label="Cliente" value={current.partyId} disabled={quote !== null} onChange={(e) => set({ partyId: e.target.value })}>
            <option value="">Seleccione…</option>
            {data.customers.map((c) => (
              <option key={c.partyId} value={c.partyId}>
                {c.legalName} {c.rnc ? `(${c.rnc})` : ""}
                {c.customerStatus === "DRAFT" ? " — borrador" : ""}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Planta" required error={fe.errors.plantId}>
          <select aria-label="Planta de la cotización" value={current.plantId} onChange={(e) => set({ plantId: e.target.value })}>
            {data.plants.map((p) => (
              <option key={p.plantId} value={p.plantId}>
                {plantName(p.plantId, p.code)}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Vigente hasta" required error={fe.errors.validUntil}>
          <input type="date" aria-label="Vigente hasta" value={current.validUntil} onChange={(e) => set({ validUntil: e.target.value })} />
        </Field>
        <Field label="Entrega" required>
          <select aria-label="Término de entrega" value={current.deliveryTermCode} onChange={(e) => set({ deliveryTermCode: e.target.value })}>
            {Object.entries(DELIVERY_TERMS).map(([code, label]) => (
              <option key={code} value={code}>
                {label}
              </option>
            ))}
          </select>
        </Field>
        {current.deliveryTermCode === "DELIVERED_OWN_TRANSPORT" ? (
          <Field label="Dirección de la obra" required error={fe.errors.siteAddress}>
            <input aria-label="Dirección de la obra" value={current.siteAddress} onChange={(e) => set({ siteAddress: e.target.value })} />
          </Field>
        ) : null}
        <Field label="Referencia del cliente (opcional)">
          <input aria-label="Referencia del cliente (opcional)" value={current.customerRef} onChange={(e) => set({ customerRef: e.target.value })} />
        </Field>
        <Field label="Notas (opcional)">
          <textarea aria-label="Notas (opcional)" rows={2} value={current.notes} onChange={(e) => set({ notes: e.target.value })} />
        </Field>
      </div>
      {customerStatus === "DRAFT" ? (
        <p className="notice">El cliente está en borrador: se puede cotizar, pero para convertir la cotización en pedido debe estar activo.</p>
      ) : null}
      <LineTable>
        <thead>
          <tr>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Cantidad</th>
            <th className="num">Precio de lista (RD$)</th>
            <th className="num">Precio cotizado (RD$, opcional)</th>
            <th className="num">Neto (RD$)</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {current.lines.map((line, index) => {
            const price = data.prices.find((p) => p.itemId === line.itemId && p.uom === line.uom);
            const special = isSpecialPrice(normalizeInput(line.unitPrice), price?.unitPrice);
            const priced = preview.preview?.lines[index];
            return (
              <tr key={index}>
                <td>
                  <select
                    aria-label={`Producto ${index + 1}`}
                    {...fieldAria(fe.errors[`line-${index}-item`], `quote-line-${index}-item`, true)}
                    value={line.itemId && line.uom ? `${line.itemId}|${line.uom}` : ""}
                    onChange={(e) => {
                      const [itemId = "", uom = ""] = e.target.value.split("|");
                      setLine(index, { itemId, uom });
                    }}
                  >
                    <option value="">Seleccione…</option>
                    {data.prices.map((p) => (
                      <option key={`${p.itemId}|${p.uom}`} value={`${p.itemId}|${p.uom}`}>
                        {p.itemCode} — {p.itemDescription} ({p.uom})
                      </option>
                    ))}
                  </select>
                  <FieldMessage id={`quote-line-${index}-item`} error={fe.errors[`line-${index}-item`]} />
                </td>
                <td>{line.uom}</td>
                <td className="num">
                  <input
                    aria-label={`Cantidad ${index + 1}`}
                    {...fieldAria(fe.errors[`line-${index}-quantity`], `quote-line-${index}-quantity`, true)}
                    inputMode="decimal"
                    value={line.quantity}
                    onChange={(e) => setLine(index, { quantity: e.target.value })}
                  />
                  <FieldMessage id={`quote-line-${index}-quantity`} error={fe.errors[`line-${index}-quantity`]} />
                </td>
                <td className="num">
                  <Money value={price?.unitPrice} testId={`list-price:${index + 1}`} />
                </td>
                <td className="num">
                  <input
                    aria-label={`Precio ${index + 1}`}
                    {...fieldAria(fe.errors[`line-${index}-price`], `quote-line-${index}-price`)}
                    inputMode="decimal"
                    placeholder="Precio de lista"
                    value={line.unitPrice}
                    onChange={(e) => setLine(index, { unitPrice: e.target.value })}
                  />
                  <FieldMessage id={`quote-line-${index}-price`} error={fe.errors[`line-${index}-price`]} />
                  {special ? (
                    <div className="warning" data-testid={`special-price:${index + 1}`}>
                      Precio especial: requiere aprobación
                    </div>
                  ) : null}
                </td>
                <td className="num">
                  {priced && priced.itemId === line.itemId ? <Money value={priced.netAmount} testId={`preview-line-net:${index + 1}`} /> : <span className="muted">—</span>}
                </td>
                <td>
                  {current.lines.length > 1 ? (
                    <button type="button" onClick={() => set({ lines: current.lines.filter((_, i) => i !== index) })}>
                      Quitar
                    </button>
                  ) : null}
                </td>
              </tr>
            );
          })}
        </tbody>
      </LineTable>
      <PreviewTotals {...preview} />
      <p className="muted">
        Sin precio cotizado, la línea toma el de la lista vigente. Un precio por debajo de la lista necesita que otra persona autorizada lo apruebe antes
        de enviar la cotización al cliente.
      </p>
      <div className="actions form-actions">
        <button type="button" onClick={() => set({ lines: [...current.lines, { ...EMPTY_LINE }] })}>
          Agregar línea
        </button>
        <button type="button" className="primary" disabled={create.busy || update.busy} onClick={submit}>
          {quote ? "Guardar borrador" : "Crear cotización"}
        </button>
      </div>
      <ErrorBox error={create.error ?? update.error} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <QuoteForm />
    </Suspense>
  );
}
