"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { todayInDominicanRepublic } from "@/lib/labels";

// AF1-05 (E-AF1-05-1): Contabilidad › Activos fijos — the pages share these tabs and labels.

export const ASSET_STATUS: Readonly<Record<string, string>> = {
  AWAITING_SERVICE: "En espera de servicio",
  IN_SERVICE: "En servicio",
  DISPOSED: "Dado de baja",
  CANCELLED: "Anulado",
};

export const CLASS_STATUS: Readonly<Record<string, string>> = { DRAFT: "Por aprobar", ACTIVE: "Vigente", SUPERSEDED: "Reemplazada", DISCARDED: "Descartada" };

export const DISPOSAL_STATUS: Readonly<Record<string, string>> = { DRAFT: "Por aprobar", POSTED: "Contabilizada", CANCELLED: "Cancelada" };

export const DISPOSAL_KIND: Readonly<Record<string, string>> = { SCRAP: "Desecho", SALE: "Venta" };

export const LOAD_STATUS: Readonly<Record<string, string>> = { DRAFT: "Por aprobar", POSTED: "Contabilizada", REVERSED: "Reversada", DISCARDED: "Descartada" };

export const RUN_STATUS: Readonly<Record<string, string>> = { POSTED: "Registrada", UNDONE: "Deshecha" };

export const MOVEMENT_KIND: Readonly<Record<string, string>> = {
  ACQUISITION: "Compra",
  COST_ADDED: "Costo de liquidación",
  COST_REMOVED: "Liquidación reversada",
  IN_SERVICE: "Puesta en servicio",
  TRANSFER: "Traslado",
  DEPRECIATION: "Depreciación",
  DEPRECIATION_UNDONE: "Depreciación deshecha",
  DISPOSAL: "Baja",
  OPENING: "Carga inicial",
  CANCELLED: "Anulación",
};

const TABS: readonly { href: string; label: string }[] = [
  { href: "/contabilidad/activos/", label: "Activos" },
  { href: "/contabilidad/activos/clases/", label: "Clases" },
  { href: "/contabilidad/activos/depreciacion/", label: "Depreciación del mes" },
  { href: "/contabilidad/activos/bajas/", label: "Bajas" },
  { href: "/contabilidad/activos/carga/", label: "Carga inicial" },
];

export function FixedAssetTabs() {
  const path = usePathname();
  return (
    <nav className="actions" aria-label="Activos fijos">
      {TABS.map((t) => (
        <Link key={t.href} href={t.href} className={path === t.href ? "button primary" : "button"} aria-current={path === t.href ? "page" : undefined}>
          {t.label}
        </Link>
      ))}
    </nav>
  );
}

/** The first day of the last month that has ended, as YYYY-MM-01. */
export function lastEndedMonth(): string {
  const today = todayInDominicanRepublic();
  const year = Number(today.slice(0, 4));
  const month = Number(today.slice(5, 7));
  return month === 1 ? `${year - 1}-12-01` : `${year}-${String(month - 1).padStart(2, "0")}-01`;
}

/** The last day of the last month that has ended (a load's cut-off). */
export function lastMonthEnd(): string {
  const today = todayInDominicanRepublic();
  const first = new Date(`${today.slice(0, 7)}-01T12:00:00Z`);
  first.setUTCDate(0);
  return first.toISOString().slice(0, 10);
}
