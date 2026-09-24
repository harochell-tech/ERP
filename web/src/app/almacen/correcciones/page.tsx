"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query, type Schemas } from "@/api/client";
import { AccountingStatus, ErrorBox, Loading, NoPermission, ReasonAction } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { statusLabel } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

type Correction = Schemas["ReceiptCorrectionView"];

/** T-05 / E-PR11-4: the Controller approves (and thereby posts) a DRAFT or pending correction; only a pending one can be rejected. */
function Decision({ correction, onDone }: { correction: Correction; onDone: () => void }) {
  const approve = useCommand(`approve-rc:${correction.correctionId}`, "/api/v1/companies/{companyId}/procurement/approve-receipt-correction");
  const reject = useCommand(`reject-rc:${correction.correctionId}`, "/api/v1/companies/{companyId}/procurement/reject-receipt-correction");
  const busy = approve.busy || reject.busy;
  return (
    <>
      <span className="actions">
        <button
          type="button"
          disabled={busy}
          onClick={async () => {
            if (await approve.run({ correctionId: correction.correctionId })) {
              onDone();
            }
          }}
        >
          Aprobar
        </button>
        {correction.documentStatus === "PENDING_APPROVAL" ? (
          <ReasonAction
            label="Rechazar"
            busy={busy}
            onConfirm={async (reason) => {
              if (await reject.run({ correctionId: correction.correctionId, reason })) {
                onDone();
              }
            }}
          />
        ) : null}
      </span>
      <ErrorBox error={approve.error ?? reject.error} />
    </>
  );
}

function Corrections() {
  const { companyId, can, plantFor } = useSession();
  const status = useSearchParams().get("estado") ?? "";
  const plantId = plantFor("goods_receipt:read");
  const { data, error, reload } = useLoad(
    can("goods_receipt:read")
      ? () => query("/api/v1/companies/{companyId}/procurement/receipt-corrections", { path: { companyId }, query: { plantId, documentStatus: status, limit: 200 } })
      : null,
    [companyId, plantId, status],
  );

  if (!can("goods_receipt:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Correcciones de recepción</h1>
      <p>
        <Link href="/almacen/correcciones/">Todas</Link> · <Link href="/almacen/correcciones/?estado=PENDING_APPROVAL">Pendientes de aprobación</Link>
      </p>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">No hay correcciones.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Recepción</th>
              <th className="num">Diferencia</th>
              <th>Motivo</th>
              <th>Evidencia</th>
              <th>Creada por</th>
              <th>Estado</th>
              <th>Contabilidad</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.items.map((c) => (
              <tr key={c.correctionId}>
                <td>
                  <Link href={`/almacen/recepcion/?id=${c.goodsReceiptId}`}>{c.grNo}</Link>
                </td>
                <td className="num">{formatQuantity(c.deltaQty)}</td>
                <td>{c.reason}</td>
                <td>{c.evidenceObjectKey}</td>
                <td>{c.createdBy ?? "—"}</td>
                <td>{statusLabel(c.documentStatus)}</td>
                <td>
                  <AccountingStatus status={c.accountingStatus} eventId={c.postingEventId} />
                </td>
                <td>{(c.documentStatus === "PENDING_APPROVAL" || c.documentStatus === "DRAFT") && can("receipt_correction:approve") ? <Decision correction={c} onDone={reload} /> : null}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <Corrections />
    </Suspense>
  );
}
