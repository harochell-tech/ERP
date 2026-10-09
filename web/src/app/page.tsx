"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { lastEndedMonth } from "@/components/FixedAssets";
import { EcfContingencyNotice } from "@/components/EcfGateway";
import { EmptyState } from "@/components/StateNotices";
import { todayInDominicanRepublic } from "@/lib/labels";
import { isReadyToRelease } from "@/lib/production";
import { nextSteps, setupProgress, stepInfo } from "@/lib/setup";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";
import { authorizationAlertDays, countDraftsToApprove, countExpiringAuthorizations, countFiscalRulesToActivate, countPolicyDraftsToApprove } from "@/lib/ux4b";

/** Lists are read with this limit; a full page shows as "200+" (E-UI01-7). */
const COUNT_LIMIT = 200;

/** `isMine` leaves out the drafts the reader prepared: four eyes, another person approves them. */
type Counter = (companyId: string, plantId: string | null | undefined, isMine: (actor: string | null | undefined) => boolean) => Promise<number>;

interface Task {
  href: string;
  label: string;
  permission: string;
  /** The read permission the counter's list needs, when the task has a counter. */
  countPermission?: string;
  count?: Counter;
}

/** UX3-02 (E-UX3-13): shift summaries still in draft (runs in progress with a DRAFT summary), any day. */
const draftSummaries: Counter = async (companyId, plantId) =>
  (await query("/api/v1/companies/{companyId}/manufacturing/runs", { path: { companyId }, query: { plantId, status: "IN_PROGRESS", limit: COUNT_LIMIT } })).items.filter(
    (r) => r.summaryStatus === "DRAFT",
  ).length;

