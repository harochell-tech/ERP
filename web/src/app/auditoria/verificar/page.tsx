"use client";

import { useState } from "react";
import { ApiError } from "@/api/client";
import { ErrorBox, NoPermission, StatusBadge } from "@/components/ui";
import { useCommand } from "@/lib/useCommand";
import { useSession } from "@/lib/session";

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

const LEDGERS: Readonly<Record<string, string>> = { GL: "Libro mayor", INV: "Inventario", DOMAIN_EVENT: "Eventos del sistema" };

// UI-01 / E-UI01-2: recompute every hash chain from the data and compare it with the seals and with the digests read from WORM
// (hash:verify). Nothing is stored; without WORM storage in the environment the server answers "not available".
export default function Page() {
  const { can } = useSession();
  const verify = useCommand("verify-hash-chain", "/api/v1/companies/{companyId}/audit/verify-hash-chain");
  const [report, setReport] = useState<Report | null>(null);
  if (!can("hash:verify")) {
    return <NoPermission />;
  }
  const unavailable = verify.error instanceof ApiError && verify.error.status === 503;
  return (
    <>
      <h1>Verificar cadena de integridad</h1>
      <p className="muted">
        Recalcula los hashes de los asientos, del inventario y de los eventos, y los compara con los sellos y con los resúmenes diarios guardados en
        almacenamiento WORM (no borrable). No modifica nada.
      </p>
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
            }
          }}
        >
          {verify.busy ? "Verificando…" : "Verificar cadena"}
        </button>
      </div>
      {unavailable ? <p className="notice">La verificación no está disponible en este ambiente: no tiene almacenamiento WORM configurado.</p> : <ErrorBox error={verify.error} />}
      {report ? (
        <>
          <p data-testid="chain-result">
            Resultado: <StatusBadge status={report.valid ? "MATCHED" : "FAILED"} label={report.valid ? "Cadena válida" : "Cadena con problemas"} />
          </p>
          <table>
            <thead>
              <tr>
                <th>Libro</th>
                <th className="num">Sellos</th>
                <th>Estado</th>
                <th>Primer sello inválido</th>
                <th>Huecos</th>
                <th>Diferencias con WORM</th>
                <th className="num">Sin sellar (viejos)</th>
                <th className="num">Errores de sello</th>
              </tr>
            </thead>
            <tbody>
              {report.chains.map((c) => (
                <tr key={c.ledger}>
                  <td>{LEDGERS[c.ledger] ?? c.ledger}</td>
                  <td className="num">{c.seals}</td>
                  <td>
                    <StatusBadge status={c.valid ? "MATCHED" : "FAILED"} label={c.valid ? "Válida" : "Inválida"} />
                  </td>
                  <td>{c.firstInvalidSequence ?? "—"}</td>
                  <td>{c.gaps.length === 0 ? "—" : c.gaps.join(", ")}</td>
                  <td>{c.digestMismatches.length === 0 ? "—" : c.digestMismatches.join("; ")}</td>
                  <td className="num">{c.stalePending}</td>
                  <td className="num">{c.sealErrors}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      ) : null}
    </>
  );
}
