"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useCallback, useEffect, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { actAs, loginUrl, logout, query, stopActingAs } from "@/api/client";
import { ErrorBox } from "./ui";
import { useSession } from "@/lib/session";
import { identityLabel, showIdentitySelector, type TestIdentityOption } from "@/lib/identities";
import { environmentBadge, type EnvironmentBadge } from "@/lib/environment";
import { ROLES } from "@/lib/labels";

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
// Journals are reached from the documents' "ver asientos" links; Auditoría and Seguridad came with UI-01.
export const NAV: readonly NavGroup[] = [
  {
    title: "Maestros",
    items: [
      { href: "/maestros/proveedores/", label: "Proveedores", permission: "master_data:read" },
      { href: "/maestros/articulos/", label: "Materias primas", permission: "master_data:read" },
      { href: "/maestros/cuentas-bancarias/", label: "Cuentas bancarias de la empresa", permission: "bank:read" },
      { href: "/maestros/productos-terminados/", label: "Productos terminados", permission: "master_data:read" },
      { href: "/maestros/costos-estandar/", label: "Costos estándar", permission: "sales:read" },
      { href: "/maestros/precios/", label: "Lista de precios", permission: "sales:read" },
      { href: "/maestros/flota/", label: "Vehículos y choferes", permission: "sales:read" },
      { href: "/maestros/padron-rnc/", label: "Padrón RNC (DGII)", permission: "rnc:read" },
    ],
  },
  {
    title: "Ventas",
    items: [
      { href: "/ventas/cotizaciones/", label: "Cotizaciones", permission: "sales:read" }, // QUO1-04 (E-QUO1-04-1)
      { href: "/ventas/pedidos/", label: "Pedidos", permission: "sales:read" },
      { href: "/ventas/clientes/", label: "Clientes", permission: "sales:read" },
      { href: "/ventas/antiguedad/", label: "Antigüedad de CxC", permission: "sales:read" },
      { href: "/ventas/estado-de-cuenta/", label: "Estado de cuenta", permission: "sales:read" },
    ],
  },
  { title: "Despacho", items: [{ href: "/despacho/tablero/", label: "Tablero de despacho", permission: "sales:read" }] },
  {
    title: "Facturación",
    items: [
      { href: "/facturacion/por-facturar/", label: "Por facturar", permission: "sales:read" },
      { href: "/facturacion/facturas/", label: "Facturas", permission: "sales:read" },
      { href: "/facturacion/notas/", label: "Notas de crédito", permission: "sales:read" },
    ],
  },
  {
    title: "Cobros",
    items: [
      { href: "/cobros/recibos/", label: "Recibos", permission: "sales:read" },
      { href: "/cobros/depositos/", label: "Depósitos", permission: "sales:read" },
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
    // MFG1-07 (E-MFG1-07-1)
    title: "Producción",
    items: [
      { href: "/produccion/dia/", label: "Producción del día", permission: "production:read" },
      { href: "/produccion/lotes/", label: "Curado y liberación", permission: "production:read" },
      { href: "/produccion/recetas/", label: "Recetas", permission: "production:read" },
      { href: "/produccion/maquinas/", label: "Máquinas y turnos", permission: "production:read" },
      { href: "/produccion/costos/", label: "Costos de producción", permission: "production:read" },
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
      { href: "/contabilidad/ajustes/", label: "Diario de ajustes", permission: "ledger:read" },
      { href: "/contabilidad/balanza/", label: "Balanza", permission: "ledger:read" },
      { href: "/contabilidad/mayor/", label: "Mayor", permission: "ledger:read" },
      { href: "/contabilidad/estados/", label: "Estados financieros", permission: "ledger:read" },
      { href: "/contabilidad/apertura/", label: "Apertura de inventario", permission: "configuration:read" },
    ],
  },
  {
    title: "Fiscal",
    items: [
      { href: "/fiscal/autorizaciones/", label: "Autorizaciones fiscales", permission: "sales:read" }, // FIS1-05 (E-FIS1-05-1)
      { href: "/fiscal/reportes/", label: "Reportes fiscales", permission: "fiscal_report:read" }, // FIS2-03 (E-FIS2-03-1)
    ],
  },
  {
    title: "Cierre",
    items: [
      { href: "/cierre/periodos/", label: "Períodos y cierre", permission: "period:read" },
      { href: "/cierre/conciliaciones/", label: "Conciliaciones", permission: "reconciliation:read" },
    ],
  },
  {
    title: "Auditoría",
    items: [
      { href: "/auditoria/verificar/", label: "Verificar cadena", permission: "hash:verify" },
      { href: "/auditoria/digests/", label: "Resúmenes en WORM", permission: "audit:read" },
    ],
  },
  {
    title: "Seguridad",
    items: [
      { href: "/seguridad/usuarios/", label: "Usuarios y roles", permission: "iam:read" },
      { href: "/seguridad/solicitudes/", label: "Solicitudes de rol", permission: "iam:read" },
    ],
  },
  {
    // UX2-02 (E-UX2-9): the company's setup in one place; the screens keep their routes.
    title: "Configuración",
    items: [
      { href: "/configuracion/", label: "Centro de configuración", permission: "configuration:read" },
      { href: "/configuracion/empresa/", label: "Empresa", permission: "configuration:read" },
      { href: "/maestros/plantas/", label: "Plantas y ubicaciones", permission: "master_data:read" },
      { href: "/contabilidad/cuentas/", label: "Catálogo de cuentas", permission: "configuration:read" },
      { href: "/contabilidad/estructuras/", label: "Estructuras de reporte", permission: "configuration:read" },
      { href: "/contabilidad/mapas/", label: "Mapas de cuentas", permission: "configuration:read" },
      { href: "/contabilidad/reglas/", label: "Reglas contables", permission: "configuration:read" },
      { href: "/contabilidad/politicas/", label: "Políticas", permission: "configuration:read" },
      { href: "/fiscal/fuentes/", label: "Fuentes fiscales", permission: "configuration:read" },
      { href: "/fiscal/reglas/", label: "Reglas fiscales", permission: "configuration:read" },
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
  "/ventas/cliente/": "/ventas/clientes/",
  "/ventas/pedido/": "/ventas/pedidos/",
  "/ventas/proforma/": "/ventas/pedidos/",
  "/despacho/conduce/": "/despacho/tablero/",
  "/despacho/planificar/": "/despacho/tablero/",
  "/contabilidad/apertura-lote/": "/contabilidad/apertura/",
  "/facturacion/factura/": "/facturacion/facturas/",
  "/facturacion/nota/": "/facturacion/notas/",
  "/cobros/recibo/": "/cobros/recibos/",
  "/cobros/deposito/": "/cobros/depositos/",
  "/produccion/corrida/": "/produccion/dia/",
  "/produccion/receta/": "/produccion/recetas/",
  "/fiscal/autorizacion/": "/fiscal/autorizaciones/",
  "/ventas/cotizacion/": "/ventas/cotizaciones/",
  "/contabilidad/estructura/": "/contabilidad/estructuras/",
};

function matches(pathname: string, href: string): boolean {
  const parent = Object.entries(DETAIL_PARENTS).find(([detail]) => pathname.startsWith(detail))?.[1];
  return pathname.startsWith(href) || parent === href;
}

/** The item lit for a path: the longest matching href, so "/configuracion/empresa/" does not also light "/configuracion/". */
function isActive(pathname: string, href: string): boolean {
  if (!matches(pathname, href)) {
    return false;
  }
  return !NAV.some((g) => g.items.some((i) => i.href.length > href.length && i.href.startsWith(href) && matches(pathname, i.href)));
}


/** The label of the current screen for the mobile top bar: its menu item (or its list's, for a detail page). */
export function screenTitle(pathname: string): string {
  if (pathname === "/") {
    return "Inicio";
  }
  for (const group of NAV) {
    for (const item of group.items) {
      if (isActive(pathname, item.href)) {
        return item.label;
      }
    }
  }
  return "Rochell Core";
}

const COLLAPSED_KEY = "rochell.menu.collapsed";

function readCollapsed(): string[] {
  try {
    const raw = window.localStorage.getItem(COLLAPSED_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    return Array.isArray(parsed) ? parsed.filter((v): v is string => typeof v === "string") : [];
  } catch {
    return [];
  }
}

function writeCollapsed(groups: string[]): void {
  try {
    window.localStorage.setItem(COLLAPSED_KEY, JSON.stringify(groups));
  } catch {
    // Private windows: the folded groups simply are not remembered.
  }
}

const MOBILE_QUERY = "(max-width: 900px)";

/** E-UX1-01-1: below 900 px the shell switches to the top bar and the menu panel. */
function useIsMobile(): boolean {
  return useSyncExternalStore(
    (notify) => {
      const media = window.matchMedia(MOBILE_QUERY);
      media.addEventListener("change", notify);
      return () => media.removeEventListener("change", notify);
    },
    () => window.matchMedia(MOBILE_QUERY).matches,
    () => false,
  );
}

function useEnvironmentBadge(): EnvironmentBadge {
  const hostname = useSyncExternalStore(
    () => () => undefined,
    () => window.location.hostname,
    () => "",
  );
  return environmentBadge(hostname);
}

function SideMenu({
  pathname,
  can,
  mobile,
  open,
  onClose,
  footer,
}: {
  pathname: string;
  can: (permission: string) => boolean;
  mobile: boolean;
  open: boolean;
  onClose: () => void;
  footer?: ReactNode;
}) {
  const [collapsed, setCollapsed] = useState<string[]>(() => (typeof window === "undefined" ? [] : readCollapsed()));
  const ref = useRef<HTMLElement>(null);

  const toggle = (title: string) => {
    const next = collapsed.includes(title) ? collapsed.filter((t) => t !== title) : [...collapsed, title];
    setCollapsed(next);
    writeCollapsed(next);
  };

  // The open panel keeps the focus inside (Tab cycles), Escape closes it.
  useEffect(() => {
    if (!mobile || !open) {
      return;
    }
    const nav = ref.current;
    const focusables = () => [...(nav?.querySelectorAll<HTMLElement>("a[href], button:not([disabled]), select:not([disabled])") ?? [])];
    focusables()[0]?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
        return;
      }
      if (e.key !== "Tab") {
        return;
      }
      const items = focusables();
      const first = items[0];
      const last = items[items.length - 1];
      if (!first || !last) {
        return;
      }
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [mobile, open, onClose]);

  const hidden = mobile && !open;
  return (
    <nav
      ref={ref}
      id="main-menu"
      className={`sidebar${mobile ? " sidebar-mobile" : ""}${open ? " open" : ""}`}
      aria-label="Menú principal"
      aria-hidden={hidden || undefined}
      inert={hidden || undefined}
      onClick={(e) => {
        if (mobile && (e.target as HTMLElement).closest("a")) {
          onClose();
        }
      }}
    >
      <div className="sidebar-head">
        <Link href="/" className="brand">
          <span className="brand-mark" aria-hidden="true">
            R
          </span>
          Rochell Core
        </Link>
        {mobile ? (
          <button type="button" className="menu-close" aria-label="Cerrar menú" onClick={onClose}>
            ×
          </button>
        ) : null}
      </div>
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
        const folded = collapsed.includes(group.title);
        const listId = `menu-group-${group.title.replace(/\W+/g, "-")}`;
        return (
          <div key={group.title} className="menu-group">
            <h2>
              <button type="button" className="menu-group-toggle" aria-expanded={!folded} aria-controls={listId} onClick={() => toggle(group.title)}>
                <span>{group.title}</span>
                <span className="chevron" aria-hidden="true">
                  {folded ? "▸" : "▾"}
                </span>
              </button>
            </h2>
            <ul id={listId} hidden={folded}>
              {items.map((item) => (
                <li key={item.href}>
                  <Link href={item.href} className={isActive(pathname, item.href) ? "active" : undefined} aria-current={isActive(pathname, item.href) ? "page" : undefined}>
                    {item.label}
                  </Link>
                </li>
              ))}
            </ul>
          </div>
        );
      })}
      {footer ? <div className="sidebar-footer">{footer}</div> : null}
    </nav>
  );
}

function PlantSelector() {
  const { company, plantId, selectPlant, plantScoped, plantName } = useSession();
  const assigned = [...new Set((company?.assignments ?? []).flatMap((a) => (a.plantId ? [a.plantId] : [])))];
  const firstPlant = assigned[0];

  if (!plantScoped) {
    return null;
  }
  return (
    <label className="plant-selector">
      Planta:{" "}
      <select aria-label="Planta" value={plantId ?? firstPlant ?? ""} onChange={(e) => selectPlant(e.target.value)}>
        {assigned.map((id) => (
          <option key={id} value={id}>
            {plantName(id, id.slice(0, 8))}
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

/** "Name · main role" (E-UX1-01-3): the Google name (the e-mail until the first sign-in brings it), the e-mail as tooltip. */
function UserBlock() {
  const { state, company, reload } = useSession();
  if (state.status !== "ready") {
    return null;
  }
  const session = state.session;
  const role = company?.assignments[0];
  const roleName = role ? (ROLES[role.roleCode] ?? role.roleName) : null;
  return (
    <span className="user-block">
      <span className="user" data-testid="user-email" title={session.email ?? undefined}>
        <strong>{session.displayName?.trim() || session.email}</strong>
        {roleName ? <span className="muted"> · {roleName}</span> : null}
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
    </span>
  );
}

function CompanyName() {
  const { state, company, selectCompany } = useSession();
  if (state.status !== "ready") {
    return null;
  }
  return state.session.companies.length > 1 ? (
    <select aria-label="Empresa" value={company?.companyId} onChange={(e) => selectCompany(e.target.value)}>
      {state.session.companies.map((c) => (
        <option key={c.companyId} value={c.companyId}>
          {c.legalName}
        </option>
      ))}
    </select>
  ) : (
    <span className="company">{company?.legalName ?? "Sin empresa asignada"}</span>
  );
}

function EnvironmentTag() {
  const badge = useEnvironmentBadge();
  return badge ? (
    <span className={`env-badge env-${badge.tone}`} data-testid="environment-badge" title="Ambiente de pruebas: los datos no son reales.">
      {badge.label}
    </span>
  ) : null;
}

export function Shell({ children }: { children: ReactNode }) {
  const { state, company, can } = useSession();
  const pathname = usePathname();
  const mobile = useIsMobile();
  const [menuOpen, setMenuOpen] = useState(false);
  const toggleRef = useRef<HTMLButtonElement>(null);
  const closeMenu = useCallback(() => {
    setMenuOpen(false);
    toggleRef.current?.focus();
  }, []);
  const menuVisible = mobile && menuOpen;

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
        <a className="button primary" href={loginUrl(pathname)}>
          Iniciar sesión
        </a>
      </main>
    );
  }

  const account = (
    <>
      <PlantSelector />
      <IdentitySelector />
      <UserBlock />
    </>
  );
  return (
    <div className={`app${mobile ? " app-mobile" : ""}`}>
      <SideMenu pathname={pathname} can={can} mobile={mobile} open={menuVisible} onClose={closeMenu} footer={mobile ? account : undefined} />
      {menuVisible ? <div className="menu-backdrop" aria-hidden="true" onClick={closeMenu} /> : null}
      <div className="main" inert={menuVisible || undefined}>
        {mobile ? (
          <header className="topbar topbar-mobile">
            <button
              ref={toggleRef}
              type="button"
              className="menu-toggle"
              aria-expanded={menuOpen}
              aria-controls="main-menu"
              onClick={() => setMenuOpen(true)}
            >
              <span aria-hidden="true">☰</span> Menú
            </button>
            <span className="screen-title">{screenTitle(pathname)}</span>
            <span className="topbar-right">
              <EnvironmentTag />
              <CompanyName />
            </span>
          </header>
        ) : (
          <header className="topbar">
            <EnvironmentTag />
            <span className="spacer" />
            <CompanyName />
            {account}
          </header>
        )}
        <main className="page">{company ? children : <p>No tiene roles asignados en ninguna empresa.</p>}</main>
      </div>
    </div>
  );
}
