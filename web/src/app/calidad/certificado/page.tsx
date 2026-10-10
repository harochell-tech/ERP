"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { LabPrintedDocument } from "@/components/PrintedDocument";

// LAB1-03 (baseline §4.6, E-LAB1-03-1…8): the compression certificate as it prints — drawn by the server from the snapshot kept when it
// was issued, with the company's format and the public QR that verifies it; «ANULADO» once void.

function CertificatePrint() {
  const id = useSearchParams().get("id") ?? "";
  return <LabPrintedDocument kind="certificate" id={id} back={<Link href="/calidad/lotes/">← Lotes</Link>} />;
}

export default function Page() {
  return (
    <Suspense>
      <CertificatePrint />
    </Suspense>
  );
}
