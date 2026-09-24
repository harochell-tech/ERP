"use client";

import Link from "next/link";
import { useSession } from "@/lib/session";

const TASKS: readonly { href: string; label: string; permission: string }[] = [
  { href: "/compras/ordenes/nueva/", label: "Crear una orden de compra", permission: "purchase_order:create" },
  { href: "/compras/ordenes/?estado=PENDING_APPROVAL", label: "Aprobar órdenes de compra", permission: "purchase_order:approve" },
  { href: "/compras/ordenes/?estado=APPROVED", label: "Recibir material", permission: "goods_receipt:post" },
  { href: "/almacen/correcciones/", label: "Aprobar correcciones de recepción", permission: "receipt_correction:approve" },
  { href: "/cxp/facturas/nueva/", label: "Registrar una factura de proveedor", permission: "supplier_invoice:register" },
  { href: "/cierre/conciliaciones/", label: "Ejecutar conciliaciones", permission: "reconciliation:run" },
  { href: "/cierre/periodos/", label: "Cerrar o reabrir períodos", permission: "period:read" },
];

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
            <li key={t.href}>
              <Link href={t.href}>{t.label}</Link>
            </li>
          ))}
        </ul>
      )}
      <p className="muted">
        Ningún estado mostrado en pantalla es evidencia contable: lo contabilizado es lo que está en los asientos (E-11).
      </p>
    </>
  );
}
