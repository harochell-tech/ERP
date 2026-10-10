"use client";

import { useEffect, useRef, type ReactNode } from "react";
import { query } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { NoPermission } from "@/components/ui";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// PRT-01 (E-PRT-1, E-PRT-01-2): every printed document is drawn by the server with the company's format — the same HTML the e-mail
// turns into its PDF. The screen shows it in an isolated block (its styles do not touch the app, the app's do not touch it) and the
// browser prints it; the menu and the buttons are hidden when printing.

export type PrintDocumentType = "DELIVERY_NOTE" | "INVOICE" | "QUOTE" | "PROFORMA" | "ORDER_PROFORMA" | "STATEMENT" | "AR_AGING";

/** A server-drawn document in an isolated block: its CSS and body in a shadow root (styles do not cross either way). */
export function DocumentView({ css, body, testId = "printed-document", formatVersion }: { css: string; body: string; testId?: string; formatVersion?: number }) {
  const host = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const element = host.current;
    if (!element) {
      return;
    }
    const root = element.shadowRoot ?? element.attachShadow({ mode: "open" });
    root.innerHTML = `<style>${css}</style><div class="doc">${body}</div>`;
  }, [css, body]);
  return <div ref={host} data-testid={testId} data-format-version={formatVersion} />;
}

export function PrintedDocument({
  documentType,
  id,
  from,
  to,
  back,
}: {
  documentType: PrintDocumentType;
  id: string;
  from?: string;
  to?: string;
  back: ReactNode;
}) {
  const { companyId, can } = useSession();
  const allowed = can("sales:read") && id !== "";
  const { data, error } = useLoad(
    allowed
      ? () => query("/api/v1/companies/{companyId}/sales/print/{documentType}/{id}", { path: { companyId, documentType, id }, query: { from, to } })
      : null,
    [companyId, documentType, id, from, to],
  );
  if (!can("sales:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  return (
    <div className="printed-document">
      <div className="actions no-print">
        {back}
        <button type="button" className="primary" onClick={() => window.print()}>
          Imprimir
        </button>
      </div>
      <DocumentView css={data.css} body={data.body} formatVersion={data.formatVersion} />
    </div>
  );
}

/** LAB1-03 (E-LAB1-03-1): the lab's documents print through their own queries — the certificate with lab:read, the rack labels with production:read. */
export function LabPrintedDocument({ kind, id, rack, back }: { kind: "certificate" | "rackLabels"; id: string; rack?: number; back: ReactNode }) {
  const { companyId, can } = useSession();
  const permission = kind === "certificate" ? "lab:read" : "production:read";
  const allowed = can(permission) && id !== "";
  const { data, error } = useLoad(
    allowed
      ? () =>
          kind === "certificate"
            ? query("/api/v1/companies/{companyId}/manufacturing/lab/certificates/{certificateId}/print", { path: { companyId, certificateId: id } })
            : query("/api/v1/companies/{companyId}/manufacturing/lots/{lotId}/rack-labels", { path: { companyId, lotId: id }, query: { rack } })
      : null,
    [companyId, kind, id, rack],
  );
  if (!can(permission)) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  return (
    <div className="printed-document">
      <div className="actions no-print">
        {back}
        <button type="button" className="primary" onClick={() => window.print()}>
          Imprimir
        </button>
      </div>
      <DocumentView css={data.css} body={data.body} formatVersion={data.formatVersion} />
    </div>
  );
}
