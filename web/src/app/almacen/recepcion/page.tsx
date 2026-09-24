"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { History } from "@/components/History";
import { AccountingStatus, ErrorBox, Field, Loading, NoPermission, ReasonAction } from "@/components/ui";
import { formatDecimal, formatQuantity, isDecimal, normalizeInput } from "@/lib/decimal";
import { formatDateTime, statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Receipt = Schemas["GoodsReceiptDetail"];

interface CorrectionValues {
  lineId: string;
  delta: string;
  reason: string;
  evidence: string;
}

/** E-8 §5.4: a quantity correction of one line (signed), with the evidence reference (E-PR11-1). */
function CorrectionForm({ receipt, onDone }: { receipt: Receipt; onDone: () => void }) {
  const create = useCommand<"/api/v1/companies/{companyId}/procurement/create-receipt-correction", CorrectionValues>(
    `correct-gr:${receipt.goodsReceiptId}`,
    "/api/v1/companies/{companyId}/procurement/create-receipt-correction",
  );
  const [values, setValues] = useState<CorrectionValues>(() => create.restored ?? { lineId: receipt.lines[0]?.grLineId ?? "", delta: "", reason: "", evidence: "" });
  const [invalid, setInvalid] = useState<string | null>(null);

  const submit = async () => {
    const delta = normalizeInput(values.delta);
    if (!values.lineId || !isDecimal(delta, 6) || !/[1-9]/.test(delta) || !values.reason.trim() || !values.evidence.trim()) {
      setInvalid("Indique la línea, la diferencia (positiva o negativa, distinta de cero), el motivo y la evidencia.");
      return;
    }
    setInvalid(null);
    const response = await create.run(
      {
        plantId: receipt.plantId,
        goodsReceiptId: receipt.goodsReceiptId,
        goodsReceiptLineId: values.lineId,
        deltaQuantity: delta,
        reason: values.reason.trim(),
        evidenceReference: values.evidence.trim(),
      },
      values,
    );
    if (response) {
      setValues({ ...values, delta: "", reason: "", evidence: "" });
      onDone();
    }
  };

  return (
    <>
      <h2>Corregir cantidad</h2>
      <Field label="Línea">
        <select aria-label="Línea a corregir" value={values.lineId} onChange={(e) => setValues({ ...values, lineId: e.target.value })}>
          {receipt.lines.map((l) => (
            <option key={l.grLineId} value={l.grLineId}>
              {l.itemCode} — {formatQuantity(l.qty)} (lote {l.lotCode})
            </option>
          ))}
        </select>
      </Field>
      <Field label="Diferencia (+/−)">
        <input aria-label="Diferencia" inputMode="decimal" value={values.delta} onChange={(e) => setValues({ ...values, delta: e.target.value })} />
      </Field>
      <Field label="Motivo">
        <input aria-label="Motivo de la corrección" value={values.reason} onChange={(e) => setValues({ ...values, reason: e.target.value })} />
      </Field>
      <Field label="Evidencia (ticket corregido, foto o registro)">
        <input aria-label="Evidencia" value={values.evidence} onChange={(e) => setValues({ ...values, evidence: e.target.value })} />
      </Field>
      <div className="actions">
        <button type="button" disabled={create.busy} onClick={submit}>
          Registrar corrección
        </button>
      </div>
      {invalid ? <div className="error">{invalid}</div> : null}
      <ErrorBox error={create.error} />
    </>
  );
}

function ReceiptDetail() {
  const { companyId, can, plantFor } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const plantId = plantFor("goods_receipt:read");
  const { data: receipt, error, reload } = useLoad(
    can("goods_receipt:read") && id
      ? () => query("/api/v1/companies/{companyId}/procurement/goods-receipts/{goodsReceiptId}", { path: { companyId, goodsReceiptId: id }, query: { plantId } })
      : null,
    [companyId, id, plantId],
  );
  const reverse = useCommand(`reverse-gr:${id}`, "/api/v1/companies/{companyId}/procurement/reverse-goods-receipt");

  if (!can("goods_receipt:read")) {
    return <NoPermission />;
  }
  if (receipt === null) {
    return <Loading error={error} />;
  }
  return (
    <>
      <h1>Recepción {receipt.grNo}</h1>
      <dl className="facts">
        <dt>Orden</dt>
        <dd>{can("purchase_order:read") ? <Link href={`/compras/orden/?id=${receipt.purchaseOrderId}`}>{receipt.poNo}</Link> : receipt.poNo}</dd>
        <dt>Ubicación</dt>
        <dd>{receipt.locationCode}</dd>
        <dt>Fecha y hora</dt>
        <dd>{formatDateTime(receipt.occurredAt)}</dd>
        <dt>Ticket de báscula</dt>
        <dd>{receipt.weighTicketRef ?? "—"}</dd>
        <dt>Estado</dt>
        <dd>{statusLabel(receipt.documentStatus)}</dd>
        <dt>Contabilidad</dt>
        <dd>
          <AccountingStatus status={receipt.accountingStatus} eventId={receipt.postingEventId} />
        </dd>
      </dl>
      <table>
        <thead>
          <tr>
            <th>Artículo</th>
            <th>Lote</th>
            <th>Lote del proveedor</th>
            <th className="num">Cantidad</th>
            <th className="num">Precio</th>
          </tr>
        </thead>
        <tbody>
          {receipt.lines.map((l) => (
            <tr key={l.grLineId}>
              <td>{l.itemCode}</td>
              <td>{l.lotCode}</td>
              <td>{l.supplierLotNumber ?? "—"}</td>
              <td className="num">{formatQuantity(l.qty)}</td>
              <td className="num">{formatDecimal(l.unitPrice)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {receipt.reversal ? (
        <p>
          Reversada: {receipt.reversal.reason} — <AccountingStatus status={receipt.reversal.accountingStatus} eventId={receipt.reversal.postingEventId} />
        </p>
      ) : null}
      {receipt.documentStatus === "POSTED" && can("goods_receipt:reverse") ? (
        <div className="actions">
          <ReasonAction
            label="Reversar recepción"
            busy={reverse.busy}
            onConfirm={async (reason) => {
              if (await reverse.run({ goodsReceiptId: receipt.goodsReceiptId, reason })) {
                reload();
              }
            }}
          />
        </div>
      ) : null}
      <ErrorBox error={reverse.error} />
      <h2>Correcciones</h2>
      {receipt.corrections.length === 0 ? (
        <p className="muted">Sin correcciones.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th className="num">Diferencia</th>
              <th>Motivo</th>
              <th>Evidencia</th>
              <th>Estado</th>
              <th>Contabilidad</th>
            </tr>
          </thead>
          <tbody>
            {receipt.corrections.map((c) => (
              <tr key={c.correctionId}>
                <td className="num">{formatQuantity(c.deltaQty)}</td>
                <td>{c.reason}</td>
                <td>{c.evidenceObjectKey}</td>
                <td>{statusLabel(c.documentStatus)}</td>
                <td>
                  <AccountingStatus status={c.accountingStatus} eventId={c.postingEventId} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {receipt.documentStatus !== "REVERSED" && can("receipt_correction:create") ? <CorrectionForm receipt={receipt} onDone={reload} /> : null}
      <History history={receipt.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <ReceiptDetail />
    </Suspense>
  );
}
