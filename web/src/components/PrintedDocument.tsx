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
  const host = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const element = host.current;
    if (!element || !data) {
      return;
    }
    const root = element.shadowRoot ?? element.attachShadow({ mode: "open" });
    root.innerHTML = `<style>${data.css}</style><div class="doc">${data.body}</div>`;
  }, [data]);
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
      <div ref={host} data-testid="printed-document" data-format-version={data.formatVersion} />
    </div>
  );
}
