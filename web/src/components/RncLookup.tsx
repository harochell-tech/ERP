"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { useSession } from "@/lib/session";

type Lookup = Schemas["RncLookup"];

// E-RNC-6: the DGII registry only helps. Leaving the RNC field looks the number up; the legal name is proposed when the field is
// empty, and a number that is missing or not ACTIVO shows a warning. Nothing is blocked: the command keeps what the user confirms.

export function useRncLookup(onName: (legalName: string) => void) {
  const { companyId, can } = useSession();
  const [result, setResult] = useState<Lookup | null>(null);
  async function lookUp(rnc: string) {
    setResult(null);
    const digits = rnc.replace(/\D/g, "");
    if (!can("rnc:read") || (digits.length !== 9 && digits.length !== 11)) {
      return;
    }
    try {
      const found = await query("/api/v1/companies/{companyId}/master-data/rnc/{rnc}", { path: { companyId, rnc: digits } });
      setResult(found);
      if (found.found && found.legalName) {
        onName(found.legalName);
      }
    } catch {
      // The lookup is a convenience: an error leaves the form as it is.
    }
  }
  return { result, lookUp, clear: () => setResult(null) };
}

export function RncHint({ result }: { result: Lookup | null }) {
  if (result === null || result.registryDate === null) {
    return null;
  }
  if (!result.found) {
    return (
      <p className="warning" data-testid="rnc-hint">
        El número no aparece en el padrón de la DGII del {result.registryDate}. Verifíquelo antes de continuar.
      </p>
    );
  }
  const active = result.status === "ACTIVO";
  return (
    <p className={active ? "muted" : "warning"} data-testid="rnc-hint">
      DGII ({result.registryDate}): {result.legalName}
      {result.tradeName ? ` — ${result.tradeName}` : ""} · {result.status}
      {result.regime ? ` · ${result.regime}` : ""}
      {active ? "" : ". El contribuyente no está activo."}
    </p>
  );
}
