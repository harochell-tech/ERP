"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { PrintedDocument } from "@/components/PrintedDocument";

// UX3-02 (E-UX3-7), ENT-1 (E-ENT-8), PRT-01: the printable delivery note (conduce), drawn by the server with the company's format —
// watermark until the gate-out, the driver's QR once out of the gate.

function DeliveryPrint() {
  const id = useSearchParams().get("id") ?? "";
  return <PrintedDocument documentType="DELIVERY_NOTE" id={id} back={<Link href={`/despacho/conduce/?id=${id}`}>← Conduce</Link>} />;
}

export default function Page() {
  return (
    <Suspense>
      <DeliveryPrint />
    </Suspense>
  );
}
