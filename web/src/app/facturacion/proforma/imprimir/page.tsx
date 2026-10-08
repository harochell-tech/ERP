"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { PrintedDocument } from "@/components/PrintedDocument";

// FIS1b-07 (E-FIS1b-01-13), PRT-01: the proforma of a delivery as the customer takes it to the DGII, with room for the supplier's
// signature and stamp, drawn by the server with the company's format.

function ProformaPrint() {
  const id = useSearchParams().get("id") ?? "";
  return <PrintedDocument documentType="PROFORMA" id={id} back={<Link href={`/facturacion/proforma/?id=${id}`}>← Proforma</Link>} />;
}

export default function Page() {
  return (
    <Suspense>
      <ProformaPrint />
    </Suspense>
  );
}
