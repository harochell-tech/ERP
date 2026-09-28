"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { History } from "@/components/History";
import { AccountingStatus, ErrorBox, Loading, Money, NoPermission, ReasonAction, StatusBadge } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// VS3-10a (E-VS3-02b-2/6/9): an opening batch — its lines at standard cost; posted (OPEN-INV) by the Aprobador de políticas, not the
// preparer, with step-up; reversed whole with a reason while none of its lots moved.

function BatchDetail() {
  const { companyId, can } = useSession();
  const id = useSearchParams().get("id") ?? "";
  const { data, error, reload } = useLoad(
    can("configuration:read") && id ? () => query("/api/v1/companies/{companyId}/sales/opening-batches/{batchId}", { path: { companyId, batchId: id } }) : null,
    [companyId, id],
  );
  const post = useCommand(`post-opening:${id}`, "/api/v1/companies/{companyId}/sales/post-opening-inventory");
  const reverse = useCommand(`reverse-opening:${id}`, "/api/v1/companies/{companyId}/sales/reverse-opening-inventory");
  if (!can("configuration:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const h = data.header;
  const target = { batchId: h.batchId, expectedVersion: h.version };
  return (
    <>
      <p>
        <Link href="/contabilidad/apertura/">← Aperturas</Link>
      </p>
      <h1>
        {h.fileName} <StatusBadge status={h.status} />
      </h1>
      <p>
        Fecha de corte {formatDate(h.cutoverDate)} · {h.lines} líneas · valor <Money value={h.total} /> · contabilidad{" "}
        <AccountingStatus status={h.status === "DRAFT" ? "NOT_POSTED" : h.status} eventId={h.postingEventId} />
      </p>
      {h.reversalReason ? <p className="muted">Motivo de la reversa: {h.reversalReason}</p> : null}
      <div className="actions">
        {h.status === "DRAFT" && can("opening_inventory:post") ? (
          <button type="button" disabled={post.busy} onClick={async () => (await post.run(target)) && reload()}>
            Contabilizar apertura
          </button>
        ) : null}
        {h.status === "POSTED" && can("opening_inventory:post") ? (
          <ReasonAction label="Reversar apertura" busy={reverse.busy} onConfirm={async (reason) => (await reverse.run({ ...target, reason })) && reload()} />
        ) : null}
      </div>
      <ErrorBox error={post.error ?? reverse.error} />
      <table>
        <thead>
          <tr>
            <th className="num">#</th>
            <th>Documento</th>
            <th>Planta</th>
            <th>Ubicación</th>
            <th>Producto</th>
            <th className="num">Cantidad</th>
            <th className="num">Costo</th>
            <th className="num">Valor</th>
            <th>Lote</th>
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => (
            <tr key={l.lineNo}>
              <td className="num">{l.lineNo}</td>
              <td>{l.sourceDocumentNumber}</td>
              <td>{l.plantCode}</td>
              <td>{l.locationCode}</td>
              <td>
                {l.itemCode} — {l.itemDescription}
              </td>
              <td className="num">{formatQuantity(l.quantity)}</td>
              <td className="num">
                <Money value={l.unitCost} />
              </td>
              <td className="num">
                <Money value={l.value} />
              </td>
              <td className="mono">{l.lotCode ?? "—"}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <History history={data.history} />
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <BatchDetail />
    </Suspense>
  );
}
