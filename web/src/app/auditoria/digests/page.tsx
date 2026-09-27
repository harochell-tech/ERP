"use client";

import { query } from "@/api/client";
import { Loading, NoPermission } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// UI-01 / E-UI01-3: the daily digests written once to WORM storage (audit:read) — proof that the chain is anchored outside the
// database, day by day.
export default function Page() {
  const { companyId, can } = useSession();
  const { data, error } = useLoad(
    can("audit:read") ? () => query("/api/v1/companies/{companyId}/audit/digests", { path: { companyId }, query: { limit: 200 } }) : null,
    [companyId],
  );
  if (!can("audit:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Resúmenes diarios en WORM</h1>
      <p className="muted">Cada día con actividad, a las 00:15 se escribe un resumen firmado de cada libro en almacenamiento que nadie puede borrar ni cambiar.</p>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">Todavía no hay resúmenes escritos.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Día</th>
              <th>Libro</th>
              <th className="num">Sellos</th>
              <th className="num">Registros</th>
              <th>Hash del resumen</th>
              <th>Objeto en WORM</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((d) => (
              <tr key={`${d.ledger}:${d.digestDate}`}>
                <td>{formatDate(d.digestDate)}</td>
                <td>{d.ledger}</td>
                <td className="num">
                  {d.firstSeq}–{d.lastSeq}
                </td>
                <td className="num">{d.itemCount}</td>
                <td className="mono" title={d.digestHash}>
                  {d.digestHash.slice(0, 16)}…
                </td>
                <td className="mono">{d.wormObjectKey}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
