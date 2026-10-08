"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { PrintedDocument } from "@/components/PrintedDocument";

// QUO1-04 (E-QUO1-04-5), UX3-02 (E-UX3-10), PRT-01: the quote the customer receives, drawn by the server with the company's format.

function QuotePrint() {
  const id = useSearchParams().get("id") ?? "";
  return <PrintedDocument documentType="QUOTE" id={id} back={<Link href={`/ventas/cotizacion/?id=${id}`}>← Cotización</Link>} />;
}

export default function Page() {
  return (
    <Suspense>
      <QuotePrint />
    </Suspense>
  );
}
