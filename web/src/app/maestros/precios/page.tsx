"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { ErrorBox, Loading, Money, NoPermission, StatusBadge } from "@/components/ui";
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
      <table>
        <thead>
          <tr>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Precio (sin ITBIS)</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {lines.map((line, index) => (
            <tr key={index}>
              <td>
                {line.itemId && current.some((c) => c.itemId === line.itemId) ? (
                  currentLabel(line.itemId)
                ) : (
                  <select
                    aria-label={`Producto ${index + 1}`}
                    value={line.itemId}
                    onChange={(e) => setLine(index, { itemId: e.target.value, uom: goods.find((g) => g.itemId === e.target.value)?.baseUom ?? "" })}
                  >
                    <option value="">—</option>
                    {goods.map((g) => (
                      <option key={g.itemId} value={g.itemId}>
                        {g.code} — {g.description}
                      </option>
                    ))}
                  </select>
                )}
              </td>
              <td>{line.uom}</td>
              <td className="num">
                <input aria-label={`Precio ${index + 1}`} inputMode="decimal" value={line.unitPrice} onChange={(e) => setLine(index, { unitPrice: e.target.value })} />
              </td>
              <td>
                <button type="button" onClick={() => setLines(lines.filter((_, i) => i !== index))}>
                  Quitar
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="actions">
        {goods.length > 0 ? (
          <button type="button" onClick={() => setLines([...lines, { itemId: "", uom: "", unitPrice: "" }])}>
            Agregar producto
          </button>
        ) : null}
        <button
          type="button"
          disabled={prepare.busy}
          onClick={async () => {
            const body = lines.map((l) => ({ ...l, unitPrice: normalizeInput(l.unitPrice) }));
            if (body.length === 0 || body.some((l) => !l.itemId || !l.uom || !isPositiveDecimal(l.unitPrice, 4))) {
              setInvalid("Cada línea necesita producto y un precio mayor que cero (hasta 4 decimales).");
              return;
            }
            setInvalid(null);
            if (await prepare.run({ lines: body }, lines)) {
              onDone();
            }
          }}
        >
          Preparar lista
        </button>
      </div>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={prepare.error} />
    </>
  );
}

function Version({ version, onDone }: { version: Schemas["PriceListSummary"]; onDone: () => void }) {
  const { companyId, can } = useSession();
  const approve = useCommand(`approve-price-list:${version.priceListVersionId}`, "/api/v1/companies/{companyId}/sales/approve-price-list");
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
        <td>{version.preparedBy ?? "—"}</td>
        <td>{version.approvedBy ?? "—"}</td>
        <td className="actions">
          <button type="button" onClick={() => setOpen(!open)}>
            {open ? "Ocultar" : "Ver precios"}
          </button>
          {version.status === "DRAFT" && can("price_list:approve") ? (
            <button type="button" disabled={approve.busy} onClick={async () => (await approve.run({ priceListVersionId: version.priceListVersionId })) && onDone()}>
              Aprobar
            </button>
          ) : null}
          <ErrorBox error={approve.error} />
        </td>
      </tr>
      {open ? (
        <tr>
          <td colSpan={7}>
            {data === null ? (
              <Loading error={error} />
            ) : (
              <table>
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
              </table>
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
    return <Loading error={error} />;
  }
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
      {preparing ? (
        <PrepareList
          current={data.activeLines}
          onDone={() => {
            setPreparing(false);
            reload();
          }}
        />
      ) : null}
      <h2>Versiones</h2>
      {data.lists.length === 0 ? (
        <p className="muted">No hay listas de precios.</p>
      ) : (
        <table>
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
        </table>
      )}
    </>
  );
}
