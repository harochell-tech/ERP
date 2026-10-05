"use client";

import { useId, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, FieldMessage, fieldAria, LineTable, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-01-14, E-VS3-02-8) and PRS-05 (E-PRS-05-1): the price lists — «General» and the customers' own lists (DOP, without
// ITBIS). The Controller creates a list and prepares each version — starting from the version in force — with its product prices and
// its freight table per product and zone; the Aprobador de políticas approves it (step-up) and it replaces that list's version in force.

type Header = Schemas["PriceListHeaderView"];

interface Line {
  itemId: string;
  uom: string;
  unitPrice: string;
}

interface FreightLine extends Line {
  zoneId: string;
}

interface Draft {
  lines: Line[];
  freight: FreightLine[];
}

const STATUS_LABELS: Readonly<Record<string, string>> = { ACTIVE: "Activa", INACTIVE: "Inactiva" };

function PrepareVersion({ list, detail, onDone }: { list: Header; detail: Schemas["PriceListDetail"] | null; onDone: () => void }) {
  const { companyId, can } = useSession();
  const prepare = useCommand<"/api/v1/companies/{companyId}/sales/prepare-price-list", Draft>(
    `prepare-price-list:${list.priceListId}`,
    "/api/v1/companies/{companyId}/sales/prepare-price-list",
  );
  const [draft, setDraft] = useState<Draft>(
    () =>
      prepare.restored ?? {
        lines: (detail?.lines ?? []).map((l) => ({ itemId: l.itemId, uom: l.uom, unitPrice: l.unitPrice })),
        freight: (detail?.freight ?? []).map((f) => ({ itemId: f.itemId, uom: f.uom, zoneId: f.zoneId, unitPrice: f.unitPrice })),
      },
  );
  const [invalid, setInvalid] = useState<string | null>(null);
  const fe = useFieldErrors();
  const messageId = useId();
  const masters = useLoad(
    can("master_data:read")
      ? async () => {
          const [items, zones] = await Promise.all([
            query("/api/v1/companies/{companyId}/master-data/items", { path: { companyId }, query: { status: "ACTIVE", limit: 200 } }),
            query("/api/v1/companies/{companyId}/sales/delivery-zones", { path: { companyId }, query: { status: "ACTIVE" } }),
          ]);
          return { goods: items.items.filter((i) => i.itemType === "FINISHED_GOOD"), zones: zones.items };
        }
      : null,
    [companyId],
  );
  const goods = masters.data?.goods ?? [];
  const zones = masters.data?.zones ?? [];
  const uomOf = (itemId: string) => goods.find((g) => g.itemId === itemId)?.baseUom ?? "";
  const setLine = (index: number, change: Partial<Line>) => setDraft((d) => ({ ...d, lines: d.lines.map((l, i) => (i === index ? { ...l, ...change } : l)) }));
  const setFreight = (index: number, change: Partial<FreightLine>) =>
    setDraft((d) => ({ ...d, freight: d.freight.map((l, i) => (i === index ? { ...l, ...change } : l)) }));
  const productSelect = (label: string, value: string, onChange: (itemId: string) => void, error: string | undefined, id: string) => (
    <>
      <select aria-label={label} value={value} onChange={(e) => onChange(e.target.value)} {...fieldAria(error, id, true)}>
        <option value="">Seleccione…</option>
        {goods.map((g) => (
          <option key={g.itemId} value={g.itemId}>
            {g.code} — {g.description}
          </option>
        ))}
      </select>
      <FieldMessage id={id} error={error} />
    </>
  );

  return (
    <section data-testid="prepare-version">
      <h2>Nueva versión de «{list.name}»</h2>
      <p className="muted">Se parte de la versión vigente. Lo que falte en una lista de cliente se cobra con el precio de «General»; el flete, no.</p>
      <h3>Precios por producto</h3>
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
          {draft.lines.map((line, index) => (
            <tr key={index}>
              <td className="wrap">
                {productSelect(`Producto ${index + 1}`, line.itemId, (itemId) => setLine(index, { itemId, uom: uomOf(itemId) }), fe.errors[`line-${index}-item`], `${messageId}-${index}-item`)}
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
                <button type="button" onClick={() => setDraft({ ...draft, lines: draft.lines.filter((_, i) => i !== index) })}>
                  Quitar
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </LineTable>
      <div className="actions">
        <button type="button" onClick={() => setDraft({ ...draft, lines: [...draft.lines, { itemId: "", uom: "", unitPrice: "" }] })}>
          Agregar producto
        </button>
      </div>
      <h3>Flete por producto y zona</h3>
      {zones.length === 0 ? (
        <p className="muted">No hay zonas de entrega activas: créelas en Maestros › Zonas de entrega para cobrar flete.</p>
      ) : (
        <>
          <LineTable testId="freight-table">
            <thead>
              <tr>
                <th>Producto</th>
                <th>Zona</th>
                <th className="num">Flete por unidad (RD$)</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {draft.freight.map((line, index) => (
                <tr key={index}>
                  <td className="wrap">
                    {productSelect(
                      `Producto del flete ${index + 1}`,
                      line.itemId,
                      (itemId) => setFreight(index, { itemId, uom: uomOf(itemId) }),
                      fe.errors[`freight-${index}-item`],
                      `${messageId}-f${index}-item`,
                    )}
                  </td>
                  <td>
                    <select aria-label={`Zona del flete ${index + 1}`} value={line.zoneId} onChange={(e) => setFreight(index, { zoneId: e.target.value })} {...fieldAria(fe.errors[`freight-${index}-zone`], `${messageId}-f${index}-zone`, true)}>
                      <option value="">Seleccione…</option>
                      {zones.map((z) => (
                        <option key={z.zoneId} value={z.zoneId}>
                          {z.name}
                        </option>
                      ))}
                    </select>
                    <FieldMessage id={`${messageId}-f${index}-zone`} error={fe.errors[`freight-${index}-zone`]} />
                  </td>
                  <td className="num">
                    <input
                      aria-label={`Flete ${index + 1}`}
                      inputMode="decimal"
                      value={line.unitPrice}
                      onChange={(e) => setFreight(index, { unitPrice: e.target.value })}
                      {...fieldAria(fe.errors[`freight-${index}-price`], `${messageId}-f${index}-price`, true)}
                    />
                    <FieldMessage id={`${messageId}-f${index}-price`} error={fe.errors[`freight-${index}-price`]} />
                  </td>
                  <td>
                    <button type="button" onClick={() => setDraft({ ...draft, freight: draft.freight.filter((_, i) => i !== index) })}>
                      Quitar
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </LineTable>
          <div className="actions">
            <button type="button" onClick={() => setDraft({ ...draft, freight: [...draft.freight, { itemId: "", uom: "", zoneId: "", unitPrice: "" }] })}>
              Agregar flete
            </button>
          </div>
        </>
      )}
      {invalid ? <div className="error">{invalid}</div> : null}
      <div className="actions form-actions">
        <button
          type="button"
          className="primary"
          disabled={prepare.busy}
          onClick={async () => {
            const lines = draft.lines.map((l) => ({ ...l, unitPrice: normalizeInput(l.unitPrice) }));
            const freight = draft.freight.map((l) => ({ ...l, unitPrice: normalizeInput(l.unitPrice) }));
            setInvalid(lines.length + freight.length === 0 ? "Agregue al menos un precio de producto o de flete." : null);
            const found: Record<string, string | false> = {};
            lines.forEach((l, i) => {
              found[`line-${i}-item`] = (!l.itemId || !l.uom) && "Elija el producto.";
              found[`line-${i}-price`] = !isPositiveDecimal(l.unitPrice, 4) && "Indique un precio mayor que cero (hasta 4 decimales).";
            });
            freight.forEach((l, i) => {
              found[`freight-${i}-item`] = (!l.itemId || !l.uom) && "Elija el producto.";
              found[`freight-${i}-zone`] = !l.zoneId && "Elija la zona.";
              found[`freight-${i}-price`] = !isPositiveDecimal(l.unitPrice, 4) && "Indique un flete mayor que cero (hasta 4 decimales).";
            });
            if (!fe.check(found) || lines.length + freight.length === 0) {
              return;
            }
            if (
              await prepare.run(
                { lines, freight, priceListId: list.priceListId },
                draft,
                `Versión de «${list.name}» con ${lines.length} precio(s) y ${freight.length} flete(s) preparada; falta su aprobación.`,
              )
            ) {
              onDone();
            }
          }}
        >
          Preparar versión
        </button>
      </div>
      <ErrorBox error={prepare.error ?? masters.error} />
    </section>
  );
}

function Version({ version, onDone }: { version: Schemas["PriceListSummary"]; onDone: () => void }) {
  const { companyId, can } = useSession();
  const approve = useCommand(
    `approve-price-list:${version.priceListVersionId}`,
    "/api/v1/companies/{companyId}/sales/approve-price-list",
    `«${version.priceListName}» versión ${version.version} aprobada: rige desde hoy.`,
  );
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
              title={`¿Aprobar «${version.priceListName}» versión ${version.version}?`}
              consequence="Rige desde hoy y reemplaza la versión vigente de esta lista: los pedidos y cotizaciones nuevos de sus clientes usarán estos precios y fletes. No se puede deshacer."
              onConfirm={async () => (await approve.run({ priceListVersionId: version.priceListVersionId })) && onDone()}
            />
          ) : null}
          <ErrorBox error={approve.error} />
        </td>
      </tr>
      {open ? (
        <tr>
          <td colSpan={7}>{data === null ? <LoadingIndicator error={error} /> : <VersionTables detail={data} />}</td>
        </tr>
      ) : null}
    </>
  );
}

function VersionTables({ detail, testId }: { detail: Schemas["PriceListDetail"]; testId?: string }) {
  return (
    <>
      {detail.lines.length > 0 ? (
        <div className="table-wrap">
          <table data-testid={testId}>
            <thead>
              <tr>
                <th>Producto</th>
                <th>Unidad</th>
                <th className="num">Precio sin ITBIS (RD$)</th>
              </tr>
            </thead>
            <tbody>
              {detail.lines.map((l) => (
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
      ) : (
        <p className="muted">Sin precios propios: sus productos se cobran con «General».</p>
      )}
      {detail.freight.length > 0 ? (
        <div className="table-wrap">
          <table data-testid={testId ? `${testId}-freight` : undefined}>
            <thead>
              <tr>
                <th>Flete de</th>
                <th>Zona</th>
                <th className="num">Por unidad (RD$, exento)</th>
              </tr>
            </thead>
            <tbody>
              {detail.freight.map((f) => (
                <tr key={`${f.itemId}:${f.uom}:${f.zoneId}`}>
                  <td className="wrap">
                    {f.itemCode} ({f.uom})
                  </td>
                  <td>{f.zoneName}</td>
                  <td className="num">
                    <Money value={f.unitPrice} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
    </>
  );
}

function NewList({ onDone }: { onDone: (priceListId: string) => void }) {
  const create = useCommand("create-price-list", "/api/v1/companies/{companyId}/sales/create-price-list");
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const fe = useFieldErrors();
  return (
    <section data-testid="new-list">
      <h2>Nueva lista</h2>
      <Field label="Código" required error={fe.errors.code} hint="Mayúsculas, dígitos o guion bajo: HOTELES, CONTRATISTAS…">
        <input aria-label="Código de la lista" value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} />
      </Field>
      <Field label="Nombre" required error={fe.errors.name}>
        <input aria-label="Nombre de la lista" maxLength={80} value={name} onChange={(e) => setName(e.target.value)} />
      </Field>
      <div className="actions form-actions">
        <button
          type="button"
          className="primary"
          disabled={create.busy}
          onClick={async () => {
            if (!fe.check({ code: !/^[A-Z0-9][A-Z0-9_]{1,29}$/.test(code.trim()) && "De 2 a 30 mayúsculas, dígitos o guion bajo.", name: !name.trim() && "Indique el nombre." })) {
              return;
            }
            const response = await create.run({ code: code.trim(), name: name.trim() }, undefined, `Lista «${name.trim()}» creada; prepare su primera versión.`);
            if (response) {
              onDone(response.resultRef);
            }
          }}
        >
          Crear lista
        </button>
      </div>
      <ErrorBox error={create.error} />
    </section>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const [selected, setSelected] = useState("");
  const [preparing, setPreparing] = useState(false);
  const [creating, setCreating] = useState(false);
  const deactivate = useCommand("deactivate-price-list", "/api/v1/companies/{companyId}/sales/deactivate-price-list");
  const reactivate = useCommand("reactivate-price-list", "/api/v1/companies/{companyId}/sales/reactivate-price-list");
  const headers = useLoad(can("sales:read") ? () => query("/api/v1/companies/{companyId}/sales/price-list-headers", { path: { companyId } }) : null, [companyId]);
  const list = headers.data?.items.find((l) => l.priceListId === selected) ?? headers.data?.items[0];
  const versions = useLoad(
    list
      ? async () => {
          const all = await query("/api/v1/companies/{companyId}/sales/price-lists", { path: { companyId }, query: { priceListId: list.priceListId } });
          const active = list.activeVersionId
            ? await query("/api/v1/companies/{companyId}/sales/price-lists/{priceListVersionId}", { path: { companyId, priceListVersionId: list.activeVersionId } })
            : null;
          return { all: all.items, active };
        }
      : null,
    [companyId, list?.priceListId, list?.activeVersionId],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (headers.data === null) {
    return <LoadingIndicator error={headers.error} />;
  }
  const reload = () => {
    headers.reload();
    versions.reload();
  };
  return (
    <>
      <div className="actions" style={{ justifyContent: "space-between" }}>
        <h1>Listas de precios</h1>
        {can("price_list:prepare") && !creating ? (
          <button type="button" onClick={() => setCreating(true)}>
            Nueva lista
          </button>
        ) : null}
      </div>
      <p className="muted">
        Cada cliente compra con su lista (en sus condiciones comerciales); lo que su lista no tenga se cobra con «General». El flete sale solo de la lista del cliente.
      </p>
      {creating ? (
        <NewList
          onDone={(id) => {
            setCreating(false);
            setSelected(id);
            headers.reload();
          }}
        />
      ) : null}
      <div className="table-wrap">
        <table data-testid="price-lists">
          <thead>
            <tr>
              <th>Lista</th>
              <th>Estado</th>
              <th>Versión vigente</th>
              <th className="num">Clientes</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {headers.data.items.map((l) => (
              <tr key={l.priceListId} data-testid={`price-list:${l.code}`} aria-selected={l.priceListId === list?.priceListId}>
                <td>
                  <button type="button" className="link" onClick={() => setSelected(l.priceListId)}>
                    {l.name}
                  </button>
                  <div className="muted">{l.code}</div>
                </td>
                <td>
                  <StatusBadge status={l.status} label={STATUS_LABELS[l.status]} />
                  {l.hasDraft ? <div className="muted">Con versión por aprobar</div> : null}
                </td>
                <td>{l.activeVersion ? `${l.activeVersion} (desde ${formatDate(l.activeFrom)})` : "—"}</td>
                <td className="num">{l.customers}</td>
                <td className="actions">
                  {can("price_list:prepare") && l.code !== "GENERAL" && l.status === "ACTIVE" ? (
                    <ConfirmAction
                      label="Desactivar"
                      danger
                      busy={deactivate.busy}
                      consequence={`«${l.name}» deja de ofrecerse. Solo se puede si ningún cliente la tiene en sus condiciones.`}
                      onConfirm={async () => (await deactivate.run({ priceListId: l.priceListId, expectedVersion: l.version }, undefined, `«${l.name}» desactivada.`)) && reload()}
                    />
                  ) : null}
                  {can("price_list:prepare") && l.status === "INACTIVE" ? (
                    <ConfirmAction
                      label="Reactivar"
                      busy={reactivate.busy}
                      consequence={`«${l.name}» vuelve a poder asignarse a clientes.`}
                      onConfirm={async () => (await reactivate.run({ priceListId: l.priceListId, expectedVersion: l.version }, undefined, `«${l.name}» reactivada.`)) && reload()}
                    />
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <ErrorBox error={deactivate.error ?? reactivate.error} />
      {list ? (
        <>
          <div className="actions" style={{ justifyContent: "space-between" }}>
            <h2>«{list.name}»: precios vigentes</h2>
            {can("price_list:prepare") && list.status === "ACTIVE" && !preparing ? (
              <button type="button" className="primary" onClick={() => setPreparing(true)}>
                Preparar nueva versión
              </button>
            ) : null}
          </div>
          {versions.data === null ? (
            <LoadingIndicator error={versions.error} />
          ) : (
            <>
              {versions.data.active ? (
                <VersionTables detail={versions.data.active} testId="current-prices" />
              ) : (
                <EmptyState title={`«${list.name}» todavía no tiene una versión vigente.`}>
                  <p>{list.code === "GENERAL" ? "Sin ella no se pueden tomar pedidos ni cotizar." : "Mientras tanto, sus clientes compran con «General» y sin flete."}</p>
                </EmptyState>
              )}
              {preparing ? (
                <PrepareVersion
                  key={list.priceListId}
                  list={list}
                  detail={versions.data.active}
                  onDone={() => {
                    setPreparing(false);
                    reload();
                  }}
                />
              ) : null}
              <h2>Versiones de «{list.name}»</h2>
              {versions.data.all.length === 0 ? (
                <p className="muted">No hay versiones.</p>
              ) : (
                <div className="table-wrap">
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
                      {versions.data.all.map((v) => (
                        <Version key={`${v.priceListVersionId}:${v.status}`} version={v} onDone={reload} />
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}
        </>
      ) : null}
    </>
  );
}
