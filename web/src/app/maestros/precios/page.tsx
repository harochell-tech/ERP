"use client";

import { useId, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, FieldMessage, fieldAria, LineTable, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-01-14, E-VS3-02-8): the price list (DOP, without ITBIS). The Controller prepares a new version with every line —
// starting from the list in force — and the Aprobador de políticas approves it (step-up); it replaces the list from that day.

interface Line {
  itemId: string;
  uom: string;
  unitPrice: string;
}

function PrepareList({ current, onDone }: { current: Schemas["PriceListLineView"][]; onDone: () => void }) {
  const { companyId, can } = useSession();
  const prepare = useCommand<"/api/v1/companies/{companyId}/sales/prepare-price-list", Line[]>("prepare-price-list", "/api/v1/companies/{companyId}/sales/prepare-price-list");
  const [lines, setLines] = useState<Line[]>(() => prepare.restored ?? current.map((l) => ({ itemId: l.itemId, uom: l.uom, unitPrice: l.unitPrice })));
  const [invalid, setInvalid] = useState<string | null>(null);
  const fe = useFieldErrors();
  const messageId = useId();
  const { data } = useLoad(
    can("master_data:read") ? () => query("/api/v1/companies/{companyId}/master-data/items", { path: { companyId }, query: { status: "ACTIVE", limit: 200 } }) : null,
    [companyId],
  );
  const goods = data?.items.filter((i) => i.itemType === "FINISHED_GOOD") ?? [];
  const currentLabel = (itemId: string) => {
    const line = current.find((c) => c.itemId === itemId);
    return line ? `${line.itemCode} — ${line.itemDescription}` : itemId;
  };
  const setLine = (index: number, change: Partial<Line>) => setLines((ls) => ls.map((l, i) => (i === index ? { ...l, ...change } : l)));

  return (
    <>
      <h2>Nueva lista de precios</h2>
      <LineTable>
        <thead>
          <tr>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Precio sin ITBIS (RD$)</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {lines.map((line, index) => (
            <tr key={index}>
              <td className="wrap">
                {line.itemId && current.some((c) => c.itemId === line.itemId) ? (
                  currentLabel(line.itemId)
                ) : (
                  <>
                    <select
                      aria-label={`Producto ${index + 1}`}
                      value={line.itemId}
                      onChange={(e) => setLine(index, { itemId: e.target.value, uom: goods.find((g) => g.itemId === e.target.value)?.baseUom ?? "" })}
                      {...fieldAria(fe.errors[`line-${index}-item`], `${messageId}-${index}-item`, true)}
                    >
                      <option value="">Seleccione…</option>
                      {goods.map((g) => (
                        <option key={g.itemId} value={g.itemId}>
                          {g.code} — {g.description}
                        </option>
                      ))}
                    </select>
                    <FieldMessage id={`${messageId}-${index}-item`} error={fe.errors[`line-${index}-item`]} />
                  </>
                )}
              </td>
              <td>{line.uom}</td>
              <td className="num">
                <input
                  aria-label={`Precio ${index + 1}`}
                  inputMode="decimal"
                  value={line.unitPrice}
                  onChange={(e) => setLine(index, { unitPrice: e.target.value })}
                  {...fieldAria(fe.errors[`line-${index}-price`], `${messageId}-${index}-price`, true)}
                />
                <FieldMessage id={`${messageId}-${index}-price`} error={fe.errors[`line-${index}-price`]} />
              </td>
              <td>
                <button type="button" onClick={() => setLines(lines.filter((_, i) => i !== index))}>
                  Quitar
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </LineTable>
      {invalid ? <div className="error">{invalid}</div> : null}
      <div className="actions form-actions">
        {goods.length > 0 ? (
          <button type="button" onClick={() => setLines([...lines, { itemId: "", uom: "", unitPrice: "" }])}>
            Agregar producto
          </button>
        ) : null}
        <button
          type="button"
          className="primary"
          disabled={prepare.busy}
          onClick={async () => {
            const body = lines.map((l) => ({ ...l, unitPrice: normalizeInput(l.unitPrice) }));
            setInvalid(body.length === 0 ? "Agregue al menos un producto." : null);
            const found: Record<string, string | false> = {};
            body.forEach((l, i) => {
              found[`line-${i}-item`] = (!l.itemId || !l.uom) && "Elija el producto.";
              found[`line-${i}-price`] = !isPositiveDecimal(l.unitPrice, 4) && "Indique un precio mayor que cero (hasta 4 decimales).";
            });
            if (!fe.check(found) || body.length === 0) {
              return;
            }
            if (await prepare.run({ lines: body }, lines, `Lista de precios con ${body.length} producto(s) preparada; falta su aprobación.`)) {
              onDone();
            }
          }}
        >
          Preparar lista
        </button>
      </div>
      <ErrorBox error={prepare.error} />
    </>
  );
}

function Version({ version, onDone }: { version: Schemas["PriceListSummary"]; onDone: () => void }) {
  const { companyId, can } = useSession();
  const approve = useCommand(`approve-price-list:${version.priceListVersionId}`, "/api/v1/companies/{companyId}/sales/approve-price-list", `Lista de precios versión ${version.version} aprobada: rige desde hoy.`);
  const [open, setOpen] = useState(false);
  const { data, error } = useLoad(
    open ? () => query("/api/v1/companies/{companyId}/sales/price-lists/{priceListVersionId}", { path: { companyId, priceListVersionId: version.priceListVersionId } }) : null,
    [companyId, open],
  );
  return (
    <>
      <tr>
        <td className="num">{version.version}</td>
        <td>{formatDate(version.effectiveFrom)}</td>
        <td>
          <StatusBadge status={version.status} />
        </td>
        <td className="num">{version.lines}</td>
        <td className="wrap">{version.preparedBy ?? "—"}</td>
        <td className="wrap">{version.approvedBy ?? "—"}</td>
        <td className="actions">
          <button type="button" onClick={() => setOpen(!open)}>
            {open ? "Ocultar" : "Ver precios"}
          </button>
          {version.status === "DRAFT" && can("price_list:approve") ? (
            <ConfirmAction
              label="Aprobar"
              className="primary"
              stepUp
              busy={approve.busy}
              title={`¿Aprobar la lista de precios versión ${version.version}?`}
              consequence="La lista rige desde hoy y reemplaza a la vigente: los pedidos y cotizaciones nuevos usarán estos precios. No se puede deshacer."
              onConfirm={async () => (await approve.run({ priceListVersionId: version.priceListVersionId })) && onDone()}
            />
          ) : null}
          <ErrorBox error={approve.error} />
        </td>
      </tr>
      {open ? (
        <tr>
          <td colSpan={7}>
            {data === null ? (
              <LoadingIndicator error={error} />
            ) : (
              <div className="table-wrap"><table>
                <tbody>
                  {data.lines.map((l) => (
                    <tr key={`${l.itemId}:${l.uom}`}>
                      <td>
                        {l.itemCode} — {l.itemDescription}
                      </td>
                      <td>{l.uom}</td>
                      <td className="num">
                        <Money value={l.unitPrice} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table></div>
            )}
          </td>
        </tr>
      ) : null}
    </>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const [preparing, setPreparing] = useState(false);
  const { data, error, reload } = useLoad(
    can("sales:read")
      ? async () => {
          const lists = await query("/api/v1/companies/{companyId}/sales/price-lists", { path: { companyId } });
          const active = lists.items.find((l) => l.status === "ACTIVE");
          const lines = active
            ? (await query("/api/v1/companies/{companyId}/sales/price-lists/{priceListVersionId}", { path: { companyId, priceListVersionId: active.priceListVersionId } })).lines
            : [];
          return { lists: lists.items, activeLines: lines };
        }
      : null,
    [companyId],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const active = data.lists.find((l) => l.status === "ACTIVE");
  return (
    <>
      <div className="actions" style={{ justifyContent: "space-between" }}>
        <h1>Lista de precios</h1>
        {can("price_list:prepare") && !preparing ? (
          <button type="button" className="primary" onClick={() => setPreparing(true)}>
            Preparar nueva lista
          </button>
        ) : null}
      </div>
      {/* UX4-03 (V-21): what a salesperson needs first — the prices in force today (the ACTIVE version). */}
      <h2>Precios vigentes</h2>
      {active ? (
        <>
          <p className="muted">
            Versión {active.version}, vigente desde el {formatDate(active.effectiveFrom)}. Precios en RD$ sin ITBIS.
          </p>
          <div className="table-wrap">
            <table data-testid="current-prices">
              <thead>
                <tr>
                  <th>Producto</th>
                  <th>Unidad</th>
                  <th className="num">Precio sin ITBIS (RD$)</th>
                </tr>
              </thead>
              <tbody>
                {data.activeLines.map((l) => (
                  <tr key={`${l.itemId}:${l.uom}`}>
                    <td className="wrap">
                      {l.itemCode} — {l.itemDescription}
                    </td>
                    <td>{l.uom}</td>
                    <td className="num">
                      <Money value={l.unitPrice} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      ) : (
        <EmptyState title="Todavía no hay una lista de precios vigente.">
          <p>Sin ella no se pueden tomar pedidos ni cotizar. El Controller la prepara aquí y otra persona autorizada la aprueba.</p>
        </EmptyState>
      )}
      {preparing ? (
        <PrepareList
          current={data.activeLines}
          onDone={() => {
            setPreparing(false);
            reload();
          }}
        />
      ) : null}
      <h2>Todas las versiones</h2>
      {data.lists.length === 0 ? (
        <p className="muted">No hay listas de precios.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th className="num">Versión</th>
              <th>Vigente desde</th>
              <th>Estado</th>
              <th className="num">Productos</th>
              <th>Preparó</th>
              <th>Aprobó</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.lists.map((l) => (
              <Version key={`${l.priceListVersionId}:${l.status}`} version={l} onDone={reload} />
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
