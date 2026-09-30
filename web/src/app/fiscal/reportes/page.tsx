"use client";

import Link from "next/link";
import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { Field, Loading, Money, NoPermission } from "@/components/ui";
import {
  codeLabel,
  FISCAL_REPORT_TABS,
  GOODS_TYPES,
  ID_TYPES,
  isValidPeriod,
  PAYMENT_METHODS,
  previousPeriod,
  warningLabel,
  WITHHOLDING_TAXES,
  type FiscalReportTab,
} from "@/lib/fiscalReports";
import { formatDate } from "@/lib/labels";
import { csvUrl } from "@/lib/ledger";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

// FIS2-03 (E-FIS2-03-1…4): the 606 of a month (to paste into the DGII tool) and the informative IT-1 / IR-17 summaries. Every
// amount, count and total is the server's (fiscal_report:read); the browser only formats them.

type Record606 = Schemas["Report606Record"];

const INFORMATIVE = "Informativo: no es el formulario oficial de la DGII.";

function Report606Rows({ records }: { records: readonly Record606[] }) {
  return (
    <div className="scroll-x">
      <table data-testid="report-606-table">
        <thead>
          <tr>
            <th colSpan={3}>Proveedor</th>
            <th colSpan={5}>Comprobante</th>
            <th colSpan={3} className="num">
              Montos facturados
            </th>
            <th colSpan={6} className="num">
              ITBIS
            </th>
            <th colSpan={3} className="num">
              ISR
            </th>
            <th colSpan={4} className="num">
              Otros
            </th>
            <th />
          </tr>
          <tr>
            <th>Nombre</th>
            <th>1. RNC / cédula</th>
            <th>2. Tipo id.</th>
            <th>3. Tipo de bienes y servicios</th>
            <th>4. NCF</th>
            <th>5. NCF modificado</th>
            <th>6. Fecha comprobante</th>
            <th>7. Fecha de pago</th>
            <th className="num">8. Servicios</th>
            <th className="num">9. Bienes</th>
            <th className="num">10. Total facturado</th>
            <th className="num">11. Facturado</th>
            <th className="num">12. Retenido</th>
            <th className="num">13. Sujeto a proporcionalidad</th>
            <th className="num">14. Llevado al costo</th>
            <th className="num">15. Por adelantar</th>
            <th className="num">16. Percibido en compras</th>
            <th>17. Tipo de retención</th>
            <th className="num">18. Retención de renta</th>
            <th className="num">19. Percibido en compras</th>
            <th className="num">20. Selectivo al consumo</th>
            <th className="num">21. Otros impuestos / tasas</th>
            <th className="num">22. Propina legal</th>
            <th>23. Forma de pago</th>
            <th>Advertencias</th>
          </tr>
        </thead>
        <tbody>
          {records.map((r) => (
            <tr key={`${r.recordKind}:${r.supplierInvoiceId}`} data-testid="report-606-row">
              <td>
                {r.supplierName}
                {r.recordKind === "PAYMENT" ? (
                  <div>
                    <span className="badge tone-progress">Pago de retención</span>
                  </div>
                ) : null}
              </td>
              <td className="mono">{r.rnc ?? "—"}</td>
              <td>{codeLabel(ID_TYPES, r.idType)}</td>
              <td>{codeLabel(GOODS_TYPES, r.goodsType)}</td>
              <td className="mono">
                <Link href={`/cxp/factura/?id=${r.supplierInvoiceId}`}>{r.ncf}</Link>
              </td>
              <td className="mono">{r.ncfModified ?? "—"}</td>
              <td>{formatDate(r.ncfDate)}</td>
              <td>{formatDate(r.paymentDate)}</td>
              <td className="num">
                <Money value={r.servicesAmount} />
              </td>
              <td className="num">
                <Money value={r.goodsAmount} />
              </td>
              <td className="num">
                <Money value={r.totalAmount} />
              </td>
              <td className="num">
                <Money value={r.itbisBilled} />
              </td>
              <td className="num">
                <Money value={r.itbisWithheld} />
              </td>
              <td className="num">
                <Money value={r.itbisProportional} />
              </td>
              <td className="num">
                <Money value={r.itbisToCost} />
              </td>
              <td className="num">
                <Money value={r.itbisToAdvance} />
              </td>
              <td className="num">
                <Money value={r.itbisPerceived} />
              </td>
              <td>{r.isrWithholdingType ?? "—"}</td>
              <td className="num">
                <Money value={r.isrWithheld} />
              </td>
              <td className="num">
                <Money value={r.isrPerceived} />
              </td>
              <td className="num">
                <Money value={r.selectiveTax} />
              </td>
              <td className="num">
                <Money value={r.otherTaxes} />
              </td>
              <td className="num">
                <Money value={r.legalTip} />
              </td>
              <td>{codeLabel(PAYMENT_METHODS, r.paymentMethod)}</td>
              <td>
                {r.warnings.length === 0 ? (
                  "—"
                ) : (
                  <div className="warnings">
                    {r.warnings.map((w) => (
                      <div key={w} className="warning" data-testid="report-606-warning">
                        {warningLabel(w)}
                      </div>
                    ))}
                  </div>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Report606View({ companyId, period }: { companyId: string; period: string }) {
  const { data, error } = useLoad(() => query("/api/v1/companies/{companyId}/tax/reports/606", { path: { companyId }, query: { period } }), [companyId, period]);
  return (
    <>
      <p className="notice">
        Solo incluye las compras registradas en el sistema; agregue los demás gastos (servicios, gastos menores, compras informales) en la herramienta
        606 de la DGII.
      </p>
      {data === null ? (
        <Loading error={error} />
      ) : (
        <>
          <div className="cards">
            <div className="stat">
              <span>RNC de la empresa</span>
              <span className="value" data-testid="report-606-rnc">
                {data.companyRnc || "—"}
              </span>
            </div>
            <div className="stat">
              <span>Período</span>
              <span className="value" data-testid="report-606-period">
                {data.period}
              </span>
            </div>
            <div className="stat">
              <span>Cantidad de registros</span>
              <span className="value" data-testid="report-606-count">
                {data.recordCount}
              </span>
            </div>
            <div className="stat">
              <span>Total facturado</span>
              <span className="value">
                <Money value={data.totalAmount} testId="report-606-total" />
              </span>
            </div>
          </div>
          <div className="card">
            <a className="button" href={csvUrl("/api/v1/companies/{companyId}/tax/reports/606", { path: { companyId }, query: { period } })}>
              Descargar CSV para la herramienta DGII
            </a>
            <ol>
              <li>
                Abra la Herramienta de Envío del Formato 606 de la DGII y escriba el encabezado: RNC, período (AAAAMM) y cantidad de registros.
              </li>
              <li>Pegue las filas del CSV y pulse Validar.</li>
              <li>Genere el archivo TXT y remítalo por la Oficina Virtual (a más tardar el día 15).</li>
            </ol>
          </div>
          {data.records.length === 0 ? <p className="muted">No hay compras registradas en el período.</p> : <Report606Rows records={data.records} />}
        </>
      )}
    </>
  );
}

function It1View({ companyId, period }: { companyId: string; period: string }) {
  const { data, error } = useLoad(
    () => query("/api/v1/companies/{companyId}/tax/reports/it1-summary", { path: { companyId }, query: { period } }),
    [companyId, period],
  );
  return (
    <>
      <p className="notice">{INFORMATIVE}</p>
      {data === null ? (
        <Loading error={error} />
      ) : (
        <>
          <h2>Ventas del período {data.period}</h2>
          {data.sales.length === 0 ? (
            <p className="muted">No hay facturas emitidas en el período.</p>
          ) : (
            <table data-testid="it1-sales">
              <thead>
                <tr>
                  <th>Tipo de e-CF</th>
                  <th className="num">Facturas</th>
                  <th className="num">Neto gravado</th>
                  <th className="num">Neto exento</th>
                  <th className="num">ITBIS</th>
                </tr>
              </thead>
              <tbody>
                {data.sales.map((s) => (
                  <tr key={s.ecfType}>
                    <td>{s.ecfType}</td>
                    <td className="num">{s.invoices}</td>
                    <td className="num">
                      <Money value={s.taxedNet} />
                    </td>
                    <td className="num">
                      <Money value={s.exemptNet} />
                    </td>
                    <td className="num">
                      <Money value={s.itbis} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <h2>Notas de crédito</h2>
          <table>
            <tbody>
              <tr>
                <th>Notas de crédito emitidas</th>
                <td className="num" data-testid="it1-credit-notes">
                  {data.creditNotes}
                </td>
              </tr>
              <tr>
                <th>Neto</th>
                <td className="num">
                  <Money value={data.creditNotesNet} />
                </td>
              </tr>
              <tr>
                <th>ITBIS</th>
                <td className="num">
                  <Money value={data.creditNotesItbis} />
                </td>
              </tr>
            </tbody>
          </table>
          <h2>ITBIS en compras (del 606)</h2>
          <table>
            <tbody>
              <tr>
                <th>ITBIS facturado</th>
                <td className="num">
                  <Money value={data.purchaseItbisBilled} testId="it1-purchase-itbis" />
                </td>
              </tr>
              <tr>
                <th>ITBIS llevado al costo</th>
                <td className="num">
                  <Money value={data.purchaseItbisToCost} />
                </td>
              </tr>
              <tr>
                <th>ITBIS por adelantar</th>
                <td className="num">
                  <Money value={data.purchaseItbisToAdvance} />
                </td>
              </tr>
            </tbody>
          </table>
          <h2>Retenciones que nos hicieron los clientes</h2>
          {data.customerWithholdings.length === 0 ? (
            <p className="muted">No hay retenciones de clientes en el período.</p>
          ) : (
            <table>
              <thead>
                <tr>
                  <th>Impuesto</th>
                  <th className="num">Certificados</th>
                  <th className="num">Monto</th>
                </tr>
              </thead>
              <tbody>
                {data.customerWithholdings.map((w) => (
                  <tr key={w.kind}>
                    <td>{WITHHOLDING_TAXES[w.kind] ?? w.kind}</td>
                    <td className="num">{w.count}</td>
                    <td className="num">
                      <Money value={w.amount} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      )}
    </>
  );
}

function Ir17View({ companyId, period }: { companyId: string; period: string }) {
  const { data, error } = useLoad(
    () => query("/api/v1/companies/{companyId}/tax/reports/ir17-summary", { path: { companyId }, query: { period } }),
    [companyId, period],
  );
  return (
    <>
      <p className="notice">{INFORMATIVE}</p>
      {data === null ? (
        <Loading error={error} />
      ) : (
        <>
          <h2>Retenciones a proveedores pagadas en {data.period}</h2>
          {data.lines.length === 0 ? (
            <p className="muted">No hay retenciones a proveedores en el período.</p>
          ) : (
            <table data-testid="ir17-lines">
              <thead>
                <tr>
                  <th>Impuesto</th>
                  <th>Tipo de retención (ISR)</th>
                  <th className="num">Registros</th>
                  <th className="num">Base</th>
                  <th className="num">Retenido</th>
                </tr>
              </thead>
              <tbody>
                {data.lines.map((l) => (
                  <tr key={`${l.tax}:${l.isrWithholdingType ?? ""}`}>
                    <td>{WITHHOLDING_TAXES[l.tax] ?? l.tax}</td>
                    <td>{l.tax === "ISR" ? (l.isrWithholdingType ?? "Sin tipo") : "—"}</td>
                    <td className="num">{l.records}</td>
                    <td className="num">
                      <Money value={l.base} />
                    </td>
                    <td className="num">
                      <Money value={l.amount} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <table>
            <tbody>
              <tr>
                <th>Total ITBIS retenido</th>
                <td className="num">
                  <Money value={data.itbisWithheld} testId="ir17-itbis" />
                </td>
              </tr>
              <tr>
                <th>Total ISR retenido</th>
                <td className="num">
                  <Money value={data.isrWithheld} testId="ir17-isr" />
                </td>
              </tr>
            </tbody>
          </table>
        </>
      )}
    </>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const [tab, setTab] = useState<FiscalReportTab>("606");
  const [typed, setTyped] = useState(() => previousPeriod());
  const period = typed.trim();
  const valid = isValidPeriod(period);

  if (!can("fiscal_report:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Reportes fiscales</h1>
      <div className="inline-form">
        <Field label="Período (AAAAMM)">
          <input value={typed} onChange={(e) => setTyped(e.target.value)} inputMode="numeric" maxLength={6} placeholder="202609" />
        </Field>
      </div>
      {valid ? null : <p className="error">Escriba el período como AAAAMM, por ejemplo 202609.</p>}
      <div className="tabs" role="tablist">
        {FISCAL_REPORT_TABS.map((t) => (
          <button key={t.id} type="button" role="tab" aria-selected={t.id === tab} onClick={() => setTab(t.id)}>
            {t.label}
          </button>
        ))}
      </div>
      {!valid ? null : tab === "606" ? (
        <Report606View companyId={companyId} period={period} />
      ) : tab === "IT1" ? (
        <It1View companyId={companyId} period={period} />
      ) : (
        <Ir17View companyId={companyId} period={period} />
      )}
    </>
  );
}
