"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { LabPrintedDocument } from "@/components/PrintedDocument";

// LAB1-03 (E-LAB1-03-9/10): the rack labels of a lot, 100 × 150 mm, one per rack (or only `rack`'s) — the field code in large type and the
// QR Dispatch scans at «Confirmar carga».

function RackLabels() {
  const params = useSearchParams();
  const lot = params.get("lote") ?? "";
  const rack = Number.parseInt(params.get("rack") ?? "", 10);
  return (
    <LabPrintedDocument kind="rackLabels" id={lot} rack={Number.isInteger(rack) && rack >= 1 ? rack : undefined} back={<Link href={`/calidad/lotes/?lote=${lot}`}>← Lote</Link>} />
  );
}

export default function Page() {
  return (
    <Suspense>
      <RackLabels />
    </Suspense>
  );
}
