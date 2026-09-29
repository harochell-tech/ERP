"use client";

import Link from "next/link";
import { query } from "@/api/client";
import { useSession } from "@/lib/session";
import { useLoad } from "@/lib/useQuery";

/** Lists are read with this limit; a full page shows as "200+" (E-UI01-7). */
const COUNT_LIMIT = 200;

type Counter = (companyId: string, plantId: string | null | undefined) => Promise<number>;

interface Task {
  href: string;
  label: string;
  permission: string;
  /** The read permission the counter's list needs, when the task has a counter. */
  countPermission?: string;
  count?: Counter;
}

// E-UI01-7: each task's counter comes from an existing list query, only when the user may read it; no API of its own.
const TASKS: readonly Task[] = [
  { href: "/compras/ordenes/nueva/", label: "Crear una orden de compra", permission: "purchase_order:create" },
  {
    href: "/compras/ordenes/?estado=PENDING_APPROVAL",
    label: "Aprobar órdenes de compra",
    permission: "purchase_order:approve",
    countPermission: "purchase_order:read",
    count: async (companyId, plantId) =>
      (await query("/api/v1/companies/{companyId}/procurement/purchase-orders", { path: { companyId }, query: { plantId, status: "PENDING_APPROVAL", limit: COUNT_LIMIT } })).items.length,
  },
  {
    href: "/compras/ordenes/?estado=APPROVED",
    label: "Recibir material",
    permission: "goods_receipt:post",
    countPermission: "purchase_order:read",
    count: async (companyId, plantId) =>
      (await query("/api/v1/companies/{companyId}/procurement/purchase-orders", { path: { companyId }, query: { plantId, status: "APPROVED", limit: COUNT_LIMIT } })).items.length,
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
  // FIS1-05 (E-FIS1-05-10): CONFOTUR authorizations waiting for the Especialista fiscal.
  {
    href: "/fiscal/autorizaciones/?estado=PENDING_VERIFICATION",
    label: "Autorizaciones por verificar",
    permission: "fiscal_authorization:verify",
    countPermission: "sales:read",
    count: async (companyId) =>
      (await query("/api/v1/companies/{companyId}/tax/fiscal-authorizations", { path: { companyId }, query: { status: "PENDING_VERIFICATION" } })).items.length,
  },
  { href: "/cierre/conciliaciones/", label: "Ejecutar conciliaciones", permission: "reconciliation:run" },
  { href: "/cierre/periodos/", label: "Cerrar o reabrir períodos", permission: "period:read" },
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
  const { companyId, can, plantFor } = useSession();
  const counted = task.count !== undefined && task.countPermission !== undefined && can(task.countPermission);
  const plantId = task.countPermission ? plantFor(task.countPermission) : undefined;
  const { data } = useLoad(counted && task.count ? () => task.count!(companyId, plantId) : null, [companyId, plantId, counted]);
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

export default function Home() {
  const { company, can } = useSession();
  const tasks = TASKS.filter((t) => can(t.permission));
  return (
    <>
      <h1>Inicio</h1>
      <p>
        Roles en {company?.legalName}: {company?.assignments.map((a) => a.roleName).join(", ")}
      </p>
      <h2>Tareas</h2>
      {tasks.length === 0 ? (
        <p className="muted">Sus roles no tienen tareas en esta versión de la interfaz.</p>
      ) : (
        <ul>
          {tasks.map((t) => (
            <TaskItem key={t.href} task={t} />
          ))}
        </ul>
      )}
      <p className="muted">
        Ningún estado mostrado en pantalla es evidencia contable: lo contabilizado es lo que está en los asientos (E-11).
      </p>
    </>
  );
}
