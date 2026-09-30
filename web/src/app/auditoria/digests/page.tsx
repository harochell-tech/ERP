"use client";

import { query } from "@/api/client";
import { Loading, NoPermission } from "@/components/ui";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { ledgerLabel, shortHash } from "@/lib/ux4a-auditoria";

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
      <h1>Respaldos diarios inalterables</h1>
      <p className="muted">
        Cada día con actividad, a las 00:15, se guarda la huella de cada libro en un almacenamiento externo que nadie puede borrar ni cambiar (WORM). Si
        alguien alterara un registro después, la verificación de integridad lo detecta al compararlo con este respaldo.
      </p>
      {data === null ? (
        <Loading error={error} />
      ) : data.items.length === 0 ? (
        <p className="muted">Todavía no hay respaldos diarios guardados.</p>
      ) : (
        <div className="table-wrap"><table>
          <thead>
            <tr>
              <th>Día</th>
              <th>Libro</th>
              <th className="num">Registros sellados</th>
              <th className="num">Registros</th>
              <th>Huella</th>
              <th>Archivo del respaldo</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((d) => (
              <tr key={`${d.ledger}:${d.digestDate}`}>
                <td>{formatDate(d.digestDate)}</td>
                <td>{ledgerLabel(d.ledger)}</td>
                <td className="num">
                  {d.firstSeq}–{d.lastSeq}
                </td>
                <td className="num">{d.itemCount}</td>
                <td className="mono" title={d.digestHash}>
                  {shortHash(d.digestHash, 12)}
                </td>
                <td className="mono wrap">{d.wormObjectKey}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}
