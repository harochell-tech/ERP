"use client";

import { useState } from "react";
import { query, type Schemas } from "@/api/client";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, NoPermission, StatusBadge, useFieldErrors } from "@/components/ui";
import { isPositiveDecimal, normalizeInput } from "@/lib/decimal";
import { addDays, formatDate, todayInDominicanRepublic } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// USD1-07a (E-USD1-07-1, E-USD-2, E-USD1-02-1…7): Contabilidad › Tasas de cambio. Tesorería or the Contador enters the Banco Central's
// selling rate of a day with its source; the Controller (someone else) approves it and it is the rate of that day's USD documents. A
// correction is a new rate of the same day; a weekday holiday is entered with the previous business day's rate.

const STATUS_LABELS: Readonly<Record<string, string>> = {
  DRAFT: "Por aprobar",
  ACTIVE: "Vigente",
  SUPERSEDED: "Reemplazada",
  DISCARDED: "Descartada",
};

function RateRow({ rate, onDone }: { rate: Schemas["ExchangeRateView"]; onDone: () => void }) {
  const { can, isMine } = useSession();
  const approve = useCommand(`approve-rate:${rate.rateId}`, "/api/v1/companies/{companyId}/finance/approve-exchange-rate");
  const discard = useCommand(`discard-rate:${rate.rateId}`, "/api/v1/companies/{companyId}/finance/discard-exchange-rate");
  const busy = approve.busy || discard.busy;
  const draft = rate.status === "DRAFT";
  return (
    <tr data-testid={`rate:${rate.rateDate}:${rate.rate}`}>
      <td>{formatDate(rate.rateDate)}</td>
      <td className="num mono">{rate.rate}</td>
      <td className="wrap">{rate.source}</td>
      <td>
        <StatusBadge status={rate.status} label={STATUS_LABELS[rate.status]} />
      </td>
      <td>{rate.preparedBy ?? "—"}</td>
      <td>{rate.approvedBy ?? "—"}</td>
      <td>
        <div className="actions row-buttons">
          {draft && can("exchange_rate:approve") && !isMine(rate.preparedBy) ? (
            <ConfirmAction
              label="Aprobar"
              className="primary"
              busy={busy}
              consequence={`La tasa ${rate.rate} queda vigente para los documentos en dólares del ${formatDate(rate.rateDate)}; si ese día ya tenía una, la reemplaza.`}
              onConfirm={async () =>
                (await approve.run(
                  {
                    rateId: rate.rateId,
                    expectedVersion: rate.version,
                  },
                  undefined,
                  `Tasa ${rate.rate} aprobada.`,
                )) && onDone()
              }
            />
          ) : null}
          {draft && can("exchange_rate:prepare") ? (
            <ConfirmAction
              label="Descartar"
              danger
              busy={busy}
              consequence="La tasa en borrador se descarta y no se usa."
              onConfirm={async () =>
                (await discard.run(
                  {
                    rateId: rate.rateId,
                    expectedVersion: rate.version,
                  },
                  undefined,
                  `Tasa ${rate.rate} descartada.`,
                )) && onDone()
              }
            />
          ) : null}
        </div>
        <ErrorBox error={approve.error ?? discard.error} />
      </td>
    </tr>
  );
}

function PrepareRate({ onDone }: { onDone: () => void }) {
  const prepare = useCommand("prepare-exchange-rate", "/api/v1/companies/{companyId}/finance/prepare-exchange-rate");
  const [rateDate, setRateDate] = useState(todayInDominicanRepublic());
  const [rate, setRate] = useState("");
  const [source, setSource] = useState("Banco Central — tasa de venta");
  const fe = useFieldErrors<"rateDate" | "rate" | "source">();
  return (
    <form
      className="card"
      noValidate
      data-testid="rate-form"
      onSubmit={async (e) => {
        e.preventDefault();
        const value = normalizeInput(rate);
        if (
          !fe.check({
            rateDate: (!rateDate && "Indique la fecha.") || (rateDate > todayInDominicanRepublic() && "La tasa es de hoy o de un día pasado."),
            rate: !isPositiveDecimal(value, 4) && "La tasa es mayor que cero, con hasta 4 decimales (pesos por dólar).",
            source: !source.trim() && "Diga de dónde sale la tasa.",
          })
        ) {
          return;
        }
        if (
          await prepare.run(
            {
              currency: "USD",
              rateDate,
              rate: value,
              source: source.trim(),
            },
            undefined,
            `Tasa ${value} del ${formatDate(rateDate)} registrada: falta la aprobación del Controller.`,
          )
        ) {
          setRate("");
          onDone();
        }
      }}
    >
      <Field label="Fecha" required error={fe.errors.rateDate}>
        <input type="date" aria-label="Fecha de la tasa" value={rateDate} max={todayInDominicanRepublic()} onChange={(e) => setRateDate(e.target.value)} />
      </Field>
      <Field label="Tasa (RD$ por US$)" required error={fe.errors.rate}>
        <input aria-label="Tasa" inputMode="decimal" value={rate} onChange={(e) => setRate(e.target.value)} />
      </Field>
      <Field label="Fuente" required error={fe.errors.source}>
        <input aria-label="Fuente" maxLength={200} value={source} onChange={(e) => setSource(e.target.value)} />
      </Field>
      <p className="muted">En un feriado entre semana registre la tasa del último día hábil con la fuente «Feriado: tasa del …».</p>
      <div className="actions form-actions">
        <button type="submit" className="primary" disabled={prepare.busy}>
          Registrar tasa
        </button>
      </div>
      <ErrorBox error={prepare.error} />
    </form>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const [to, setTo] = useState(todayInDominicanRepublic());
  const [from, setFrom] = useState(addDays(todayInDominicanRepublic(), -30));
  const rates = useLoad(
    can("exchange_rate:read") ? () => query("/api/v1/companies/{companyId}/finance/exchange-rates", { path: { companyId }, query: { from, to } }) : null,
    [companyId, from, to],
  );
  if (!can("exchange_rate:read")) {
    return <NoPermission />;
  }
  return (
    <>
      <h1>Tasas de cambio</h1>
      <p className="muted">
        La tasa de venta del Banco Central de cada día (RD$ por US$). Sin la tasa aprobada del día no se registra un documento en dólares; sábados y domingos
        usan la última aprobada.
      </p>
      {can("exchange_rate:prepare") ? <PrepareRate onDone={rates.reload} /> : null}
      <div className="inline-form" role="search">
        <Field label="Desde">
          <input type="date" aria-label="Desde" value={from} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label="Hasta">
          <input type="date" aria-label="Hasta" value={to} onChange={(e) => setTo(e.target.value)} />
        </Field>
      </div>
      {rates.data === null ? (
        <LoadingIndicator error={rates.error} />
      ) : rates.data.items.length === 0 ? (
        <p className="muted">No hay tasas en esas fechas.</p>
      ) : (
        <div className="table-wrap">
          <table data-testid="rates">
            <thead>
              <tr>
                <th>Fecha</th>
                <th className="num">Tasa</th>
                <th>Fuente</th>
                <th>Estado</th>
                <th>Registró</th>
                <th>Aprobó</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {rates.data.items.map((r) => (
                <RateRow key={`${r.rateId}:${r.version}`} rate={r} onDone={rates.reload} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
