"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { PrintedDocument } from "@/components/PrintedDocument";

// VS4-04 (E-VS4-04-4), ENT-1 (E-ENT-9), PRT-01: the printable invoice, drawn by the server with the company's format — «SIN VALIDEZ
// FISCAL» until the DGII accepts its e-CF, then the stamp with its QR; the driver's QR of its deliveries still in transit.

function InvoicePrint() {
  const id = useSearchParams().get("id") ?? "";
  return <PrintedDocument documentType="INVOICE" id={id} back={<Link href={`/facturacion/factura/?id=${id}`}>← Factura</Link>} />;
}

export default function Page() {
  return (
    <Suspense>
      <InvoicePrint />
    </Suspense>
  );
}
