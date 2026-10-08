"use client";

import { stampQr } from "@/lib/ecf";

// ENT1-03 (E-ENT-1/8): the driver's QR on the delivery note. The server gives the page path with the link's HMAC; the QR is the full
// address on this host, so the driver's phone opens Core's public page.

export function driverLinkUrl(path: string | null | undefined): string | null {
  return path ? `${window.location.origin}${path}` : null;
}

export function QrCode({ url, size = 120, label, testId }: { url: string | null; size?: number; label: string; testId?: string }) {
  const qr = stampQr(url);
  if (!qr) {
    return null;
  }
  const cells: string[] = [];
  for (let row = 0; row < qr.size; row++) {
    for (let col = 0; col < qr.size; col++) {
      if (qr.dark(row, col)) {
        cells.push(`M${col + 4} ${row + 4}h1v1h-1z`);
      }
    }
  }
  const box = qr.size + 8;
  return (
    <svg width={size} height={size} viewBox={`0 0 ${box} ${box}`} role="img" aria-label={label} data-testid={testId} data-url={url ?? undefined} shapeRendering="crispEdges">
      <rect width={box} height={box} fill="#fff" />
      <path d={cells.join("")} fill="#000" />
    </svg>
  );
}

/** E-ENT-8: bottom right of the delivery note, with the line the driver reads. */
export function DriverQrBlock({ path }: { path: string | null | undefined }) {
  const url = typeof window === "undefined" ? null : driverLinkUrl(path);
  if (!url) {
    return null;
  }
  return (
    <div className="driver-qr" data-testid="conduce-driver-qr">
      <QrCode url={url} label="Código QR para que el chofer confirme la entrega" testId="driver-qr" />
      <div>Chofer: escanee para confirmar la entrega</div>
    </div>
  );
}
