"use client";

import { useState } from "react";
import { ApiError, query } from "@/api/client";
import { ErrorBox, Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatDate, formatDateTime } from "@/lib/labels";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { useSession } from "@/lib/session";
import { ledgerLabel } from "@/lib/ux4a-auditoria";

interface ChainResult {
  ledger: string;
  seals: number;
  valid: boolean;
  firstInvalidSequence: number | null;
  gaps: string[];
  digestMismatches: string[];
  stalePending: number;
  sealErrors: number;
}

interface Report {
  valid: boolean;
  chains: ChainResult[];
}

// UI-01 / E-UI01-2: recompute every hash chain from the data and compare it with the seals and with the digests read from WORM
// (hash:verify). Nothing is stored; without WORM storage in the environment the server answers "not available".
// UX4-02 (A-09, E-UX4-15): "Verificar integridad" in plain words, with the last verification and each chain's state
// (GetIntegrityStatus, audit:read).
function Status({ generation }: { generation: number }) {
  const { companyId, can } = useSession();
  const { data, error } = useLoad(
    can("audit:read") ? () => query("/api/v1/companies/{companyId}/audit/integrity-status", { path: { companyId } }) : null,
    [companyId, generation],
  );
  if (!can("audit:read")) {
    return null;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const last = data.lastVerification;
  return (
    <>
      <p data-testid="last-verification">
        {last ? (
          <>
            Última verificación: {formatDateTime(last.verifiedAt)}
            {last.verifiedBy ? ` por ${last.verifiedBy}` : ""} —{" "}
            <StatusBadge status={last.valid ? "MATCHED" : "FAILED"} label={last.valid ? "Todo en orden" : "Con problemas"} />
          </>
        ) : (
          "Todavía no se ha verificado la integridad en este ambiente."
        )}
      </p>
      <div className="table-wrap"><table data-testid="integrity-chains">
        <thead>
          <tr>
            <th>Libro</th>
            <th>Último respaldo diario</th>
            <th className="num">Último registro sellado</th>
            <th className="num">Pendientes de sellar</th>
            <th className="num">Errores al sellar</th>
          </tr>
        </thead>
        <tbody>
          {data.chains.map((c) => (
            <tr key={c.ledger}>
              <td>{ledgerLabel(c.ledger)}</td>
              <td>{c.lastDigestDate ? formatDate(c.lastDigestDate) : "Ninguno"}</td>
              <td className="num">{c.lastSequence}</td>
              <td className="num">{c.pendingSeal}</td>
              <td className="num">{c.sealErrors}</td>
            </tr>
          ))}
        </tbody>
      </table></div>
    </>
  );
}

export default function Page() {
  const { can } = useSession();
  const [generation, setGeneration] = useState(0);
  const verify = useCommand("verify-hash-chain", "/api/v1/companies/{companyId}/audit/verify-hash-chain", (r) =>
    (r.result as unknown as Report | null)?.valid ? "Verificación terminada: la cadena es válida." : "Verificación terminada: revise los problemas encontrados.",
  );
  const [report, setReport] = useState<Report | null>(null);
  if (!can("hash:verify")) {
    return <NoPermission />;
  }
  const unavailable = verify.error instanceof ApiError && verify.error.status === 503;
  return (
    <>
      <h1>Verificar integridad</h1>
      <p className="muted">
        Comprueba que nadie haya cambiado ni borrado asientos, movimientos de inventario o eventos: recalcula la huella de cada registro y la compara con
        los sellos y con los respaldos diarios inalterables. No modifica nada.
      </p>
      <Status generation={generation} />
      <div className="actions">
        <button
          type="button"
          className="primary"
          disabled={verify.busy}
          onClick={async () => {
            setReport(null);
            const response = await verify.run({});
            if (response) {
              setReport(response.result as unknown as Report);
              setGeneration((g) => g + 1);
            }
          }}
        >
          {verify.busy ? "Verificando…" : "Verificar ahora"}
        </button>
      </div>
      {unavailable ? <p className="notice">La verificación no está disponible en este ambiente: no tiene configurado el almacenamiento de respaldos inalterables (WORM).</p> : <ErrorBox error={verify.error} />}
      {report ? (
        <>
          <p data-testid="chain-result">
            Resultado: <StatusBadge status={report.valid ? "MATCHED" : "FAILED"} label={report.valid ? "Todo en orden" : "Con problemas"} />
          </p>
          <div className="table-wrap"><table>
            <thead>
              <tr>
                <th>Libro</th>
                <th className="num">Sellos</th>
                <th>Estado</th>
                <th>Primer sello alterado</th>
                <th>Registros faltantes</th>
                <th>Diferencias con los respaldos</th>
                <th className="num">Pendientes de sellar (atrasados)</th>
                <th className="num">Errores al sellar</th>
              </tr>
            </thead>
            <tbody>
              {report.chains.map((c) => (
                <tr key={c.ledger}>
                  <td>{ledgerLabel(c.ledger)}</td>
                  <td className="num">{c.seals}</td>
                  <td>
                    <StatusBadge status={c.valid ? "MATCHED" : "FAILED"} label={c.valid ? "En orden" : "Alterada"} />
                  </td>
                  <td>{c.firstInvalidSequence ?? "—"}</td>
                  <td>{c.gaps.length === 0 ? "—" : c.gaps.join(", ")}</td>
                  <td className="wrap">{c.digestMismatches.length === 0 ? "—" : c.digestMismatches.join("; ")}</td>
                  <td className="num">{c.stalePending}</td>
                  <td className="num">{c.sealErrors}</td>
                </tr>
              ))}
            </tbody>
          </table></div>
        </>
      ) : null}
    </>
  );
}
