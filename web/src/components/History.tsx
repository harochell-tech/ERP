"use client";

import type { Schemas } from "@/api/client";
import { formatDateTime, statusLabel } from "@/lib/labels";

/** A document's status changes (ADR-027), as recorded in core.state_history. */
export function History({ history }: { history: Schemas["StateChange"][] }) {
  return (
    <>
      <h2>Historial</h2>
      <table>
        <thead>
          <tr>
            <th>Fecha</th>
            <th>De</th>
            <th>A</th>
            <th>Por</th>
            <th>Motivo</th>
          </tr>
        </thead>
        <tbody>
          {history.map((h, i) => (
            <tr key={i}>
              <td>{formatDateTime(h.at)}</td>
              <td>{statusLabel(h.from)}</td>
              <td>{statusLabel(h.to)}</td>
              <td>{h.by ?? "—"}</td>
              <td>{h.reason ?? ""}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
