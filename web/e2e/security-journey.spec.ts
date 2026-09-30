import { expect, test } from "@playwright/test";
import { confirmAction, nav, signIn } from "./support";

// UI-01 (E-UI01-8): the security administrator requests a role for a new employee, the second approver approves it, and the
// employee signs in to a menu that follows the new role; the read-only audit and master screens open for their readers.


test("a role requested by the security admin is approved by a second person", async ({ browser }) => {
  const admin = await signIn(browser, "Administrador de seguridad");
  await nav(admin, "Usuarios y roles");
  const newcomer = (await admin.locator("tr", { hasText: "Sin roles" }).locator("td").first().innerText()).trim();
  await admin.locator("form").getByLabel("Usuario").selectOption({ label: newcomer });
  await admin.locator("form").getByLabel("Rol").selectOption({ label: "Comprador" });
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

  // UX4-02 (A-07, A-08): the journals page without an event is a search by document number; a posted supplier invoice's NCF finds
  // its journals, and the page is named after the document.
  await nav(controller, "Facturas de proveedor");
  const posted = controller.locator('[data-testid^="si-row-"]', { hasText: /Pendiente de pago|Pagada/ }).first();
  const ncf = ((await posted.getAttribute("data-testid")) ?? "").replace("si-row-", "");
  expect(ncf).not.toBe("");
  await controller.goto("/auditoria/asientos/");
  await expect(controller.getByRole("heading", { name: "Buscar asientos" })).toBeVisible();
  await controller.getByLabel("Documento o identificador").fill(ncf);
  await controller.getByRole("button", { name: "Buscar", exact: true }).click();
  const hit = controller.getByTestId("journal-hit").filter({ hasText: ncf }).first();
  await expect(hit).toBeVisible();
  await hit.getByRole("link", { name: "Ver asientos" }).click();
  await expect(controller.getByTestId("journals-title")).toContainText(ncf);
  await expect(controller.getByTestId("journal").first()).toBeVisible();

  const auditor = await signIn(browser, "Auditor");
  // UX4-02/03 (A-09): the menu and the screen read "Verificar integridad", with the last verification.
  await nav(auditor, "Verificar integridad");
  await expect(auditor.getByRole("heading", { name: "Verificar integridad" })).toBeVisible();
  await expect(auditor.getByTestId("last-verification")).toBeVisible();
  await expect(auditor.getByTestId("integrity-chains")).toContainText("Libro mayor");
  await auditor.getByRole("button", { name: "Verificar ahora" }).click();
  // The dev stack has no WORM storage: the screen says so instead of failing (E-UI01-2).
  await expect(auditor.getByText("no está disponible en este ambiente").or(auditor.getByTestId("chain-result"))).toBeVisible();
  await nav(auditor, "Respaldos diarios inalterables");
  await expect(auditor.getByRole("heading", { name: "Respaldos diarios inalterables" })).toBeVisible();
  await expect(auditor.getByRole("link", { name: "Usuarios y roles" })).toBeVisible();
});
