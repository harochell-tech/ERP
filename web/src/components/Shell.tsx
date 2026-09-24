"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState, type ReactNode } from "react";
import { loginUrl, logout, query } from "@/api/client";
import { ErrorBox } from "./ui";
import { useSession } from "@/lib/session";

interface NavItem {
  href: string;
  label: string;
  permission: string;
}

// E-PR18b-8: navigation follows the permissions the session grants in the selected company.
const NAV: readonly NavItem[] = [
  { href: "/compras/ordenes/", label: "Órdenes de compra", permission: "purchase_order:read" },
  { href: "/almacen/recepciones/", label: "Recepciones", permission: "goods_receipt:read" },
  { href: "/almacen/correcciones/", label: "Correcciones", permission: "goods_receipt:read" },
  { href: "/cxp/facturas/", label: "Facturas de proveedor", permission: "supplier_invoice:read" },
  { href: "/cierre/conciliaciones/", label: "Conciliaciones", permission: "reconciliation:read" },
  { href: "/cierre/periodos/", label: "Períodos y cierre", permission: "period:read" },
];

function PlantSelector() {
  const { companyId, company, plantId, selectPlant, plantScoped } = useSession();
  const [plants, setPlants] = useState<{ plantId: string; code: string }[]>([]);
  const assigned = [...new Set((company?.assignments ?? []).flatMap((a) => (a.plantId ? [a.plantId] : [])))];
  const firstPlant = assigned[0];

  useEffect(() => {
    if (!plantScoped || !companyId || !firstPlant) {
      return;
    }
    query("/api/v1/companies/{companyId}/master-data/plants", { path: { companyId }, query: { plantId: firstPlant } })
      .then((list) => setPlants(list.items))
      .catch(() => setPlants([]));
  }, [companyId, plantScoped, firstPlant]);

  if (!plantScoped) {
    return null;
  }
  return (
    <label>
      Planta:{" "}
      <select aria-label="Planta" value={plantId ?? firstPlant ?? ""} onChange={(e) => selectPlant(e.target.value)}>
        {assigned.map((id) => (
          <option key={id} value={id}>
            {plants.find((p) => p.plantId === id)?.code ?? id.slice(0, 8)}
          </option>
        ))}
      </select>
    </label>
  );
}

export function Shell({ children }: { children: ReactNode }) {
  const { state, company, selectCompany, can, reload } = useSession();
  const pathname = usePathname();

  if (state.status === "loading") {
    return <main className="page">Cargando…</main>;
  }
  if (state.status === "error") {
    return (
      <main className="page">
        <ErrorBox error={state.error} />
      </main>
    );
  }
  if (state.status === "anonymous") {
    return (
      <main className="page">
        <h1>Rochell Core</h1>
        <p>Inicie sesión con su cuenta de la organización.</p>
        <a className="button" href={loginUrl(pathname)}>
          Iniciar sesión
        </a>
      </main>
    );
  }

  const session = state.session;
  return (
    <>
      <header className="topbar">
        <Link href="/" className="brand">
          Rochell Core
        </Link>
        {session.companies.length > 1 ? (
          <select aria-label="Empresa" value={company?.companyId} onChange={(e) => selectCompany(e.target.value)}>
            {session.companies.map((c) => (
              <option key={c.companyId} value={c.companyId}>
                {c.legalName}
              </option>
            ))}
          </select>
        ) : (
          <span>{company?.legalName ?? "Sin empresa asignada"}</span>
        )}
        <PlantSelector />
        <span className="muted" data-testid="user-email">
          {session.email}
        </span>
        <button
          type="button"
          onClick={async () => {
            await logout();
            reload();
          }}
        >
          Cerrar sesión
        </button>
      </header>
      <nav className="nav">
        {NAV.filter((item) => can(item.permission)).map((item) => (
          <Link key={item.href} href={item.href} className={pathname.startsWith(item.href) ? "active" : undefined}>
            {item.label}
          </Link>
        ))}
      </nav>
      <main className="page">{company ? children : <p>No tiene roles asignados en ninguna empresa.</p>}</main>
    </>
  );
}
