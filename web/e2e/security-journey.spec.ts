import { expect, test, type Browser, type Page } from "@playwright/test";

// UI-01 (E-UI01-8): the security administrator requests a role for a new employee, the second approver approves it, and the
// employee signs in to a menu that follows the new role; the read-only audit and master screens open for their readers.

async function signIn(browser: Browser, account: string): Promise<Page> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto("/");
  await page.getByRole("link", { name: "Iniciar sesión" }).click();
  await page.getByRole("link", { name: account, exact: true }).click();
  await expect(page.getByTestId("user-email")).toBeVisible();
  return page;
}

test("a role requested by the security admin is approved by a second person", async ({ browser }) => {
  const admin = await signIn(browser, "Administrador de seguridad");
  await admin.getByRole("link", { name: "Usuarios y roles" }).click();
  const newcomer = (await admin.locator("tr", { hasText: "Sin roles" }).locator("td").first().innerText()).trim();
  await admin.getByLabel("Usuario").selectOption({ label: newcomer });
  await admin.getByLabel("Rol").selectOption({ label: "Comprador" });
  await admin.getByRole("button", { name: "Solicitar", exact: true }).click();
  await expect(admin.getByText("Solicitud enviada")).toBeVisible();

  const approver = await signIn(browser, "Segundo aprobador de seguridad");
  await approver.getByRole("link", { name: "Solicitudes de rol", exact: true }).click();
  const row = approver.locator("tr", { hasText: newcomer });
  await row.getByRole("button", { name: "Aprobar" }).click();
  await expect(approver.getByText("No hay solicitudes.")).toBeVisible();

  const employee = await signIn(browser, "Empleado nuevo (sin roles)");
  await expect(employee.getByRole("link", { name: "Órdenes de compra" })).toBeVisible();
  await expect(employee.getByRole("link", { name: "Usuarios y roles" })).toHaveCount(0);
});

test("the read-only audit and master screens open for their readers", async ({ browser }) => {
  const controller = await signIn(browser, "Controller");
  await controller.getByRole("link", { name: "Plantas y ubicaciones" }).click();
  await expect(controller.getByRole("heading", { name: "Plantas y ubicaciones" })).toBeVisible();
  await expect(controller.locator("tbody tr").first()).toBeVisible();
  await controller.getByRole("link", { name: "Catálogo de cuentas" }).click();
  await expect(controller.getByRole("cell", { name: "1101" })).toBeVisible();

  const auditor = await signIn(browser, "Auditor");
  await auditor.getByRole("link", { name: "Verificar cadena" }).click();
  await auditor.getByRole("button", { name: "Verificar cadena" }).click();
  // The dev stack has no WORM storage: the screen says so instead of failing (E-UI01-2).
  await expect(auditor.getByText("no está disponible en este ambiente").or(auditor.getByTestId("chain-result"))).toBeVisible();
  await auditor.getByRole("link", { name: "Resúmenes en WORM" }).click();
  await expect(auditor.getByRole("heading", { name: "Resúmenes diarios en WORM" })).toBeVisible();
  await expect(auditor.getByRole("link", { name: "Usuarios y roles" })).toBeVisible();
});
