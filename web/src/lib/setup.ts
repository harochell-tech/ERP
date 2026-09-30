// E-UX2-9/10: the setup steps of Reconciliation.GetSetupStatus in Spanish — titles, areas, where each is resolved and what the
// `missing` codes mean. Pure and unit-tested; the status of each step is the server's.
import { FISCAL_KIND_LABELS } from "./configuration";
import { ROLES } from "./labels";
import { REPORTS } from "./ledger";

export interface SetupStepLike {
  order: number;
  code: string;
  area: string;
  status: string;
  missing: readonly string[];
}

export interface SetupStatusLike {
  steps: readonly SetupStepLike[];
  complete: boolean;
}

export interface StepInfo {
  title: string;
  /** The screen that resolves the step. */
  href: string;
  /** Its menu name, for the link. */
  screen: string;
}

/** The 19 steps, in the server's order (E-UX2-10). */
export const SETUP_STEPS: Readonly<Record<string, StepInfo>> = {
  COMPANY: { title: "Datos de la empresa", href: "/configuracion/empresa/", screen: "Empresa" },
  PLANTS: { title: "Plantas con nombre", href: "/configuracion/empresa/", screen: "Empresa" },
  USERS: { title: "Usuarios con los roles clave", href: "/seguridad/usuarios/", screen: "Usuarios y roles" },
  PERIODS: { title: "Período contable abierto para hoy", href: "/cierre/periodos/", screen: "Períodos y cierre" },
  ACCOUNTS: { title: "Catálogo de cuentas con clase", href: "/contabilidad/cuentas/", screen: "Catálogo de cuentas" },
  REPORT_STRUCTURES: { title: "Estructuras del balance y del estado de resultados", href: "/contabilidad/estructuras/", screen: "Estructuras de reporte" },
  ACCOUNT_MAPS: { title: "Mapas de cuentas", href: "/contabilidad/mapas/", screen: "Mapas de cuentas" },
  POSTING_RULES: { title: "Reglas contables aprobadas", href: "/contabilidad/reglas/", screen: "Reglas contables" },
  POLICIES: { title: "Políticas contables en vigor", href: "/contabilidad/politicas/", screen: "Políticas" },
  FISCAL_SOURCES: { title: "Fuentes fiscales oficiales", href: "/fiscal/fuentes/", screen: "Fuentes fiscales" },
  FISCAL_RULES: { title: "Reglas fiscales activas", href: "/fiscal/reglas/", screen: "Reglas fiscales" },
  BANK_ACCOUNTS: { title: "Cuenta bancaria de la empresa", href: "/maestros/cuentas-bancarias/", screen: "Cuentas bancarias de la empresa" },
  SUPPLIERS: { title: "Proveedores activos", href: "/maestros/proveedores/", screen: "Proveedores" },
  ITEMS: { title: "Materias primas y productos terminados", href: "/maestros/articulos/", screen: "Materias primas" },
  STANDARD_COSTS: { title: "Costos estándar de los productos terminados", href: "/maestros/costos-estandar/", screen: "Costos estándar" },
  PRICE_LIST: { title: "Lista de precios", href: "/maestros/precios/", screen: "Lista de precios" },
  CUSTOMERS: { title: "Clientes con condiciones de crédito", href: "/ventas/clientes/", screen: "Clientes" },
  RECIPES: { title: "Recetas de producción", href: "/produccion/recetas/", screen: "Recetas" },
  OPENING_INVENTORY: { title: "Inventario de apertura contabilizado", href: "/contabilidad/apertura/", screen: "Apertura de inventario" },
};

/** The areas, in the order the Centro de configuración shows them. */
export const SETUP_AREAS: readonly { code: string; label: string }[] = [
  { code: "EMPRESA", label: "Empresa" },
  { code: "SEGURIDAD", label: "Seguridad" },
  { code: "CONTABILIDAD", label: "Contabilidad" },
  { code: "FISCAL", label: "Fiscal" },
  { code: "TESORERIA", label: "Tesorería" },
  { code: "MAESTROS", label: "Maestros" },
  { code: "VENTAS", label: "Ventas" },
  { code: "PRODUCCION", label: "Producción" },
  { code: "INVENTARIO", label: "Inventario" },
];

export function areaLabel(code: string): string {
  return SETUP_AREAS.find((a) => a.code === code)?.label ?? code;
}

