"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { QuoteStatusBadge } from "@/components/QuoteStatus";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Money, NoPermission, ReasonAction } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { addDays, DELIVERY_TERMS, formatDate, formatDateTime, todayInDominicanRepublic } from "@/lib/labels";
import { DEFAULT_QUOTE_VALIDITY_DAYS, quoteActions, quoteStatusLabel } from "@/lib/quotes";
import { QuoteMail } from "@/components/DocumentMail";
import { quoteMailBlocked } from "@/lib/mail";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Quote = Schemas["QuoteDetail"];

// QUO1-04 (E-QUO1-04-4): a quote — header, lines with the list and the quoted price, the price approval (who, when, and whether it
// covers the current lines), copies and the order it became, history; actions by status and permission: the Vendedor edits,
// submits special prices for approval, sends, marks lost, cancels, copies and converts into an order; the Aprobador de políticas
// approves the prices (step-up) or returns the quote to draft with a reason.

function CopyAction({ quoteId, busy }: { quoteId: string; busy: boolean }) {
  const router = useRouter();
  const copy = useCommand(`copy-quote:${quoteId}`, "/api/v1/companies/{companyId}/sales/copy-quote", (_, doc) => (doc ? `Copia ${doc} creada en borrador.` : "Copia de la cotización creada en borrador."));
  const [open, setOpen] = useState(false);
  const [validUntil, setValidUntil] = useState(() => addDays(todayInDominicanRepublic(), DEFAULT_QUOTE_VALIDITY_DAYS));
  if (!open) {
    return (
      <button type="button" onClick={() => setOpen(true)}>
        Copiar
      </button>
    );
  }
  return (
    <span className="inline-form">
      <label className="field">
        <span className="is-required">
          Vigente hasta (copia)
        </span>
        <input type="date" aria-label="Vigente hasta (copia)" aria-required value={validUntil} onChange={(e) => setValidUntil(e.target.value)} />
      </label>
      <button
        type="button"
        className="primary"
        disabled={busy || copy.busy || !validUntil}
        onClick={async () => {
          const response = await copy.run({ quoteId, validUntil });
          if (response) {
            setOpen(false);
            router.push(`/ventas/cotizacion/?id=${response.resultRef}`);
          }
        }}
      >
        Confirmar copia
      </button>
      <button type="button" onClick={() => setOpen(false)}>
        Cancelar
      </button>
      <ErrorBox error={copy.error} />
    </span>
  );
}

