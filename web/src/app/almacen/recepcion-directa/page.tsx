"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Field, FieldMessage, fieldAria, LineTable, Loading, Money, NoPermission, useFieldErrors } from "@/components/ui";
import { SearchSelect, partyOption } from "@/components/SearchSelect";
import { formatPercent, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { allSuppliers } from "@/lib/paging";
import { useSession } from "@/lib/session";
import { uomOptions } from "@/lib/units";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { isReceivableLocation, itemLabel, uomLabel } from "@/lib/ux4a-compras";

// REC-02 (E-REC-6…8, E-REC-02-1…7): raw material received without a purchase order. Core proposes each line's price (the last price
// from that supplier, else the standard price); within the purchasing policy the receipt posts at once, otherwise it waits for approval.

interface Line {
  itemId: string;
  uom: string;
  quantity: string;
  unitPrice: string;
}

interface Values {
  plantId: string;
  partyId: string;
  locationId: string;
  occurredAt: string;
  supplierDeliveryNote: string;
  vehicleId: string;
  driverId: string;
  weighTicketRef: string;
  lines: Line[];
}

type Reference = Schemas["DirectReceiptReferenceView"];

const EMPTY_LINE: Line = { itemId: "", uom: "", quantity: "", unitPrice: "" };

/** Local date-time for <input type="datetime-local"> (the browser's clock, in the plant's time zone). */
function localNow(): string {
  const now = new Date();
  now.setSeconds(0, 0);
  return new Date(now.getTime() - now.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
}

/** E-REC-02-2: the server's reference price for one supplier, item and unit (null while the line is incomplete). */
function useReference(companyId: string, partyId: string, itemId: string, uom: string): Reference | null {
  const [state, setState] = useState<{ key: string; reference: Reference | null }>({ key: "", reference: null });
  const key = partyId && itemId && uom ? `${partyId}|${itemId}|${uom}` : "";
  useEffect(() => {
    if (!key) {
      return;
    }
    let cancelled = false;
    query("/api/v1/companies/{companyId}/procurement/direct-receipt-reference", { path: { companyId }, query: { partyId, itemId, uom } })
      .then((reference) => {
        if (!cancelled) {
          setState({ key, reference });
        }
      })
      .catch(() => {
        if (!cancelled) {
          setState({ key, reference: null });
        }
      });
    return () => {
      cancelled = true;
    };
  }, [companyId, key, partyId, itemId, uom]);
  return state.key === key ? state.reference : null;
}

function ReferenceNote({ companyId, partyId, line, index, onPrice }: { companyId: string; partyId: string; line: Line; index: number; onPrice: (price: string) => void }) {
  const reference = useReference(companyId, partyId, line.itemId, line.uom);
  const price = reference?.referencePrice ?? null;
  useEffect(() => {
    if (price && !line.unitPrice) {
      onPrice(price);
    }
  }, [price, line.unitPrice, onPrice]);
  if (!partyId || !line.itemId || !line.uom) {
    return null;
  }
  if (reference === null) {
    return <span className="muted">Buscando precio de referencia…</span>;
  }
  if (!price) {
    return (
      <span className="muted" data-testid={`reference-${index + 1}`}>
        Sin precio de referencia: la recepción quedará pendiente de aprobación.
      </span>
    );
  }
  return (
    <span className="muted" data-testid={`reference-${index + 1}`}>
      Referencia <Money value={price} /> ({reference.source === "LAST_PURCHASE" ? `última compra ${reference.sourcePoNo ?? ""}` : "precio estándar"}).{" "}
      {reference.allowedDeviationPct
        ? `Si el precio se aparta más de ${formatPercent(reference.allowedDeviationPct)}, quedará pendiente de aprobación.`
        : "La política de compras no fija la desviación permitida: quedará pendiente de aprobación."}
    </span>
  );
}

export default function DirectReceipt() {
  const { companyId, can, scope, plantName } = useSession();
  const router = useRouter();
  const post = useCommand<"/api/v1/companies/{companyId}/procurement/post-direct-goods-receipt", Values>(
    "post-direct-gr",
    "/api/v1/companies/{companyId}/procurement/post-direct-goods-receipt",
    (_, doc) => (doc ? `Recepción directa ${doc} registrada.` : "Recepción directa registrada."),
  );
  const [values, setValues] = useState<Values>(
    () =>
      post.restored ?? {
        plantId: "",
        partyId: "",
        locationId: "",
        occurredAt: localNow(),
        supplierDeliveryNote: "",
        vehicleId: "",
        driverId: "",
        weighTicketRef: "",
        lines: [{ ...EMPTY_LINE }],
      },
  );
  const fe = useFieldErrors();
  const permission = scope("goods_receipt:direct");
  const allowed = can("goods_receipt:direct");

  const { data, error } = useLoad(
    allowed
      ? async () => {
          const plantFilter = permission.companyWide ? undefined : permission.plants[0];
          const [plants, suppliers, items, fleet] = await Promise.all([
            query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId }, query: { plantId: plantFilter } }),
            allSuppliers(companyId, { status: "ACTIVE", plantId: plantFilter }),
            query("/api/v1/companies/{companyId}/master-data/items", { path: { companyId }, query: { status: "ACTIVE", plantId: plantFilter, limit: 200 } }),
            query("/api/v1/companies/{companyId}/procurement/receiving-fleet", { path: { companyId } }),
          ]);
          return {
            plants: plants.items.filter((p) => permission.companyWide || permission.plants.includes(p.plantId)),
            // E-REC-02-5: suppliers in pesos (a foreign supplier is bought from with an order in USD).
            suppliers: suppliers.items.filter((s) => s.country === "DO" || !s.country),
            items: items.items,
            fleet,
          };
        }
      : null,
    [companyId, allowed],
  );

  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }

  const plantId = values.plantId || (data.plants.length === 1 ? (data.plants[0]?.plantId ?? "") : "");
  const plant = data.plants.find((p) => p.plantId === plantId);
  const receivable = (plant?.locations ?? []).filter((l) => isReceivableLocation(l.code));
  const defaultLocationId = receivable.find((l) => l.code === "RECEPCION")?.locationId ?? (receivable.length === 1 ? (receivable[0]?.locationId ?? "") : "");
  const locationId = values.locationId || defaultLocationId;

  const setLine = (index: number, change: Partial<Line>) =>
    setValues((v) => ({ ...v, lines: v.lines.map((line, i) => (i === index ? { ...line, ...change } : line)) }));
  const lineError = (index: number, field: string) => fe.errors[`line-${index}-${field}`];

  const submit = async () => {
    const lines = values.lines.map((l) => ({ ...l, quantity: normalizeInput(l.quantity), unitPrice: normalizeInput(l.unitPrice) }));
    const found: Record<string, string | false> = {
      plantId: !plantId && "Elija la planta.",
      partyId: !values.partyId && "Elija el proveedor.",
      locationId: !locationId && "Elija la ubicación.",
      occurredAt: !values.occurredAt && "Indique la fecha y hora de llegada.",
      supplierDeliveryNote: !values.supplierDeliveryNote.trim() && "Escriba el número del conduce que entregó el proveedor.",
      vehicleId: !values.vehicleId && "Elija el camión.",
      driverId: !values.driverId && "Elija el chofer.",
    };
    lines.forEach((l, i) => {
      found[`line-${i}-itemId`] = !l.itemId && "Elija el material.";
      found[`line-${i}-uom`] = !!l.itemId && !l.uom && "Elija la unidad.";
      found[`line-${i}-quantity`] = !isPositiveDecimal(l.quantity, 6) && "Cantidad mayor que cero (hasta 6 decimales).";
      found[`line-${i}-unitPrice`] = !isPositiveDecimal(l.unitPrice, 6) && "Precio mayor que cero (hasta 6 decimales).";
    });
    if (!fe.check(found)) {
      return;
    }
    const response = await post.run(
      {
        plantId,
        partyId: values.partyId,
        locationId,
        occurredAt: new Date(values.occurredAt).toISOString(),
        lines,
        vehicleId: values.vehicleId,
        driverId: values.driverId,
        supplierDeliveryNote: values.supplierDeliveryNote.trim(),
        weighTicketRef: values.weighTicketRef.trim() || null,
      },
      values,
      (sent) => {
        const r = sent.result as { status?: string; poNo?: string } | null;
        return r?.status === "POSTED"
          ? "Recepción registrada y contabilizada (precio dentro de la política)."
          : `Recepción guardada: la orden ${r?.poNo ?? ""} espera aprobación antes de entrar al inventario.`;
      },
    );
    if (!response) {
      return;
    }
    const result = response.result as { status?: string; purchaseOrderId?: string } | null;
    router.push(result?.status === "POSTED" ? `/almacen/recepcion/?id=${response.resultRef}` : `/compras/orden/?id=${result?.purchaseOrderId ?? ""}`);
  };

  return (
    <>
      <p className="actions">
        <Link href="/almacen/por-recibir/">Recibir contra una orden de compra</Link>
      </p>
      <h1>Recepción sin orden de compra</h1>
      <p className="muted">
        Core crea la orden de compra «Recepción directa» con estos precios y cantidades. Si cada precio está dentro de la política de compras, la recepción entra al
        inventario de inmediato; si no, espera la aprobación de otra persona.
      </p>
      <div>
        <Field label="Planta" required error={fe.errors.plantId}>
          <select aria-label="Planta" value={plantId} onChange={(e) => setValues({ ...values, plantId: e.target.value, locationId: "" })}>
            <option value="">—</option>
            {data.plants.map((p) => (
              <option key={p.plantId} value={p.plantId}>
                {plantName(p.plantId, p.code)}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Proveedor" required error={fe.errors.partyId}>
          <SearchSelect
            aria-label="Proveedor"
            value={values.partyId}
            onChange={(partyId) => setValues({ ...values, partyId, lines: values.lines.map((l) => ({ ...l, unitPrice: "" })) })}
            options={data.suppliers.map((s) => partyOption(s.supplierId, s.legalName, s.rnc, s.country))}
          />
        </Field>
        <Field label="Ubicación" required error={fe.errors.locationId}>
          <select aria-label="Ubicación" value={locationId} onChange={(e) => setValues({ ...values, locationId: e.target.value })}>
            <option value="">—</option>
            {receivable.map((l) => (
              <option key={l.locationId} value={l.locationId}>
                {l.code}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Fecha y hora de llegada" required error={fe.errors.occurredAt}>
          <input type="datetime-local" aria-label="Fecha y hora" value={values.occurredAt} onChange={(e) => setValues({ ...values, occurredAt: e.target.value })} />
        </Field>
        <Field label="Conduce del proveedor" required error={fe.errors.supplierDeliveryNote} hint="El número del conduce que entregó el proveedor con el material.">
          <input aria-label="Conduce del proveedor" value={values.supplierDeliveryNote} onChange={(e) => setValues({ ...values, supplierDeliveryNote: e.target.value })} />
        </Field>
        <Field label="Camión" required error={fe.errors.vehicleId} hint="Nuestro camión que trajo el material.">
          <select aria-label="Camión" value={values.vehicleId} onChange={(e) => setValues({ ...values, vehicleId: e.target.value })}>
            <option value="">Seleccione…</option>
            {data.fleet.vehicles.map((v) => (
              <option key={v.vehicleId} value={v.vehicleId}>
                {v.fleetCode ? `${v.fleetCode} · ${v.plate}` : v.plate}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Chofer" required error={fe.errors.driverId}>
          <select aria-label="Chofer" value={values.driverId} onChange={(e) => setValues({ ...values, driverId: e.target.value })}>
            <option value="">Seleccione…</option>
            {data.fleet.drivers.map((d) => (
              <option key={d.driverId} value={d.driverId}>
                {d.fullName}
              </option>
            ))}
          </select>
        </Field>
        <details>
          <summary>Ticket de báscula (opcional)</summary>
          <Field label="Ticket de báscula">
            <input aria-label="Ticket de báscula" value={values.weighTicketRef} onChange={(e) => setValues({ ...values, weighTicketRef: e.target.value })} />
          </Field>
        </details>
      </div>
      <LineTable>
        <thead>
          <tr>
            <th>Material</th>
            <th>Unidad</th>
            <th className="num">Cantidad</th>
            <th className="num">Precio unitario (RD$)</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {values.lines.map((line, index) => {
            const item = data.items.find((i) => i.itemId === line.itemId);
            const uoms = item ? uomOptions(item.baseUom, item.conversions) : [];
            const id = (field: string) => `dr-line-${index}-${field}`;
            return (
              <tr key={index}>
                <td>
                  <SearchSelect
                    aria-label={`Material ${index + 1}`}
                    {...fieldAria(lineError(index, "itemId"), id("itemId"), true)}
                    value={line.itemId}
                    onChange={(itemId) => setLine(index, { itemId, uom: data.items.find((i) => i.itemId === itemId)?.baseUom ?? "", unitPrice: "" })}
                    options={data.items.map((i) => ({ value: i.itemId, label: itemLabel(i.code, i.description), keywords: i.code }))}
                  />
                  <FieldMessage id={id("itemId")} error={lineError(index, "itemId")} />
                  <ReferenceNote companyId={companyId} partyId={values.partyId} line={line} index={index} onPrice={(unitPrice) => setLine(index, { unitPrice })} />
                </td>
                <td>
                  <select aria-label={`Unidad ${index + 1}`} {...fieldAria(lineError(index, "uom"), id("uom"), true)} value={line.uom} onChange={(e) => setLine(index, { uom: e.target.value, unitPrice: "" })}>
                    {uoms.map((u) => (
                      <option key={u} value={u}>
                        {uomLabel(u)}
                      </option>
                    ))}
                  </select>
                  <FieldMessage id={id("uom")} error={lineError(index, "uom")} />
                </td>
                <td className="num">
                  <input
                    aria-label={`Cantidad ${index + 1}`}
                    {...fieldAria(lineError(index, "quantity"), id("quantity"), true)}
                    inputMode="decimal"
                    value={line.quantity}
                    onChange={(e) => setLine(index, { quantity: e.target.value })}
                  />
                  <FieldMessage id={id("quantity")} error={lineError(index, "quantity")} />
                </td>
                <td className="num">
                  <input
                    aria-label={`Precio ${index + 1}`}
                    {...fieldAria(lineError(index, "unitPrice"), id("unitPrice"), true)}
                    inputMode="decimal"
                    value={line.unitPrice}
                    onChange={(e) => setLine(index, { unitPrice: e.target.value })}
                  />
                  <FieldMessage id={id("unitPrice")} error={lineError(index, "unitPrice")} />
                </td>
                <td>
                  {values.lines.length > 1 ? (
                    <button type="button" onClick={() => setValues({ ...values, lines: values.lines.filter((_, i) => i !== index) })}>
                      Quitar
                    </button>
                  ) : null}
                </td>
              </tr>
            );
          })}
        </tbody>
      </LineTable>
      <div className="actions form-actions">
        <button type="button" onClick={() => setValues({ ...values, lines: [...values.lines, { ...EMPTY_LINE }] })}>
          Agregar material
        </button>
        <button type="button" className="primary" disabled={post.busy} onClick={submit}>
          Registrar recepción
        </button>
      </div>
      <ErrorBox error={post.error} />
    </>
  );
}
