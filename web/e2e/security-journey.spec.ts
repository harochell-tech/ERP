import { expect, test } from "@playwright/test";
import { confirmAction, nav, signIn } from "./support";

// UI-01 (E-UI01-8): the security administrator requests a role for a new employee, the second approver approves it, and the
// employee signs in to a menu that follows the new role; the read-only audit and master screens open for their readers.


test("a role requested by the security admin is approved by a second person", async ({ browser }) => {
  const admin = await signIn(browser, "Administrador de seguridad");
  await nav(admin, "Usuarios y roles");
  const newcomer = (await admin.locator("tr", { hasText: "Sin roles" }).locator("td").first().innerText()).trim();
  await admin.getByLabel("Usuario").selectOption({ label: newcomer });
  await admin.getByLabel("Rol").selectOption({ label: "Comprador" });
  await admin.getByRole("button", { name: "Solicitar", exact: true }).click();
  await expect(admin.getByText("Solicitud enviada")).toBeVisible();

  const approver = await signIn(browser, "Segundo aprobador de seguridad");
  await nav(approver, "Solicitudes de rol");
  const row = approver.locator("tr", { hasText: newcomer });
  await confirmAction(row, "Aprobar");
  await expect(approver.getByText("No hay solicitudes.")).toBeVisible();

  const employee = await signIn(browser, "Empleado nuevo (sin roles)");
  await expect(employee.getByRole("link", { name: "Órdenes de compra" })).toBeVisible();
  await expect(employee.getByRole("link", { name: "Usuarios y roles" })).toHaveCount(0);
});

test("the read-only audit and master screens open for their readers", async ({ browser }) => {
  const controller = await signIn(browser, "Controller");
  await nav(controller, "Plantas y ubicaciones");
  await expect(controller.getByRole("heading", { name: "Plantas y ubicaciones" })).toBeVisible();
  await expect(controller.locator("tbody tr").first()).toBeVisible();
  await nav(controller, "Catálogo de cuentas");
  await expect(controller.getByRole("cell", { name: "1101" })).toBeVisible();

  const auditor = await signIn(browser, "Auditor");
  await nav(auditor, "Verificar cadena");
  await auditor.getByRole("button", { name: "Verificar cadena" }).click();
  // The dev stack has no WORM storage: the screen says so instead of failing (E-UI01-2).
  await expect(auditor.getByText("no está disponible en este ambiente").or(auditor.getByTestId("chain-result"))).toBeVisible();
  await nav(auditor, "Resúmenes en WORM");
  await expect(auditor.getByRole("heading", { name: "Resúmenes diarios en WORM" })).toBeVisible();
  await expect(auditor.getByRole("link", { name: "Usuarios y roles" })).toBeVisible();
});