// E-UI01-7: each task's counter comes from an existing list query, only when the user may read it; no API of its own.
const TASKS: readonly Task[] = [
  { href: "/compras/ordenes/nueva/", label: "Crear una orden de compra", permission: "purchase_order:create" },
  { href: "/compras/ordenes/gasto/", label: "Crear una orden de gastos", permission: "purchase_order:create" },
  {
    href: "/compras/ordenes/?estado=PENDING_APPROVAL",
    label: "Aprobar órdenes de compra",
    permission: "purchase_order:approve",
    countPermission: "purchase_order:read",
    count: async (companyId, plantId) =>
      (await query("/api/v1/companies/{companyId}/procurement/purchase-orders", { path: { companyId }, query: { plantId, status: "PENDING_APPROVAL", limit: COUNT_LIMIT } })).items.length,
  },
  {
    // UX3-02 (E-UX3-5): approved and partially received orders, from the orders to receive.
    href: "/almacen/por-recibir/",
    label: "Recibir material",
    permission: "goods_receipt:post",
    countPermission: "purchase_order:read",
    count: async (companyId, plantId) =>
      (await query("/api/v1/companies/{companyId}/procurement/purchase-orders/to-receive", { path: { companyId }, query: { plantId, limit: COUNT_LIMIT } })).items.length,
  },
  {
    href: "/almacen/correcciones/",
    label: "Aprobar correcciones de recepción",
    permission: "receipt_correction:approve",
    countPermission: "goods_receipt:read",
    count: async (companyId, plantId) =>
      (await query("/api/v1/companies/{companyId}/procurement/receipt-corrections", { path: { companyId }, query: { plantId, documentStatus: "PENDING_APPROVAL", limit: COUNT_LIMIT } }))
        .items.length,
  },
  { href: "/cxp/facturas/nueva/", label: "Registrar una factura de proveedor", permission: "supplier_invoice:register" },
  { href: "/cxp/facturas/gasto/", label: "Registrar una factura de gastos", permission: "supplier_invoice:register" },
  // OCR1-03 (E-OCR1-03-8): supplier documents waiting to become invoices, and answers to the DGII stuck for a day.
  {
    href: "/compras/comprobantes/?estado=CAPTURED",
    label: "Comprobantes recibidos por registrar",
    permission: "supplier_invoice:register",
    countPermission: "supplier_invoice:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/procurement/supplier-documents", { path: { companyId }, query: { status: "CAPTURED", limit: COUNT_LIMIT } })).items.length,
  },
  {
    href: "/compras/comprobantes/?sinEnviar=1",
    label: "Respuestas a la DGII sin enviar hace más de 24 horas",
    permission: "supplier_document:respond",
    countPermission: "supplier_invoice:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/procurement/supplier-documents", { path: { companyId }, query: { unsentOver24Hours: "true", limit: COUNT_LIMIT } })).items.length,
  },
  // GAS1-07 (E-GAS-07-7): expense categories waiting for the Controller.
  {
    href: "/maestros/categorias-gasto/",
    label: "Categorías de gasto por aprobar",
    permission: "expense_category:approve",
    countPermission: "master_data:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/procurement/expense-categories", { path: { companyId }, query: { status: "DRAFT" } })).items.length,
  },
  // USD1-07a (E-USD1-07-1, E-USD1-02-6): the Controller approves the day's rate; Tesorería sees when today's is missing.
  {
    href: "/contabilidad/tasas/",
    label: "Tasas de cambio por aprobar",
    permission: "exchange_rate:approve",
    countPermission: "exchange_rate:read",
    count: async (companyId, _plantId, isMine) =>
      (await query("/api/v1/companies/{companyId}/finance/exchange-rates", { path: { companyId } })).items.filter((r) => r.status === "DRAFT" && !isMine(r.preparedBy)).length,
  },
  {
    href: "/contabilidad/tasas/",
    label: "Falta la tasa del dólar de hoy",
    permission: "exchange_rate:prepare",
    countPermission: "exchange_rate:read",
    count: async (companyId) =>
      query("/api/v1/companies/{companyId}/finance/exchange-rates/for-date", { path: { companyId }, query: { date: todayInDominicanRepublic() } }).then(
        () => 0,
        () => 1,
      ),
  },
  // AF1-05 (E-AF1-05-8): fixed assets — the Contador's service and month's depreciation, the Controller's approvals.
  {
    href: "/contabilidad/activos/?estado=AWAITING_SERVICE",
    label: "Activos por poner en servicio",
    permission: "fixed_asset:manage",
    countPermission: "ledger:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/fixed-assets/assets", { path: { companyId }, query: { status: "AWAITING_SERVICE", limit: COUNT_LIMIT } })).items.length,
  },
  {
    href: "/contabilidad/activos/depreciacion/",
    label: "Depreciación del mes pendiente",
    permission: "fixed_asset:manage",
    countPermission: "ledger:read",
    count: async (companyId) => {
      const p = await query("/api/v1/companies/{companyId}/fixed-assets/depreciation-preview", { path: { companyId }, query: { month: lastEndedMonth() } });
      return p.skippedMonth || (!p.alreadyPosted && p.lines.length > 0) ? 1 : 0;
    },
  },
  {
    href: "/contabilidad/activos/clases/",
    label: "Clases de activos por aprobar",
    permission: "fixed_asset:approve",
    countPermission: "ledger:read",
    count: async (companyId, _plantId, isMine) =>
      (await query("/api/v1/companies/{companyId}/fixed-assets/classes", { path: { companyId }, query: { status: "DRAFT" } })).items.filter((k) => !isMine(k.preparedByName))
        .length,
  },
  {
    href: "/contabilidad/activos/bajas/",
    label: "Bajas de activos por aprobar",
    permission: "fixed_asset:approve",
    countPermission: "ledger:read",
    count: async (companyId, _plantId, isMine) =>
      (await query("/api/v1/companies/{companyId}/fixed-assets/disposals", { path: { companyId }, query: { status: "DRAFT" } })).items.filter((d) => !isMine(d.preparedBy)).length,
  },
  {
    href: "/contabilidad/activos/carga/",
    label: "Cargas de activos por aprobar",
    permission: "fixed_asset:approve",
    countPermission: "ledger:read",
    count: async (companyId, _plantId, isMine) =>
      (await query("/api/v1/companies/{companyId}/fixed-assets/loads", { path: { companyId } })).items.filter((l) => l.status === "DRAFT" && !isMine(l.preparedBy)).length,
  },
  { href: "/tesoreria/propuesta/", label: "Preparar pagos a proveedores", permission: "payment:prepare" },
  {
    href: "/tesoreria/pagos/?estado=PREPARED",
    label: "Liberar pagos preparados",
    permission: "payment:release",
    countPermission: "payment:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/treasury/payments", { path: { companyId }, query: { status: "PREPARED", limit: COUNT_LIMIT } })).items.length,
  },
  { href: "/tesoreria/extractos/", label: "Importar un extracto bancario", permission: "bank_statement:import" },
  {
    href: "/tesoreria/conciliacion/",
    label: "Conciliar el banco",
    permission: "bank_line:match",
    countPermission: "bank:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/treasury/bank-statement-lines", { path: { companyId }, query: { status: "UNMATCHED", limit: COUNT_LIMIT } })).items.length,
  },
  // VS3-10b (E-VS3-10-9): sales work.
  { href: "/ventas/pedidos/nuevo/", label: "Crear un pedido de venta", permission: "sales_order:create" },
  // QUO1-04 (E-QUO1-04-7): quotes with special prices waiting for the Aprobador de políticas.
  { href: "/ventas/cotizaciones/nueva/", label: "Crear una cotización", permission: "quote:manage" },
  // CF1-05 (E-CF1-05-10): cash sales to the final consumer, for Vendedor and Caja.
  { href: "/ventas/contado/nueva/", label: "Hacer una venta de contado", permission: "cash_sale:create" },
  {
    href: "/ventas/contado/",
    label: "Ventas de contado pendientes de pago",
    permission: "cash_sale:create",
    countPermission: "sales:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/sales/orders", { path: { companyId }, query: { status: "PENDING_PAYMENT", cashSale: "true", limit: COUNT_LIMIT } })).items.length,
  },
  {
    href: "/ventas/cotizaciones/?estado=PENDING_APPROVAL",
    label: "Precios de cotización por aprobar",
    permission: "quote:approve_price",
    countPermission: "sales:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/sales/quotes", { path: { companyId }, query: { status: "PENDING_APPROVAL", limit: COUNT_LIMIT } })).items.length,
  },
  {
    href: "/ventas/pedidos/?estado=PENDING_CREDIT",
    label: "Aprobar crédito de pedidos",
    permission: "credit:approve",
    countPermission: "sales:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/sales/orders", { path: { companyId }, query: { status: "PENDING_CREDIT", limit: COUNT_LIMIT } })).items.length,
  },
  {
    href: "/despacho/tablero/",
    label: "Despachar (conduces en tránsito)",
    permission: "delivery:manage",
    countPermission: "sales:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/sales/deliveries", { path: { companyId }, query: { status: "IN_TRANSIT", limit: COUNT_LIMIT } })).items.length,
  },
  // ENT1-03 (E-ENT1-01-10): the driver reported differences from the QR page; Dispatch completes the delivery.
  {
    href: "/despacho/tablero/?chofer=diferencias",
    label: "Entregas con diferencias reportadas por el chofer",
    permission: "delivery:manage",
    countPermission: "sales:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/sales/deliveries", { path: { companyId }, query: { driverReportedDifferences: "true", limit: COUNT_LIMIT } })).items.length,
  },
  { href: "/facturacion/por-facturar/", label: "Facturar entregas", permission: "invoice:create" },
  {
    href: "/facturacion/facturas/?filtro=ecf",
    label: "Registrar e-CF pendientes",
    permission: "fiscal_document:record",
    countPermission: "sales:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/sales/invoices", { path: { companyId }, query: { fiscalStatus: "PENDING_EXTERNAL", limit: COUNT_LIMIT } })).items.filter((i) => i.commercialStatus !== "VOIDED").length,
  },
  {
    href: "/cobros/recibos/?filtro=sin-aplicar",
    label: "Aplicar cobros sin aplicar",
    permission: "receipt:apply",
    countPermission: "sales:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/sales/receipts", { path: { companyId }, query: { status: "RECORDED", applicationStatus: "UNAPPLIED", limit: COUNT_LIMIT } })).items.length,
  },
  // MFG2-03 (E-MFG2-7/8): drafts from the machines' portal still without the batch plant's consumption, consumption beyond the
  // usage tolerance, and the portal's warnings for the plant manager.
  {
    href: "/produccion/dia/",
    label: "Resúmenes del portal sin consumo de dosificadora",
    permission: "shift_summary:record",
    countPermission: "production:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/manufacturing/portal", { path: { companyId } })).pendingConsumption,
  },
  {
    href: "/produccion/dia/",
    label: "Consumo de materia prima fuera de tolerancia",
    permission: "shift_summary:post",
    countPermission: "production:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/manufacturing/portal", { path: { companyId } })).outOfTolerance,
  },
  {
    href: "/produccion/portal/",
    label: "Avisos del portal de máquinas",
    permission: "portal:manage",
    countPermission: "production:read",
    count: async (companyId) => {
      const portal = await query("/api/v1/companies/{companyId}/manufacturing/portal", { path: { companyId } });
      return portal.warnings.length + portal.unpairedMachines.length + (portal.lastError ? 1 : 0);
    },
  },
  // MFG3-04 (E-MFG3-7/10): preventive maintenance overdue and due soon; the week's stoppages without a reason.
  {
    href: "/produccion/mantenimiento/",
    label: "Mantenimientos vencidos",
    permission: "maintenance_plan:manage",
    countPermission: "production:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/manufacturing/maintenance-tasks", { path: { companyId } })).overdue,
  },
  {
    href: "/produccion/mantenimiento/?por-vencer=1",
    label: "Mantenimientos por vencer",
    permission: "maintenance_plan:manage",
    countPermission: "production:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/manufacturing/maintenance-tasks", { path: { companyId } })).dueSoon,
  },
  {
    href: "/produccion/eficiencia/",
    label: "Paros sin razón esta semana",
    permission: "production:read",
    count: async (companyId) => {
      const to = todayInDominicanRepublic();
      const d = new Date(`${to}T12:00:00Z`);
      d.setUTCDate(d.getUTCDate() - 6);
      return (await query("/api/v1/companies/{companyId}/manufacturing/efficiency", { path: { companyId }, query: { from: d.toISOString().slice(0, 10), to } }))
        .stoppagesWithoutReason;
    },
  },
  // VS4-04 (E-VS4-04-1/6): e-CF needing attention, rejected ones to resend or void, ranges running out or expiring.
  {
    href: "/fiscal/ecf/?estado=REQUIRES_ACTION",
    label: "e-CF que requieren atención",
    permission: "ecf:resolve",
    countPermission: "sales:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/ecf/alerts", { path: { companyId } })).requiresAction,
  },
  {
    href: "/fiscal/ecf/?estado=REJECTED",
    label: "e-CF rechazados por reenviar o anular",
    permission: "invoice:issue",
    countPermission: "sales:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/ecf/alerts", { path: { companyId } })).rejected,
  },
  {
    href: "/fiscal/rangos/",
    label: "Rangos e-NCF por agotarse o vencer",
    permission: "ecf_series:prepare",
    countPermission: "sales:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/ecf/alerts", { path: { companyId } })).ranges.length,
  },
  // FIS1-05 (E-FIS1-05-10): CONFOTUR authorizations waiting for the Especialista fiscal.
  {
    href: "/fiscal/autorizaciones/?estado=PENDING_VERIFICATION",
    label: "Autorizaciones por verificar",
    permission: "fiscal_authorization:verify",
    countPermission: "sales:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/tax/fiscal-authorizations", { path: { companyId }, query: { status: "PENDING_VERIFICATION" } })).items.length,
  },
  // UX3-02 (E-UX3-13): production work, counted from the existing runs, recipes and lots queries.
  {
    href: "/produccion/dia/",
    label: "Corridas de hoy sin resumen",
    permission: "shift_summary:record",
    countPermission: "production:read",
    count: async (companyId, plantId) =>
      (
        await query("/api/v1/companies/{companyId}/manufacturing/runs", {
          path: { companyId },
          query: { plantId, businessDate: todayInDominicanRepublic(), status: "IN_PROGRESS", limit: COUNT_LIMIT },
        })
      ).items.filter((r) => r.summaryStatus === null).length,
  },
  {
    href: "/produccion/dia/#resumenes-borrador",
    label: "Resúmenes de turno en borrador",
    permission: "shift_summary:record",
    countPermission: "production:read",
    count: draftSummaries,
  },
  {
    href: "/produccion/dia/#resumenes-borrador",
    label: "Resúmenes de turno por cerrar",
    permission: "shift_summary:post",
    countPermission: "production:read",
    count: draftSummaries,
  },
  {
    href: "/produccion/recetas/",
    label: "Recetas por aprobar",
    permission: "recipe:approve",
    countPermission: "production:read",
    count: async (companyId, plantId) => (await query("/api/v1/companies/{companyId}/manufacturing/recipes", { path: { companyId }, query: { plantId, status: "DRAFT" } })).items.length,
  },
  {
    href: "/produccion/lotes/",
    label: "Lotes listos para liberar",
    permission: "fg_lot:release",
    countPermission: "production:read",
    count: async (companyId, plantId) =>
      (await query("/api/v1/companies/{companyId}/manufacturing/lots", { path: { companyId }, query: { plantId, status: "CURING", limit: COUNT_LIMIT } })).items.filter(isReadyToRelease)
        .length,
  },
  // UX4-03 (G-15): configuration waiting for its approver, counted from the configuration screens' own lists (configuration:read).
  {
    href: "/contabilidad/politicas/",
    label: "Políticas contables por aprobar",
    permission: "accounting_policy:approve",
    countPermission: "configuration:read",
    count: async (companyId, _plantId, isMine) =>
      countPolicyDraftsToApprove((await query("/api/v1/companies/{companyId}/finance/accounting-policies", { path: { companyId } })).items, isMine),
  },
  {
    href: "/contabilidad/mapas/",
    label: "Cuentas por rol por aprobar",
    permission: "account_role_map:approve",
    countPermission: "configuration:read",
    count: async (companyId, _plantId, isMine) =>
      countDraftsToApprove((await query("/api/v1/companies/{companyId}/finance/account-role-maps", { path: { companyId }, query: { status: "DRAFT" } })).items, isMine),
  },
  {
    href: "/contabilidad/estructuras/",
    label: "Estructuras de reporte por aprobar",
    permission: "report_structure:approve",
    countPermission: "configuration:read",
    count: async (companyId, _plantId, isMine) =>
      countDraftsToApprove((await query("/api/v1/companies/{companyId}/finance/report-structures", { path: { companyId } })).items, isMine),
  },
  {
    href: "/contabilidad/reglas/",
    label: "Reglas de contabilización por aprobar",
    permission: "posting_rule:approve",
    countPermission: "configuration:read",
    count: async (companyId) => (await query("/api/v1/companies/{companyId}/finance/posting-rules", { path: { companyId } })).items.filter((r) => r.status === "DRAFT").length,
  },
  {
    href: "/fiscal/reglas/",
    label: "Reglas fiscales por activar",
    permission: "fiscal_rule:activate",
    countPermission: "configuration:read",
    count: async (companyId, _plantId, isMine) =>
      countFiscalRulesToActivate((await query("/api/v1/companies/{companyId}/tax/fiscal-rules", { path: { companyId } })).items, isMine),
  },
  {
    // The alert window is the REVENUE_ACCOUNTING policy's authorization_expiry_alert_days, the AUTH-EXPIRY reconciliation's.
    href: "/fiscal/autorizaciones/?estado=ACTIVE",
    label: "Autorizaciones fiscales por vencer",
    permission: "fiscal_authorization:suspend",
    countPermission: "configuration:read",
    count: async (companyId) => {
      const [policies, authorizations] = await Promise.all([
        query("/api/v1/companies/{companyId}/finance/accounting-policies", { path: { companyId } }),
        query("/api/v1/companies/{companyId}/tax/fiscal-authorizations", { path: { companyId }, query: { status: "ACTIVE" } }),
      ]);
      return countExpiringAuthorizations(authorizations.items, authorizationAlertDays(policies.items, todayInDominicanRepublic()));
    },
  },
  { href: "/cierre/conciliaciones/", label: "Ejecutar conciliaciones", permission: "reconciliation:run" },
  // UX4-03 (G-16): closing is for whoever may close a component, not for every reader of the periods (the Auditor).
  { href: "/cierre/periodos/", label: "Cerrar o reabrir períodos", permission: "period_component:close" },
  { href: "/seguridad/usuarios/", label: "Solicitar cambios de rol", permission: "role:assign" },
  {
    href: "/seguridad/solicitudes/",
    label: "Decidir solicitudes de rol",
    permission: "role:second_approve",
    countPermission: "iam:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/identity/role-requests", { path: { companyId }, query: { status: "REQUESTED", limit: COUNT_LIMIT } })).items.length,
  },
];

