"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState, type ReactNode } from "react";
import { actAs, loginUrl, logout, query, stopActingAs } from "@/api/client";
import { ErrorBox } from "./ui";
import { useSession } from "@/lib/session";
import { identityLabel, showIdentitySelector, type TestIdentityOption } from "@/lib/identities";

interface NavItem {
  href: string;
  label: string;
  permission: string;
}

interface NavGroup {
  title: string;
  items: readonly NavItem[];
}

// E-UI-1 / E-PR18b-8: a side menu grouped by area; an item shows only with its read permission, a group only with a visible item.
// Auditoría and Seguridad arrive with UI-01 (E-UI-2): journals are reached from the documents' "ver asientos" links.
export const NAV: readonly NavGroup[] = [
  {
    title: "Maestros",
    items: [
      { href: "/maestros/proveedores/", label: "Proveedores", permission: "master_data:read" },
      { href: "/maestros/articulos/", label: "Materias primas", permission: "master_data:read" },
      { href: "/maestros/cuentas-bancarias/", label: "Cuentas bancarias de la empresa", permission: "bank:read" },
    ],
  },
  { title: "Compras", items: [{ href: "/compras/ordenes/", label: "Órdenes de compra", permission: "purchase_order:read" }] },
  {
    title: "Almacén",
    items: [
      { href: "/almacen/recepciones/", label: "Recepciones", permission: "goods_receipt:read" },
      { href: "/almacen/correcciones/", label: "Correcciones", permission: "goods_receipt:read" },
    ],
  },
  {
    title: "Cuentas por pagar",
    items: [
      { href: "/cxp/facturas/", label: "Facturas de proveedor", permission: "supplier_invoice:read" },
      { href: "/cxp/antiguedad/", label: "Antigüedad de CxP", permission: "payment:read" },
    ],
  },
  {
    title: "Tesorería",
    items: [
      { href: "/tesoreria/propuesta/", label: "Propuesta de pago", permission: "payment:read" },
      { href: "/tesoreria/pagos/", label: "Pagos", permission: "payment:read" },
      { href: "/tesoreria/extractos/", label: "Extractos bancarios", permission: "bank:read" },
      { href: "/tesoreria/conciliacion/", label: "Conciliación bancaria", permission: "bank:read" },
    ],
  },
  {
    title: "Contabilidad",
    items: [
      { href: "/contabilidad/mapas/", label: "Mapas de cuentas", permission: "configuration:read" },
      { href: "/contabilidad/reglas/", label: "Reglas contables", permission: "configuration:read" },
      { href: "/contabilidad/politicas/", label: "Políticas", permission: "configuration:read" },
    ],
  },
  {
    title: "Fiscal",
    items: [
      { href: "/fiscal/fuentes/", label: "Fuentes fiscales", permission: "configuration:read" },
      { href: "/fiscal/reglas/", label: "Reglas fiscales", permission: "configuration:read" },
    ],
  },
  {
    title: "Cierre",
    items: [
      { href: "/cierre/periodos/", label: "Períodos y cierre", permission: "period:read" },
      { href: "/cierre/conciliaciones/", label: "Conciliaciones", permission: "reconciliation:read" },
    ],
  },
];

/** Detail pages light up their list's menu item (the static export puts the id in the query string). */
const DETAIL_PARENTS: Readonly<Record<string, string>> = {
  "/compras/orden/": "/compras/ordenes/",
  "/almacen/recepcion/": "/almacen/recepciones/",
  "/almacen/recibir/": "/compras/ordenes/",
  "/cxp/factura/": "/cxp/facturas/",
  "/cierre/conciliacion/": "/cierre/conciliaciones/",
  "/tesoreria/pago/": "/tesoreria/pagos/",
  "/maestros/proveedor/": "/maestros/proveedores/",
};

function isActive(pathname: string, href: string): boolean {
  const parent = Object.entries(DETAIL_PARENTS).find(([detail]) => pathname.startsWith(detail))?.[1];
  return pathname.startsWith(href) || parent === href;
}

function SideMenu({ pathname, can }: { pathname: string; can: (permission: string) => boolean }) {
  return (
    <nav className="sidebar" aria-label="Menú principal">
      <Link href="/" className="brand">
        <span className="brand-mark" aria-hidden="true">
          R
        </span>
        Rochell Core
      </Link>
      <ul>
        <li>
          <Link href="/" className={pathname === "/" ? "active" : undefined}>
            Inicio
          </Link>
        </li>
      </ul>
      {NAV.map((group) => {
        const items = group.items.filter((item) => can(item.permission));
        if (items.length === 0) {
          return null;
        }
        return (
          <div key={group.title}>
            <h2>{group.title}</h2>
            <ul>
              {items.map((item) => (
                <li key={item.href}>
                  <Link href={item.href} className={isActive(pathname, item.href) ? "active" : undefined}>
                    {item.label}
                  </Link>
                </li>
              ))}
            </ul>
          </div>
        );
      })}
    </nav>
  );
}

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

/**
 * E-B03-14: in TEST databases a tester acts as synthetic users (each with its own roles, so segregation of duties holds).
 * Shown while acting, or to whoever holds identity:act_as in the selected company.
 */
function IdentitySelector() {
  const { state, company, companyId, reload } = useSession();
  const [identities, setIdentities] = useState<TestIdentityOption[]>([]);
  const [error, setError] = useState<unknown>(null);
  const authenticatedEmail = state.status === "ready" ? state.session.authenticatedEmail : null;
  const visible = showIdentitySelector(company?.permissions ?? [], authenticatedEmail);

  useEffect(() => {
    if (!visible || !companyId) {
      return;
    }
    query("/api/v1/auth/test-identities", { query: { companyId } })
      .then((list) => setIdentities(list))
      .catch(() => setIdentities([]));
  }, [visible, companyId]);

  if (!visible || state.status !== "ready") {
    return null;
  }
  const current = authenticatedEmail ? state.session.userId : "";
  return (
    <span className="identity" data-testid="identity-selector">
      <select
        aria-label="Actuar como"
        value={current}
        onChange={async (e) => {
          setError(null);
          try {
            if (e.target.value === "") {
              await stopActingAs();
            } else {
              await actAs(companyId, e.target.value);
            }
            reload();
          } catch (caught) {
            setError(caught);
          }
        }}
      >
        <option value="">{authenticatedEmail ? `Volver a mi usuario (${authenticatedEmail})` : "Actuar como…"}</option>
        {identities.map((identity) => (
          <option key={identity.userId} value={identity.userId}>
            {identityLabel(identity)}
          </option>
        ))}
      </select>
      {authenticatedEmail ? <strong className="acting"> Identidad de prueba</strong> : null}
      {error ? <ErrorBox error={error} /> : null}
    </span>
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
    <div className="app">
      <SideMenu pathname={pathname} can={can} />
      <div className="main">
        <header className="topbar">
          <span className="spacer" />
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
          <IdentitySelector />
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
        <main className="page">{company ? children : <p>No tiene roles asignados en ninguna empresa.</p>}</main>
      </div>
    </div>
  );
}
