"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { EmptyState, LoadingIndicator } from "@/components/StateNotices";
import { NoPermission } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// LAB1-04a (E-LAB1-19): the breaks each lot still owes — at 1 day (initial release), 3 days (final release) and 28 days
// (verification) after it entered curing — late first, then today's, then the next seven days.

const AGE: Record<number, string> = { 1: "1 día — liberación inicial", 3: "3 días — liberación final", 28: "28 días — verificación" };

export default function Page() {
  const { companyId, can } = useSession();
  const { data, error } = useLoad(
    can("lab:read") ? () => query("/api/v1/companies/{companyId}/manufacturing/lab/pending-tests", { path: { companyId }, query: { daysAhead: 7 } }) : null,
    [companyId],
  );
  if (!can("lab:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Ensayos pendientes</h1>
      <p className="muted">
        Las roturas de probetas que cada lote debe a 1, 3 y 28 días desde que entró a curado. Un ensayo deja de estar pendiente al registrar en
        Calidad › Laboratorio probetas de esa edad. Los productos que no se ensayan (por ejemplo el Block 4") no aparecen.
      </p>
      {data === null ? (
        <LoadingIndicator error={error} />
      ) : data.items.length === 0 ? (
        <EmptyState title="No hay ensayos pendientes para los próximos 7 días." />
      ) : (
        <>
          <p data-testid="pending-tests-summary">
            <strong>{data.overdue}</strong> atrasado(s) · <strong>{data.dueToday}</strong> para hoy
          </p>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Lote</th>
                  <th>Producto</th>
                  <th>Máquina</th>
                  <th>Producido</th>
                  <th>Ensayo</th>
                  <th>Fecha debida</th>
                  <th>Estado</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((t) => (
                  <tr key={`${t.lotId}:${t.age}`} data-testid="pending-test">
                    <td>
                      <Link className="mono" href={`/calidad/lotes/?lote=${t.lotId}`}>
                        {t.lotCode}
                      </Link>
                    </td>
                    <td>{t.itemCode}</td>
                    <td>{t.machineCode ?? "—"}</td>
                    <td>{formatDate(t.producedOn)}</td>
                    <td>{AGE[t.age] ?? `${t.age} días`}</td>
                    <td>{formatDate(t.dueOn)}</td>
                    <td>{t.overdue ? <span className="badge tone-danger">Atrasado</span> : <span className="badge">Pendiente</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </>
  );
}