function TaskItem({ task }: { task: Task }) {
  const { companyId, can, plantFor, isMine } = useSession();
  const counted = task.count !== undefined && task.countPermission !== undefined && can(task.countPermission);
  const plantId = task.countPermission ? plantFor(task.countPermission) : undefined;
  const { data } = useLoad(counted && task.count ? () => task.count!(companyId, plantId, isMine) : null, [companyId, plantId, counted]);
  return (
    <li>
      <Link href={task.href}>{task.label}</Link>
      {counted && data !== null ? (
        <span className={`badge ${data > 0 ? "tone-attention" : "tone-neutral"}`} style={{ marginLeft: 8 }} data-testid={`task-count:${task.href}`}>
          {data >= COUNT_LIMIT ? `${COUNT_LIMIT}+` : data}
        </span>
      ) : null}
    </li>
  );
}

/** UX2-02 (E-UX2-10): "Puesta en marcha" for configuration:read holders until the 19 steps are DONE. */
function SetupCard() {
  const { companyId, can } = useSession();
  const allowed = can("configuration:read");
  const { data } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/reconciliation/setup-status", { path: { companyId } }) : null, [companyId, allowed]);
  if (!allowed || data === null || data.complete) {
    return null;
  }
  const progress = setupProgress(data);
  return (
    <section className="card" data-testid="setup-card">
      <h2 style={{ marginTop: 0 }}>Puesta en marcha</h2>
      <p>
        <strong data-testid="setup-card-progress">
          {progress.done} de {progress.total} pasos listos
        </strong>
      </p>
      <progress value={progress.done} max={progress.total} aria-label="Pasos listos" style={{ width: "100%" }} />
      <p style={{ marginBottom: 4 }}>Próximos pasos:</p>
      <ul>
        {nextSteps(data).map((s) => {
          const info = stepInfo(s.code);
          return (
            <li key={s.code}>
              <Link href={info.href}>{info.title}</Link>
            </li>
          );
        })}
      </ul>
      <Link href="/configuracion/">Ir al Centro de configuración</Link>
    </section>
  );
}

export default function Home() {
  const { company, can, plantName } = useSession();
  const tasks = TASKS.filter((t) => can(t.permission));
  return (
    <>
      <h1>Inicio</h1>
      <p>
        Roles en {company?.legalName}:{" "}
        {company?.assignments.map((a) => (a.plantId ? `${a.roleName} (planta ${plantName(a.plantId, a.plantId.slice(0, 8))})` : a.roleName)).join(", ")}
      </p>
      <SetupCard />
      <EcfContingencyNotice />
      <h2>Tareas</h2>
      {tasks.length === 0 ? (
        <EmptyState title="No hay tareas para sus roles en Inicio." testId="no-tasks">
          <p>Lo que puede consultar está en el menú. Si necesita otra pantalla, pida el rol en Seguridad › Solicitudes de rol.</p>
        </EmptyState>
      ) : (
        <ul data-testid="tasks">
          {tasks.map((t) => (
            <TaskItem key={`${t.href}|${t.label}`} task={t} />
          ))}
        </ul>
      )}
    </>
  );
}
