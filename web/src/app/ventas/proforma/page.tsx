"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { PrintedDocument } from "@/components/PrintedDocument";

// FIS1-05 (E-FIS1-05-7), UX4-03 (V-20), PRT-01: the order's proforma the customer takes to the DGII to request the CONFOTUR
// exemption — the ITBIS at the rules in force today, a blank area for the supplier's signature and stamp — drawn by the server with
// the company's format; BORRADOR before the order is confirmed, CANCELADO once cancelled.

function Proforma() {
  const id = useSearchParams().get("id") ?? "";
  return <PrintedDocument documentType="ORDER_PROFORMA" id={id} back={<Link href={`/ventas/pedido/?id=${id}`}>← Pedido</Link>} />;
}

export default function Page() {
  return (
    <Suspense>
      <Proforma />
    </Suspense>
  );
}