function Actions({ quote, onDone }: { quote: Quote; onDone: () => void }) {
  const { can } = useSession();
  const router = useRouter();
  const h = quote.header;
  const target = { quoteId: h.quoteId, expectedVersion: h.version };
  const approvalPending = h.specialPrices && !quote.priceApprovalCurrent;
  const actions = quoteActions(h.status, h.expired, approvalPending, can);
  const submit = useCommand(`submit-quote:${h.quoteId}`, "/api/v1/companies/{companyId}/sales/submit-quote-for-approval", `Cotización ${h.quoteNo} enviada a aprobación de precios.`);
  const approve = useCommand(`approve-quote:${h.quoteId}`, "/api/v1/companies/{companyId}/sales/approve-quote-prices", `Precios de la cotización ${h.quoteNo} aprobados.`);
  const giveBack = useCommand(`return-quote:${h.quoteId}`, "/api/v1/companies/{companyId}/sales/return-quote-to-draft", `Cotización ${h.quoteNo} devuelta a borrador.`);
  const send = useCommand(`send-quote:${h.quoteId}`, "/api/v1/companies/{companyId}/sales/send-quote", `Cotización ${h.quoteNo} marcada como enviada al cliente.`);
  const lost = useCommand(`lost-quote:${h.quoteId}`, "/api/v1/companies/{companyId}/sales/mark-quote-lost", `Cotización ${h.quoteNo} marcada como perdida.`);
  const cancel = useCommand(`cancel-quote:${h.quoteId}`, "/api/v1/companies/{companyId}/sales/cancel-quote", `Cotización ${h.quoteNo} cancelada.`);
  const convert = useCommand(`convert-quote:${h.quoteId}`, "/api/v1/companies/{companyId}/sales/convert-quote", (_, doc) => (doc ? `Cotización ${h.quoteNo} convertida en el pedido ${doc}.` : `Cotización ${h.quoteNo} convertida en pedido.`));
  const busy = submit.busy || approve.busy || giveBack.busy || send.busy || lost.busy || cancel.busy || convert.busy;
  const after = (response: unknown) => response && onDone();
  return (
    <>
      <div className="actions no-print">
        {actions.includes("EDIT") ? (
          <Link className="button" href={`/ventas/cotizaciones/nueva/?id=${h.quoteId}`}>
            Editar
          </Link>
        ) : null}
        {actions.includes("SUBMIT") ? (
          <button type="button" className="primary" disabled={busy} onClick={async () => after(await submit.run(target))}>
            Enviar a aprobación de precios
          </button>
        ) : null}
        {actions.includes("SEND") ? (
          <button type="button" className="primary" disabled={busy} onClick={async () => after(await send.run(target))}>
            Marcar enviada al cliente
          </button>
        ) : null}
        {actions.includes("APPROVE") ? (
          <ConfirmAction
            label="Aprobar precios"
            className="primary"
            busy={busy}
            stepUp
            consequence={`Los precios por debajo de la lista de la cotización ${h.quoteNo} quedan aprobados y el Vendedor podrá enviarla al cliente y convertirla en pedido a esos precios.`}
            onConfirm={async () => after(await approve.run(target))}
          />
        ) : null}
        {actions.includes("RETURN") ? (
          <ReasonAction
            label="Devolver a borrador"
            busy={busy}
            consequence={`La cotización ${h.quoteNo} vuelve a borrador para que el Vendedor corrija los precios.`}
            onConfirm={async (reason) => after(await giveBack.run({ ...target, reason }))}
          />
        ) : null}
        {actions.includes("CONVERT") ? (
          <ConfirmAction
            label="Convertir en pedido"
            className="primary"
            busy={busy}
            consequence={`Se crea un pedido en borrador con las líneas y los precios de la cotización ${h.quoteNo}, y la cotización queda convertida (no se puede volver a convertir).`}
            onConfirm={async () => {
              const response = await convert.run(target);
              if (response) {
                router.push(`/ventas/pedido/?id=${response.resultRef}`);
              }
            }}
          />
        ) : null}
        <Link className="button" href={`/ventas/cotizacion/imprimir/?id=${h.quoteId}`}>
          Imprimir cotización
        </Link>
        {actions.includes("COPY") ? <CopyAction quoteId={h.quoteId} busy={busy} /> : null}
        {actions.includes("LOST") ? (
          <ReasonAction
            label="Marcar perdida"
            busy={busy}
            consequence={`La cotización ${h.quoteNo} queda cerrada como perdida y ya no se podrá convertir en pedido. No se puede deshacer.`}
            onConfirm={async (reason) => after(await lost.run({ ...target, reason }))}
          />
        ) : null}
        {actions.includes("CANCEL") ? (
          <ReasonAction
            label="Cancelar cotización"
            busy={busy}
            consequence={`La cotización ${h.quoteNo} queda cancelada y ya no se podrá enviar ni convertir. No se puede deshacer.`}
            onConfirm={async (reason) => after(await cancel.run({ ...target, reason }))}
          />
        ) : null}
      </div>
      {h.status === "SENT" && h.expired && can("quote:manage") ? <p className="notice">La cotización está vencida: cópiela con una nueva vigencia para ofrecerla de nuevo.</p> : null}
      {h.status === "DRAFT" && approvalPending && can("quote:manage") ? (
        <p className="notice">Hay precios por debajo de la lista: la cotización se envía al cliente después de que otra persona autorizada los apruebe.</p>
      ) : null}
      <ErrorBox error={submit.error ?? approve.error ?? giveBack.error ?? send.error ?? lost.error ?? cancel.error ?? convert.error} />
    </>
  );
}

