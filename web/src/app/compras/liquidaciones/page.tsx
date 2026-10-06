"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { query } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ErrorBox, Field, Money, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// USD1-07a (E-USD1-07-4, E-USD1-04-3…6): Compras › Liquidaciones de importación. Cuentas por pagar gathers a shipment's posted documents —
// the foreign invoices of the goods, the expense invoices of freight, insurance, agent and transport, and the DUAs — and the server
// spreads their cost by value over the goods' lines; the Controller approves it on the settlement's page.

export const SETTLEMENT_STATUS: Readonly<Record<string, string>> = {
  DRAFT: "Borrador",
  POSTED: "Contabilizada",
  REVERSED: "Reversada",
  CANCELLED: "Cancelada",
};

type Kind = "SUPPLIER_INVOICE" | "EXPENSE_INVOICE" | "CUSTOMS_DECLARATION";

function NewSettlement() {
  const { companyId, plantName } = useSession();
  const router = useRouter();
  const prepare = useCommand("prepare-import-settlement", "/api/v1/companies/{companyId}/procurement/prepare-import-settlement");
  const candidates = useLoad(async () => {
    const [invoices, duas, plants] = await Promise.all([
      query("/api/v1/companies/{companyId}/procurement/supplier-invoices", {
        path: { companyId },
        query: { accountingStatus: "POSTED", limit: 200 },
      }),
      query("/api/v1/companies/{companyId}/procurement/customs-declarations", {
        path: { companyId },
        query: { status: "POSTED", limit: 200 },
      }),
      query("/api/v1/companies/{companyId}/master-data/plants", {
        path: { companyId },
      }),
    ]);
    return {
      invoices: invoices.items.filter((i) => i.docClass === "EXPENSE"),
      duas: duas.items.filter((d) => d.settlementNo === null),
      plants: plants.items,
    };
  }, [companyId]);
  const [plantId, setPlantId] = useState("");
  const [date, setDate] = useState(todayInDominicanRepublic());
  const [reference, setReference] = useState("");
  const [picked, setPicked] = useState<ReadonlyMap<string, Kind>>(new Map());
  const fe = useFieldErrors<"plantId" | "documents">();
  if (candidates.data === null) {
    return <LoadingIndicator error={candidates.error} />;
  }
  const toggle = (id: string, kind: Kind) =>
    setPicked((m) => {
      const next = new Map(m);
      if (next.get(id) === kind) {
        next.delete(id);
      } else {
        next.set(id, kind);
      }
      return next;
    });
  const box = (id: string, kind: Kind, label: string) => (
    <input type="checkbox" aria-label={label} checked={picked.get(id) === kind} onChange={() => toggle(id, kind)} />
  );
  return (
    <section className="card" data-testid="settlement-form">
      <h2>Nueva liquidación</h2>
      <Field label="Planta" required error={fe.errors.plantId}>
        <select aria-label="Planta" value={plantId} onChange={(e) => setPlantId(e.target.value)}>
          <option value="">—</option>
          {candidates.data.plants.map((p) => (
            <option key={p.plantId} value={p.plantId}>
              {plantName(p.plantId, p.code)}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Fecha de la liquidación">
        <input type="date" aria-label="Fecha de la liquidación" value={date} max={todayInDominicanRepublic()} onChange={(e) => setDate(e.target.value)} />
      </Field>
      <Field label="Referencia del embarque (opcional)">
        <input aria-label="Referencia del embarque" maxLength={80} value={reference} onChange={(e) => setReference(e.target.value)} />
      </Field>
      <h3>Facturas</h3>
      <p className="muted">
        Marque como «mercancía» las facturas del exterior de lo importado (reciben el costo) y como «costo» las de flete, seguro, agente y transporte.
      </p>
      <div className="table-wrap">
        <table data-testid="settlement-invoices">
          <thead>
            <tr>
              <th>Mercancía</th>
              <th>Costo</th>
              <th>Factura</th>
              <th>Proveedor</th>
              <th>Fecha</th>
              <th className="num">Neto (RD$)</th>
            </tr>
          </thead>
          <tbody>
            {candidates.data.invoices.map((i) => (
              <tr key={i.supplierInvoiceId}>
                <td>{i.currency === "USD" ? box(i.supplierInvoiceId, "SUPPLIER_INVOICE", `Mercancía ${i.supplierFiscalNumber}`) : null}</td>
                <td>{box(i.supplierInvoiceId, "EXPENSE_INVOICE", `Costo ${i.supplierFiscalNumber}`)}</td>
                <td>
                  {i.supplierFiscalNumber}
                  {i.currency === "USD" ? <span className="muted"> · US$ {i.totalAmountUsd}</span> : null}
                </td>
                <td>{i.supplierName}</td>
                <td>{formatDate(i.docDate)}</td>
                <td className="num">
                  <Money value={i.totalAmount} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <h3>DUA sin liquidar</h3>
      {candidates.data.duas.length === 0 ? (
        <p className="muted">No hay DUA sin liquidar.</p>
      ) : (
        <div className="table-wrap">
          <table data-testid="settlement-duas">
            <thead>
              <tr>
                <th />
                <th>DUA</th>
                <th>Fecha</th>
                <th className="num">Aranceles + otros (RD$)</th>
              </tr>
            </thead>
            <tbody>
              {candidates.data.duas.map((d) => (
                <tr key={d.duaId}>
                  <td>{box(d.duaId, "CUSTOMS_DECLARATION", `DUA ${d.duaNo}`)}</td>
                  <td>{d.duaNo}</td>
                  <td>{formatDate(d.duaDate)}</td>
                  <td className="num">
                    <Money value={d.dutiesAmount} /> + <Money value={d.otherAmount} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="muted" id="settlement-documents-error">
        {fe.errors.documents}
      </p>
      <div className="actions form-actions">
        <button
          type="button"
          className="primary"
          disabled={prepare.busy}
          onClick={async () => {
            const documents = [...picked.entries()].map(([documentId, kind]) => ({ kind, documentId }));
            if (
              !fe.check({
                plantId: !plantId && "Elija la planta.",
                documents:
                  (!documents.some((d) => d.kind === "SUPPLIER_INVOICE") && "Marque al menos una factura de mercancía del exterior.") ||
                  (!documents.some((d) => d.kind !== "SUPPLIER_INVOICE") && "Marque al menos un documento de costo (factura o DUA)."),
              })
            ) {
              return;
            }
            const response = await prepare.run(
              {
                plantId,
                settlementDate: date,
                reference: reference.trim() || null,
                documents,
              },
              undefined,
              "Liquidación preparada en borrador.",
            );
            if (response) {
              router.push(`/compras/liquidacion/?id=${response.resultRef}`);
            }
          }}
        >
          Preparar y ver el reparto
        </button>
      </div>
      <ErrorBox error={prepare.error} />
    </section>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const [adding, setAdding] = useState(false);
  const list = useLoad(
    can("supplier_invoice:read") ? () => query("/api/v1/companies/{companyId}/procurement/import-settlements", { path: { companyId } }) : null,
    [companyId],
  );
  if (!can("supplier_invoice:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Liquidaciones de importación</h1>
      <p className="muted">Reparten por valor el flete, el seguro, los aranceles del DUA, el agente aduanal y el transporte entre lo importado.</p>
      {can("import_settlement:prepare") ? (
        adding ? (
          <NewSettlement />
        ) : (
          <div className="actions">
            <button type="button" className="primary" onClick={() => setAdding(true)}>
              Nueva liquidación
            </button>
          </div>
        )
      ) : null}
      {list.data === null ? (
        <LoadingIndicator error={list.error} />
      ) : list.data.items.length === 0 ? (
        <p className="muted">Todavía no hay liquidaciones.</p>
      ) : (
        <div className="table-wrap">
          <table data-testid="settlements">
            <thead>
              <tr>
                <th>Liquidación</th>
                <th>Fecha</th>
                <th>Referencia</th>
                <th className="num">Costo repartido (RD$)</th>
                <th>Documentos</th>
                <th>Estado</th>
              </tr>
            </thead>
            <tbody>
              {list.data.items.map((s) => (
                <tr key={s.settlementId}>
                  <td>
                    <Link href={`/compras/liquidacion/?id=${s.settlementId}`}>{s.settlementNo}</Link>
                  </td>
                  <td>{formatDate(s.settlementDate)}</td>
                  <td>{s.reference ?? "—"}</td>
                  <td className="num">
                    <Money value={s.totalCost} />
                  </td>
                  <td>{s.documents}</td>
                  <td>
                    <StatusBadge status={s.status} label={SETTLEMENT_STATUS[s.status]} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
