"use client";

import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";

// LAB1-03c (E-LAB1-03-8, 15): the page a lab certificate's QR opens — no sign-in. It says whether the certificate is authentic and in
// force, with what it certifies; never the customer nor the site. The API answers as the service identity «Verificación pública».

interface Verification {
  certificateNo: string;
  issuer: string;
  product: string;
  lot: string;
  breakDate: string;
  specimens: number;
  avgKgcm2: string;
  avgMpa: string;
  minKgcm2: string;
  cvPercent: string | null;
  issuedAt: string;
  status: string;
  voidedAt: string | null;
}

function day(iso: string): string {
  const [y, m, d] = iso.slice(0, 10).split("-");
  return `${d}/${m}/${y}`;
}

function when(utc: string): string {
  return new Date(utc).toLocaleString("es-DO", { timeZone: "America/Santo_Domingo", dateStyle: "short", timeStyle: "short" });
}

function Verify() {
  const params = useSearchParams();
  const company = params.get("c") ?? "";
  const code = params.get("k") ?? "";
  const [result, setResult] = useState<Verification | "missing" | "error" | null>(null);
  useEffect(() => {
    let alive = true;
    fetch(`/api/v1/public/lab-certificates/${encodeURIComponent(company)}/${encodeURIComponent(code)}`, { headers: { Accept: "application/json" } })
      .then(async (r) => (r.status === 404 ? "missing" : r.ok ? ((await r.json()) as Verification) : "error"))
      .catch(() => "error" as const)
      .then((v) => alive && setResult(v));
    return () => {
      alive = false;
    };
  }, [company, code]);

  if (result === null) {
    return <p>Verificando el certificado…</p>;
  }
  if (result === "missing") {
    return (
      <p className="error" role="alert" data-testid="certificate-verification-missing">
        No encontramos un certificado con este código. Puede que el QR esté dañado o que el certificado no sea auténtico.
      </p>
    );
  }
  if (result === "error") {
    return (
      <p className="error" role="alert">
        No se pudo verificar ahora. Intente de nuevo en unos minutos.
      </p>
    );
  }
  const valid = result.status === "ISSUED";
  return (
    <section data-testid="certificate-verification">
      <p className={valid ? "notice" : "notice error"} role="status" data-testid="certificate-verification-status">
        {valid ? "Certificado auténtico y vigente." : `Certificado auténtico, pero ANULADO${result.voidedAt ? ` el ${when(result.voidedAt)}` : ""}: no lo use.`}
      </p>
      <dl className="summary">
        <dt>Número</dt>
        <dd className="mono">{result.certificateNo}</dd>
        <dt>Emitido por</dt>
        <dd>{result.issuer}</dd>
        <dt>Producto</dt>
        <dd>{result.product}</dd>
        <dt>Lote</dt>
        <dd className="mono">{result.lot}</dd>
        <dt>Fecha de rotura</dt>
        <dd>{day(result.breakDate)}</dd>
        <dt>Probetas</dt>
        <dd>{result.specimens}</dd>
        <dt>Promedio</dt>
        <dd>
          {result.avgKgcm2} kg/cm² · {result.avgMpa} MPa
        </dd>
        <dt>Mínimo</dt>
        <dd>{result.minKgcm2} kg/cm²</dd>
        <dt>Coeficiente de variación</dt>
        <dd>{result.cvPercent ? `${result.cvPercent} %` : "—"}</dd>
        <dt>Fecha de emisión</dt>
        <dd>{when(result.issuedAt)}</dd>
      </dl>
      <p className="muted">Resistencia a compresión (ASTM C140) calculada sobre área bruta. Resultados válidos solo para las unidades ensayadas.</p>
    </section>
  );
}

export default function Page() {
  return (
    <>
      <h1>Verificación de certificado</h1>
      <Suspense>
        <Verify />
      </Suspense>
    </>
  );
}