function QuoteDetail() {
  const { companyId, can, plantName } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("sales:read") && id ? () => query("/api/v1/companies/{companyId}/sales/quotes/{quoteId}", { path: { companyId, quoteId: id } }) : null,
    [companyId, id],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const h = data.header;
  return (
    <>
      <p>
        <Link href="/ventas/cotizaciones/">← Cotizaciones</Link>
      </p>
      <h1>
        Cotización {h.quoteNo} <QuoteStatusBadge status={h.status} expired={h.expired} testId="quote-status" />
      </h1>
      <dl className="facts">
        <dt>Cliente</dt>
        <dd>{h.customerName}</dd>
        <dt>Fecha · vigente hasta</dt>
        <dd>
          {formatDate(h.quoteDate)} · {formatDate(h.validUntil)}
        </dd>
        <dt>Planta · entrega</dt>
        <dd>
          {plantName(h.plantCode)} · {DELIVERY_TERMS[h.deliveryTermCode] ?? h.deliveryTermCode}
          {data.siteAddress ? ` · obra: ${data.siteAddress}` : ""}
        </dd>
        {data.customerRef ? (
          <>
            <dt>Referencia del cliente</dt>
            <dd>{data.customerRef}</dd>
          </>
        ) : null}
        {data.notes ? (
          <>
            <dt>Notas</dt>
            <dd>{data.notes}</dd>
          </>
        ) : null}
        <dt>Preparada por</dt>
        <dd>{h.createdBy ?? "—"}</dd>
        {data.copiedFrom ? (
          <>
            <dt>Copia de</dt>
            <dd>
              <Link href={`/ventas/cotizacion/?id=${data.copiedFrom.quoteId}`}>{data.copiedFrom.quoteNo}</Link> ({quoteStatusLabel(data.copiedFrom.status)})
            </dd>
          </>
        ) : null}
        {data.copies.length > 0 ? (
          <>
            <dt>Copias</dt>
            <dd>
              {data.copies.map((c, i) => (
                <span key={c.quoteId}>
                  {i > 0 ? ", " : ""}
                  <Link href={`/ventas/cotizacion/?id=${c.quoteId}`}>{c.quoteNo}</Link> ({quoteStatusLabel(c.status)})
                </span>
              ))}
            </dd>
          </>
        ) : null}
        {data.salesOrderId ? (
          <>
            <dt>Pedido</dt>
            <dd data-testid="quote-order">
              <Link href={`/ventas/pedido/?id=${data.salesOrderId}`}>{data.orderNo}</Link>
            </dd>
          </>
        ) : null}
        {data.closingReason ? (
          <>
            <dt>Motivo de cierre</dt>
            <dd>{data.closingReason}</dd>
          </>
        ) : null}
      </dl>
      <Actions quote={data} onDone={reload} />

      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th className="num">#</th>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Cantidad</th>
            <th className="num">Precio de lista (RD$)</th>
            <th className="num">Precio cotizado (RD$)</th>
            <th className="num">Neto (RD$)</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => (
            <tr key={l.lineNo}>
              <td className="num">{l.lineNo}</td>
              <td>
                {l.itemCode} — {l.itemDescription}
              </td>
              <td>{l.uom}</td>
              <td className="num">{formatQuantity(l.quantity)}</td>
              <td className="num">
                <Money value={l.listPrice} />
              </td>
              <td className="num">
                <Money value={l.unitPrice} testId={`quote-price:${l.lineNo}`} />
              </td>
              <td className="num">
                <Money value={l.netAmount} />
              </td>
              <td>{l.special ? <span className="badge tone-attention">Precio especial</span> : null}</td>
            </tr>
          ))}
          <tr>
            <th colSpan={6}>Total neto (sin ITBIS, RD$)</th>
            <td className="num">
              <Money value={h.totalNet} testId="quote-total" />
            </td>
            <td />
          </tr>
        </tbody>
      </table></div>

      <h2>Aprobación de precios</h2>
      {data.priceApprovedBy ? (
        <p data-testid="quote-approval">
          Aprobados por {data.priceApprovedBy} el {formatDateTime(data.priceApprovedAt)} ·{" "}
          {data.priceApprovalCurrent ? "cubre las líneas actuales" : "no cubre las líneas actuales (se editaron después): requiere una nueva aprobación"}
        </p>
      ) : h.specialPrices ? (
        <p className="muted" data-testid="quote-approval">
          Hay precios por debajo de la lista sin aprobar.
        </p>
      ) : (
        <p className="muted" data-testid="quote-approval">
          Todas las líneas están a precio de lista o por encima: no requiere aprobación.
        </p>
      )}
      {/* MAIL-03 (E-MAIL-01-7): what prints without a watermark is e-mailed — sent and valid, or converted. */}
      <QuoteMail
        quoteId={h.quoteId}
        quoteNo={h.quoteNo}
        blocked={quoteMailBlocked(h.status, h.expired)}
      />
      <History history={data.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <QuoteDetail />
    </Suspense>
  );
}
