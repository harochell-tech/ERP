"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { query } from "@/api/client";
import { History } from "@/components/History";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Money, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// USD1-07a (E-USD1-07-4, E-USD1-04-4…7): one import settlement — its documents and the server's allocation of the cost by value over the
// goods' lines. Cuentas por pagar cancels the draft; the Controller (someone else) approves it, posting it on its date, or reverses it,
// freeing its documents.

const STATUS: Readonly<Record<string, string>> = {
  DRAFT: "Borrador",
  POSTED: "Contabilizada",
  REVERSED: "Reversada",
  CANCELLED: "Cancelada",
};

const KIND: Readonly<Record<string, string>> = {
  SUPPLIER_INVOICE: "Mercancía",
  EXPENSE_INVOICE: "Costo",
  CUSTOMS_DECLARATION: "DUA",
};

function Settlement() {
  const { companyId, can, isMine } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const detail = useLoad(
    can("supplier_invoice:read") && id
      ? () => query("/api/v1/companies/{companyId}/procurement/import-settlements/{settlementId}", { path: { companyId, settlementId: id } })
      : null,
    [companyId, id],
  );
  const approve = useCommand(`approve-settlement:${id}`, "/api/v1/companies/{companyId}/procurement/approve-import-settlement");
  const cancel = useCommand(`cancel-settlement:${id}`, "/api/v1/companies/{companyId}/procurement/cancel-import-settlement");
  const reverse = useCommand(`reverse-settlement:${id}`, "/api/v1/companies/{companyId}/procurement/reverse-import-settlement");
  const [reason, setReason] = useState("");
  if (!can("supplier_invoice:read")) {
    return <NoPermission />;
  }
  if (detail.data === null) {
    return <LoadingIndicator error={detail.error} />;
  }
  const s = detail.data.settlement;
  const busy = approve.busy || cancel.busy || reverse.busy;
  const key = { settlementId: s.settlementId, expectedVersion: s.version };
  return (
    <>
      <p className="muted">
        <Link href="/compras/liquidaciones/">Liquidaciones de importación</Link>
      </p>
      <h1>Liquidación {s.settlementNo}</h1>
      <dl className="facts">
        <dt>Estado</dt>
        <dd>
          <StatusBadge status={s.status} label={STATUS[s.status]} />
        </dd>
        <dt>Fecha</dt>
        <dd>{formatDate(s.settlementDate)}</dd>
        <dt>Referencia</dt>
        <dd>{s.reference ?? "—"}</dd>
        <dt>Costo repartido</dt>
        <dd>
          <Money value={s.totalCost} currency testId="settlement-total" />
        </dd>
        <dt>Preparó / aprobó</dt>
        <dd>
          {s.preparedBy ?? "—"} / {s.approvedBy ?? "—"}
        </dd>
      </dl>
      <div className="actions">
        {s.status === "DRAFT" && can("import_settlement:approve") && !isMine(s.preparedBy) ? (
          <ConfirmAction
            label="Aprobar y contabilizar"
            className="primary"
            stepUp
            busy={busy}
            consequence="El costo repartido pasa a la cuenta de cada línea de mercancía (activo o gasto) y sale de «Importaciones por liquidar» y de las cuentas de los gastos del embarque."
            onConfirm={async () => (await approve.run(key, undefined, `Liquidación ${s.settlementNo} contabilizada.`)) && detail.reload()}
          />
        ) : null}
        {s.status === "DRAFT" && can("import_settlement:prepare") ? (
          <ConfirmAction
            label="Cancelar"
            danger
            busy={busy}
            consequence="La liquidación se cancela y sus documentos quedan libres para otra."
            onConfirm={async () => (await cancel.run(key, undefined, `Liquidación ${s.settlementNo} cancelada.`)) && detail.reload()}
          />
        ) : null}
        {s.status === "POSTED" && can("import_settlement:approve") ? (
          <>
            <input aria-label="Motivo de la reversa" placeholder="Motivo" value={reason} onChange={(e) => setReason(e.target.value)} />
            <ConfirmAction
              label="Reversar"
              danger
              stepUp
              busy={busy}
              disabled={reason.trim().length < 3}
              consequence="Se reversa el asiento: el costo vuelve a sus cuentas y los documentos quedan libres para otra liquidación."
              onConfirm={async () =>
                (await reverse.run({ ...key, reason: reason.trim() }, undefined, `Liquidación ${s.settlementNo} reversada.`)) && detail.reload()
              }
            />
          </>
        ) : null}
      </div>
      <ErrorBox error={approve.error ?? cancel.error ?? reverse.error} />
      <h2>Documentos</h2>
      <div className="table-wrap">
        <table data-testid="settlement-documents">
          <thead>
            <tr>
              <th>Papel</th>
              <th>Documento</th>
              <th>Proveedor</th>
              <th>Fecha</th>
              <th className="num">Costo que aporta (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {detail.data.documents.map((d) => (
              <tr key={`${d.kind}:${d.documentId}`}>
                <td>{KIND[d.kind] ?? d.kind}</td>
                <td>{d.number}</td>
                <td>{d.supplierName}</td>
                <td>{formatDate(d.docDate)}</td>
                <td className="num">
                  <Money value={d.cost} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <h2>Reparto por valor</h2>
      <div className="table-wrap">
        <table data-testid="settlement-allocation">
          <thead>
            <tr>
              <th>Factura</th>
              <th>Línea</th>
              <th>Categoría</th>
              <th className="num">Valor (RD$)</th>
              <th className="num">Costo agregado (RD$)</th>
              <th className="num">Costo total (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {detail.data.allocation.map((a) => (
              <tr key={a.siLineId} data-testid={`allocation:${a.description}`}>
                <td>{a.invoiceNumber}</td>
                <td>{a.description}</td>
                <td>{a.category}</td>
                <td className="num">
                  <Money value={a.baseValue} />
                </td>
                <td className="num">
                  <Money value={a.addedCost} />
                </td>
                <td className="num">
                  <Money value={a.totalCost} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <History history={detail.data.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Settlement />
    </Suspense>
  );
}
