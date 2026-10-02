"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { MoneyText, SalesHistory } from "@/components/SalesUx4";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, Money, NoPermission, ReasonAction, StatusBadge, useFieldErrors } from "@/components/ui";
import { amountToAssign, BUYER_ID_KINDS, cashSaleStep, paymentAmountError, paymentPendingReason } from "@/lib/cashSales";
import { formatQuantity, isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { DELIVERY_TERMS, formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { METHODS, orderDispatchable } from "@/lib/sales";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { bankAccountLabel } from "@/lib/ux4bSales";

type Order = Schemas["SalesOrderDetail"];
type Cash = Schemas["CashSaleView"];
type Bank = Schemas["SalesBankAccountList"]["items"][number];

// CF1-05 (E-CF1-05-2…9, 12, 13): a cash sale in three steps — products and buyer, payment, ready to dispatch. Caja records each
// payment and assigns it to the sale in one press (two commands; if the second fails the receipt stays listed, to be assigned).
// Every amount — total, collected, still to pay — is the server's.

interface PaymentValues {
  method: string;
  amount: string;
  valueDate: string;
  bankAccountId: string;
  reference: string;
  chequeBank: string;
  chequeNo: string;
  chequeDate: string;
}

function PaymentForm({ order, cash, banks, onDone }: { order: Order; cash: Cash; banks: readonly Bank[]; onDone: () => void }) {
  const { companyId } = useSession();
  const h = order.header;
  const stillToPay = cash.stillToPay ?? "0";
  const record = useCommand<"/api/v1/companies/{companyId}/sales/record-receipt", PaymentValues>(`cash-sale-receipt:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/record-receipt");
  const allocate = useCommand(`cash-sale-allocate:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/allocate-receipt-to-order");
  const today = todayInDominicanRepublic();
  const [typed, setTyped] = useState<PaymentValues | null>(() => record.restored ?? null);
  const values: PaymentValues = typed ?? { method: "CASH", amount: stillToPay, valueDate: today, bankAccountId: "", reference: "", chequeBank: "", chequeNo: "", chequeDate: today };
  const fe = useFieldErrors<string>();
  const set = (key: keyof PaymentValues) => (e: { target: { value: string } }) => setTyped({ ...values, [key]: e.target.value });
  const bank = values.bankAccountId || banks[0]?.bankAccountId || "";
  const transfer = values.method === "TRANSFER";
  const cheque = values.method === "CHEQUE";
  return (
    <form
      className="card"
      noValidate
      data-testid="payment-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const amount = normalizeInput(values.amount);
        const valid = isPositiveDecimal(amount, 2);
        if (
          !fe.check({
            amount: !valid ? "Indique un monto mayor que cero (hasta 2 decimales)." : (paymentAmountError(values.method, amount, stillToPay) ?? false),
            bankAccountId: transfer && !bank && "Una transferencia necesita la cuenta bancaria a la que llegó.",
            valueDate: transfer && !values.valueDate && "Indique la fecha valor de la transferencia.",
            chequeBank: cheque && values.chequeBank.trim() === "" && "Indique el banco del cheque.",
            chequeNo: cheque && values.chequeNo.trim() === "" && "Indique el número del cheque.",
            chequeDate: cheque && !values.chequeDate && "Indique la fecha del cheque.",
          })
        ) {
          return;
        }
        const optional = (v: string) => (v.trim() === "" ? null : v.trim());
        const response = await record.run(
          {
            partyId: h.partyId,
            method: values.method,
            amount,
            valueDate: transfer ? values.valueDate : null,
            bankAccountId: transfer ? bank : null,
            reference: optional(values.reference),
            chequeBank: cheque ? optional(values.chequeBank) : null,
            chequeNo: cheque ? optional(values.chequeNo) : null,
            chequeDate: cheque ? values.chequeDate : null,
          },
          values,
          (_r, doc) => (doc ? `Cobro ${doc} registrado.` : "Cobro registrado."),
        );
        if (!response) {
          return;
        }
        try {
          // E-CF1-05-3: the second command of the press. If it fails the receipt is listed below as received and not assigned.
          const receipt = await query("/api/v1/companies/{companyId}/sales/receipts/{receiptId}", { path: { companyId, receiptId: response.resultRef } });
          await allocate.run(
            { receiptId: response.resultRef, expectedVersion: receipt.header.version, salesOrderId: h.salesOrderId, amount: amountToAssign(amount, stillToPay) },
            undefined,
            `Cobro ${receipt.header.receiptNo} asignado a la venta ${h.orderNo}.`,
          );
        } finally {
          setTyped(null);
          onDone();
        }
      }}
    >
      <Field label="Medio" required>
        <select aria-label="Medio de cobro" value={values.method} onChange={set("method")}>
          {["CASH", "TRANSFER", "CHEQUE"].map((code) => (
            <option key={code} value={code}>
              {METHODS[code]}
            </option>
          ))}
        </select>
      </Field>
      <Field
        label="Monto (RD$)"
        required
        error={fe.errors.amount}
        hint={transfer ? "Si la transferencia es por más, el sobrante queda en el recibo para devolverlo por banco." : "Por lo que falta o menos; el vuelto se entrega en mano y no se registra."}
      >
        <input aria-label="Monto del cobro" inputMode="decimal" value={values.amount} onChange={set("amount")} />
      </Field>
      {transfer ? (
        <>
          <Field label="Cuenta de la empresa" required error={fe.errors.bankAccountId} hint="La cuenta nuestra donde entró la transferencia.">
            <select aria-label="Cuenta bancaria" value={bank} onChange={set("bankAccountId")}>
              {banks.map((b) => (
                <option key={b.bankAccountId} value={b.bankAccountId}>
                  {bankAccountLabel(b)}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Fecha valor" required error={fe.errors.valueDate} hint="El día en que el banco acreditó el dinero en nuestra cuenta.">
            <input type="date" aria-label="Fecha valor" value={values.valueDate} max={today} onChange={set("valueDate")} />
          </Field>
        </>
      ) : null}
      {cheque ? (
        <>
          <p className="notice" data-testid="cheque-notice">
            Un cheque cuenta cuando el banco lo acredite: la venta se podrá despachar después de depositarlo y conciliar el depósito con el extracto.
          </p>
          <Field label="Banco del cheque" required error={fe.errors.chequeBank}>
            <input value={values.chequeBank} onChange={set("chequeBank")} />
          </Field>
          <Field label="Número del cheque" required error={fe.errors.chequeNo}>
            <input value={values.chequeNo} onChange={set("chequeNo")} />
          </Field>
          <Field label="Fecha del cheque" required error={fe.errors.chequeDate}>
            <input type="date" value={values.chequeDate} max={today} onChange={set("chequeDate")} />
          </Field>
        </>
      ) : null}
      <Field label="Referencia (opcional)">
        <input value={values.reference} onChange={set("reference")} />
      </Field>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={record.busy || allocate.busy}>
          Cobrar
        </button>
      </div>
      <ErrorBox error={record.error ?? allocate.error} />
    </form>
  );
}

function Unassigned({ order, cash, onDone }: { order: Order; cash: Cash; onDone: () => void }) {
  const h = order.header;
  const allocate = useCommand(`cash-sale-assign:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/allocate-receipt-to-order");
  if (cash.unassigned.length === 0 || cash.stillToPay === null || !isPositiveDecimal(cash.stillToPay, 2)) {
    return null;
  }
  const stillToPay = cash.stillToPay;
  return (
    <>
      <h3>Recibido sin asignar</h3>
      <p className="muted">Cobros del consumidor final con dinero que no está aplicado ni asignado a ninguna venta. Asigne aquí el que corresponda a esta.</p>
      <ul data-testid="unassigned-receipts">
        {cash.unassigned.map((r) => (
          <li key={r.receiptId}>
            <Link href={`/cobros/recibo/?id=${r.receiptId}`}>{r.receiptNo}</Link> · {METHODS[r.method] ?? r.method} · {formatDate(r.receiptDate)} · disponible <MoneyText value={r.available} />{" "}
            <button
              type="button"
              disabled={allocate.busy}
              onClick={async () =>
                (await allocate.run(
                  { receiptId: r.receiptId, expectedVersion: r.version, salesOrderId: h.salesOrderId, amount: amountToAssign(r.available, stillToPay) },
                  undefined,
                  `Cobro ${r.receiptNo} asignado a la venta ${h.orderNo}.`,
                )) && onDone()
              }
            >
              Asignar {r.receiptNo}
            </button>
          </li>
        ))}
      </ul>
      <ErrorBox error={allocate.error} />
    </>
  );
}

function Payments({ order, cash, onDone }: { order: Order; cash: Cash; onDone: () => void }) {
  const { can } = useSession();
  const h = order.header;
  const release = useCommand(`cash-sale-release:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/release-order-allocation");
  if (cash.payments.length === 0) {
    return <p className="muted">Todavía no hay cobros asignados a esta venta.</p>;
  }
  return (
    <>
      <div className="table-wrap"><table data-testid="cash-payments">
        <thead>
          <tr>
            <th>Recibo</th>
            <th>Fecha</th>
            <th>Medio</th>
            <th className="num">Asignado (RD$)</th>
            <th>Situación</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {cash.payments.map((p) => (
            <tr key={p.allocationEventId}>
              <td className="mono">
                <Link href={`/cobros/recibo/?id=${p.receiptId}`}>{p.receiptNo}</Link>
              </td>
              <td>{formatDate(p.receiptDate)}</td>
              <td>{METHODS[p.method] ?? p.method}</td>
              <td className="num">
                <Money value={p.amount} />
              </td>
              <td className="wrap">{paymentPendingReason(p) ?? "Cuenta para despachar"}</td>
              <td className="actions">
                {h.status === "PENDING_PAYMENT" && can("receipt:apply") ? (
                  <ReasonAction
                    label={`Quitar ${p.receiptNo}`}
                    busy={release.busy}
                    consequence={`El cobro ${p.receiptNo} deja de estar asignado a la venta ${h.orderNo}; el dinero queda en el recibo.`}
                    onConfirm={async (reason) => (await release.run({ receiptId: p.receiptId, allocationEventId: p.allocationEventId, reason })) && onDone()}
                  />
                ) : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table></div>
      <ErrorBox error={release.error} />
    </>
  );
}

function Actions({ order, cash, hasDeliveries, onDone }: { order: Order; cash: Cash; hasDeliveries: boolean; onDone: () => void }) {
  const { can } = useSession();
  const h = order.header;
  const target = { salesOrderId: h.salesOrderId, expectedVersion: h.version };
  const submit = useCommand(`submit-cash-sale:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/submit-cash-sale-for-payment", `Venta ${h.orderNo} enviada a pago.`);
  const back = useCommand(`return-cash-sale:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/return-cash-sale-to-draft", `Venta ${h.orderNo} de vuelta en borrador.`);
  const confirm = useCommand(`confirm-cash-sale:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/confirm-cash-sale", `Venta ${h.orderNo} pagada: lista para despachar.`);
  const cancel = useCommand(`cancel-cash-sale:${h.salesOrderId}`, "/api/v1/companies/{companyId}/sales/cancel-cash-sale", `Venta ${h.orderNo} cancelada.`);
  const busy = submit.busy || back.busy || confirm.busy || cancel.busy;
  const after = (response: unknown) => response && onDone();
  const sells = can("cash_sale:create");
  const paid = cash.payments.length > 0;
  return (
    <>
      <div className="actions">
        {h.status === "DRAFT" && sells ? (
          <>
            <Link className="button" href={`/ventas/contado/nueva/?id=${h.salesOrderId}`}>
              Editar
            </Link>
            <button type="button" className="primary" disabled={busy} onClick={async () => after(await submit.run(target))}>
              Enviar a pago
            </button>
          </>
        ) : null}
        {h.status === "PENDING_PAYMENT" && sells ? (
          <>
            {paid ? (
              <button type="button" className={cash.covered ? "primary" : undefined} disabled={busy} onClick={async () => after(await confirm.run({ salesOrderId: h.salesOrderId }))}>
                Verificar pago
              </button>
            ) : (
              <button type="button" disabled={busy} onClick={async () => after(await back.run(target))}>
                Volver a borrador
              </button>
            )}
          </>
        ) : null}
        {/* E-CF1-05-14: a covered sale still pending is confirmed when its first delivery is planned. */}
        {(orderDispatchable(h.status) || (h.status === "PENDING_PAYMENT" && cash.covered)) && can("delivery:manage") ? (
          <Link className="button primary" href={`/despacho/planificar/?pedido=${h.salesOrderId}`}>
            Planificar conduce
          </Link>
        ) : null}
        {(h.status === "DRAFT" || h.status === "PENDING_PAYMENT" || (h.status === "CONFIRMED" && !hasDeliveries)) && sells ? (
          <ReasonAction
            label="Cancelar venta"
            busy={busy}
            consequence={
              paid
                ? `La venta ${h.orderNo} queda cancelada y sus cobros se liberan: el dinero queda en los recibos para devolverlo al cliente por banco (transferencia o cheque), nunca en efectivo. No se puede deshacer.`
                : `La venta ${h.orderNo} queda cancelada. No se puede deshacer.`
            }
            onConfirm={async (reason) => after(await cancel.run({ ...target, reason }))}
          />
        ) : null}
      </div>
      <ErrorBox error={submit.error ?? back.error ?? confirm.error ?? cancel.error} />
    </>
  );
}

function Steps({ status }: { status: string }) {
  const step = cashSaleStep(status);
  const names: [string, boolean][] = [
    ["1. Productos y comprador", step === "PRODUCTS"],
    ["2. Cobro", step === "PAYMENT"],
    ["3. Lista para despachar", step === "DISPATCH" || step === "DONE"],
  ];
  return (
    <ol className="actions" data-testid="cash-sale-steps" style={{ listStyle: "none", paddingLeft: 0 }}>
      {names.map(([name, current]) => (
        <li key={name} className={current ? "badge tone-attention" : "badge"} aria-current={current ? "step" : undefined}>
          {name}
        </li>
      ))}
    </ol>
  );
}

function CashSaleDetail() {
  const { companyId, can, plantName } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const collects = can("receipt:record") && can("receipt:apply");
  const { data, error, reload } = useLoad(
    can("sales:read") && id
      ? async () => {
          const [order, deliveries, banks] = await Promise.all([
            query("/api/v1/companies/{companyId}/sales/orders/{salesOrderId}", { path: { companyId, salesOrderId: id } }),
            query("/api/v1/companies/{companyId}/sales/deliveries", { path: { companyId }, query: { salesOrderId: id, limit: 200 } }),
            collects ? query("/api/v1/companies/{companyId}/sales/bank-accounts", { path: { companyId } }) : Promise.resolve({ items: [] }),
          ]);
          return { order, deliveries: deliveries.items, banks: banks.items };
        }
      : null,
    [companyId, id, collects],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const { order, deliveries } = data;
  const h = order.header;
  const cash = order.cashSale;
  if (!cash) {
    return (
      <p>
        {h.orderNo} es un pedido a crédito. <Link href={`/ventas/pedido/?id=${h.salesOrderId}`}>Ver el pedido</Link>
      </p>
    );
  }
  const open = h.status === "PENDING_PAYMENT" || orderDispatchable(h.status);
  const owes = cash.stillToPay !== null && isPositiveDecimal(cash.stillToPay, 2);
  return (
    <>
      <p>
        <Link href="/ventas/contado/">← Ventas de contado</Link>
      </p>
      <h1>
        Venta de contado {h.orderNo} <StatusBadge status={h.status} testId="order-status" />
      </h1>
      {h.status === "CANCELLED" ? null : <Steps status={h.status} />}
      <p>
        {formatDate(h.orderDate)} · planta {plantName(h.plantCode)} · {DELIVERY_TERMS[h.deliveryTermCode] ?? h.deliveryTermCode}
        {order.siteAddress ? ` · obra: ${order.siteAddress}` : ""}
        {order.requestedDate ? ` · solicitado para ${formatDate(order.requestedDate)}` : ""}
      </p>
      <p data-testid="cash-buyer">
        Comprador: {cash.buyerName ?? "sin nombre"}
        {cash.buyerPhone ? ` · tel. ${cash.buyerPhone}` : ""}
        {cash.buyerIdKind && cash.buyerId ? ` · ${BUYER_ID_KINDS[cash.buyerIdKind] ?? cash.buyerIdKind} ${cash.buyerId}` : " · sin identificación"}
      </p>
      {order.cancelReason ? <p className="muted">Motivo: {order.cancelReason}</p> : null}
      <Actions order={order} cash={cash} hasDeliveries={deliveries.length > 0} onDone={reload} />
      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th className="num">#</th>
            <th>Producto</th>
            <th>Unidad</th>
            <th className="num">Cantidad</th>
            <th className="num">Precio (RD$)</th>
            <th className="num">Neto (RD$)</th>
            <th className="num">Entregado</th>
            <th className="num">Facturado</th>
          </tr>
        </thead>
        <tbody>
          {order.lines.map((l) => (
            <tr key={l.salesOrderLineId}>
              <td className="num">{l.lineNo}</td>
              <td>
                {l.itemCode} — {l.itemDescription}
              </td>
              <td>{l.uom}</td>
              <td className="num">{formatQuantity(l.qtyOrdered)}</td>
              <td className="num">
                <Money value={l.unitPrice} />
              </td>
              <td className="num">
                <Money value={l.netAmount} />
              </td>
              <td className="num">{formatQuantity(l.qtyDelivered)}</td>
              <td className="num">{formatQuantity(l.qtyInvoiced)}</td>
            </tr>
          ))}
          <tr>
            <th colSpan={5}>Total neto (sin ITBIS, RD$)</th>
            <td className="num">
              <Money value={h.totalNet} testId="order-total" />
            </td>
            <td colSpan={2} />
          </tr>
        </tbody>
      </table></div>

      <h2>Cobro</h2>
      {cash.paymentTotal === null ? (
        <p className="muted" data-testid="cash-not-sent">
          El total a pagar, con el ITBIS del día, queda fijo al enviar la venta a pago.
        </p>
      ) : (
        <dl className="preview-box" data-testid="cash-totals">
          <dt>ITBIS</dt>
          <dd>
            <MoneyText value={cash.itbis} testId="cash-itbis" />
          </dd>
          <dt>Total a pagar</dt>
          <dd>
            <MoneyText value={cash.paymentTotal} testId="cash-total" />
          </dd>
          <dt>Cobros asignados</dt>
          <dd>
            <MoneyText value={cash.assigned} testId="cash-assigned" />
          </dd>
          {isPositiveDecimal(cash.invoiced, 2) ? (
            <>
              <dt>Ya aplicado a facturas</dt>
              <dd>
                <MoneyText value={cash.invoiced} testId="cash-invoiced" />
              </dd>
            </>
          ) : null}
          <dt>Cuenta para despachar</dt>
          <dd>
            <MoneyText value={cash.counted} testId="cash-counted" />
          </dd>
          <dt>Falta por cobrar</dt>
          <dd>
            <MoneyText value={cash.stillToPay} testId="cash-due" />
          </dd>
        </dl>
      )}
      {cash.paymentTotal !== null ? <Payments order={order} cash={cash} onDone={reload} /> : null}
      {open && !cash.covered && !owes ? (
        <p className="notice" data-testid="cash-waiting-bank">
          La venta está cobrada, pero parte del dinero todavía no cuenta: un cheque cuenta cuando su depósito se concilia con el extracto del banco.
          Después se confirma sola al planificar el primer conduce, o antes con «Verificar pago».
        </p>
      ) : null}
      {open && owes ? (
        collects ? (
          <>
            <h3>Registrar un cobro</h3>
            <PaymentForm key={`${h.version}:${cash.stillToPay}`} order={order} cash={cash} banks={data.banks} onDone={reload} />
            <Unassigned order={order} cash={cash} onDone={reload} />
          </>
        ) : (
          <p className="notice" data-testid="cash-only-caja">
            El cobro lo registra Caja: indique al cliente el número {h.orderNo} para pagar.
          </p>
        )
      ) : null}

      <h2>Conduces</h2>
      {deliveries.length === 0 ? (
        <EmptyState
          title={orderDispatchable(h.status) ? "Pagada y sin conduces todavía." : "Los conduces se planifican cuando la venta está pagada."}
          steps={[orderDispatchable(h.status) && can("delivery:manage") && { href: `/despacho/planificar/?pedido=${h.salesOrderId}`, label: "Planificar el primer conduce" }]}
        />
      ) : (
        <ul>
          {deliveries.map((d) => (
            <li key={d.deliveryId}>
              <Link href={`/despacho/conduce/?id=${d.deliveryId}`}>{d.deliveryNo}</Link> — <StatusBadge status={d.status} />
            </li>
          ))}
        </ul>
      )}
      <SalesHistory history={order.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <CashSaleDetail />
    </Suspense>
  );
}