export function stepInfo(code: string): StepInfo {
  return SETUP_STEPS[code] ?? { title: code, href: "/configuracion/", screen: "Centro de configuración" };
}

export const STEP_STATUS_LABELS: Readonly<Record<string, string>> = { DONE: "Listo", WARNING: "Incompleto", PENDING: "Pendiente" };

export type Light = "green" | "amber" | "red";

/** E-UX2-9: red when any step is PENDING, amber when any is WARNING, green when all are DONE. */
export function areaLight(steps: readonly SetupStepLike[]): Light {
  if (steps.some((s) => s.status === "PENDING")) {
    return "red";
  }
  if (steps.some((s) => s.status === "WARNING")) {
    return "amber";
  }
  return "green";
}

export const LIGHT_LABELS: Readonly<Record<Light, string>> = { green: "Completo", amber: "Incompleto", red: "Pendiente" };

/** Names the screen knows beyond the fixed labels (account roles and policies come from their queries). */
export interface SetupNames {
  accountRoles?: Readonly<Record<string, string>>;
  policies?: Readonly<Record<string, string>>;
}

const POLICY_NAMES: Readonly<Record<string, string>> = {
  PURCHASING: "Compras",
  INVENTORY: "Inventario",
  POSTING: "Contabilización",
  TREASURY: "Tesorería",
  CREDIT: "Crédito",
  REVENUE_ACCOUNTING: "Ingresos y entregas",
  PRODUCTION: "Producción",
};

/** One `missing` code of a step in Spanish (E-UX2-10). Codes it does not know are shown as they come. */
export function missingLabel(stepCode: string, item: string, names: SetupNames = {}): string {
  switch (stepCode) {
    case "PLANTS":
      return `Planta ${item} sin nombre`;
    case "USERS":
      return `Nadie tiene el rol ${ROLES[item] ?? item}`;
    case "PERIODS":
      return item === "TODAY" ? "No hay un período que cubra la fecha de hoy" : item;
    case "ACCOUNTS":
      return `Cuenta ${item} sin clase`;
    case "REPORT_STRUCTURES":
      return `${REPORTS[item] ?? item} sin estructura activa`;
    case "ACCOUNT_MAPS":
      return `${names.accountRoles?.[item] ?? item} sin cuenta asignada para hoy`;
    case "POSTING_RULES":
      return `Regla ${item} sin versión aprobada para hoy`;
    case "POLICIES":
      return `Política ${names.policies?.[item] ?? POLICY_NAMES[item] ?? item} sin versión completa en vigor`;
    case "FISCAL_SOURCES":
      return item === "SOURCE" ? "No hay fuentes fiscales registradas para este ambiente" : item;
    case "FISCAL_RULES":
      return `Falta una regla activa de ${FISCAL_KIND_LABELS[item] ?? item}`;
    case "BANK_ACCOUNTS":
      return item === "BANK_ACCOUNT" ? "No hay una cuenta bancaria activa" : item;
    case "SUPPLIERS":
      return item === "SUPPLIER" ? "No hay un proveedor activo" : item;
    case "ITEMS":
      return item === "RAW_MATERIAL" ? "No hay una materia prima activa" : item === "FINISHED_GOOD" ? "No hay un producto terminado activo" : item;
    case "STANDARD_COSTS":
      return item === "FINISHED_GOOD" ? "No hay un producto terminado activo" : `${item} sin costo estándar activo`;
    case "PRICE_LIST":
      return item === "PRICE_LIST" ? "No hay una lista de precios activa" : item;
    case "CUSTOMERS":
      return item === "CUSTOMER" ? "No hay un cliente activo" : `${item} sin condiciones de crédito activas`;
    case "RECIPES":
      return item === "FINISHED_GOOD" ? "No hay un producto terminado activo" : `${item} sin receta activa`;
    case "OPENING_INVENTORY":
      return item === "BATCH" ? "No hay un lote de inventario de apertura contabilizado" : item;
    default:
      return item;
  }
}

/** Steps DONE out of all (the Inicio card counts the array's items). */
export function setupProgress(status: SetupStatusLike): { done: number; total: number } {
  return { done: status.steps.filter((s) => s.status === "DONE").length, total: status.steps.length };
}

/** The first steps not DONE, in order. */
export function nextSteps(status: SetupStatusLike, count = 3): SetupStepLike[] {
  return [...status.steps].sort((a, b) => a.order - b.order).filter((s) => s.status !== "DONE").slice(0, count);
}
