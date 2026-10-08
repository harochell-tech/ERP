"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { PrintedDocument } from "@/components/PrintedDocument";

// UX4-03 (V-19), PRT-01: the statement of account the customer receives, drawn by the server with the company's format.

function StatementPrint() {
  const params = useSearchParams();
  const customer = params.get("cliente") ?? "";
  const from = params.get("desde") ?? "";
  const to = params.get("hasta") ?? "";
  return (
    <PrintedDocument
      documentType="STATEMENT"
      id={from && to ? customer : ""}
      from={from}
      to={to}
      back={<Link href={`/ventas/estado-de-cuenta/?cliente=${customer}&desde=${from}&hasta=${to}`}>← Estado de cuenta</Link>}
    />
  );
}

export default function Page() {
  return (
    <Suspense>
      <StatementPrint />
    </Suspense>
  );
}
