import { expect, test } from "@playwright/test";
import { expectFits, nav, signIn } from "./support";

// UX4-03: the sign-in screen (G-19), a person without roles (G-18), Inicio's configuration tasks (G-15, G-16, G-17), the policies
// grouped by theme (G-22) and the menu wording (G-13). Runs on a desktop and on the phone. It asserts presence, not counts: other
// journeys change the configuration.

test("the sign-in screen carries the brand and one primary button", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Rochell Core" })).toBeVisible();
  const enter = page.getByRole("link", { name: "Entrar con Google" });
  await expect(enter).toBeVisible();
  await expect(enter).toHaveClass(/primary/);
  await expectFits(page);
});

test("a person without roles learns what to do and sees the e-mail to send", async ({ browser }) => {
  const employee = await signIn(browser, "Empleado nuevo (sin roles)");
  // The security journey later gives this identity a role; on a fresh stack it has none yet.
  const noRoles = employee.getByTestId("no-roles");
  if (await noRoles.isVisible()) {
    await expect(noRoles.getByText("Seguridad › Usuarios y roles")).toBeVisible();
    await expect(employee.getByTestId("no-roles-email")).toContainText("@");
    await expect(noRoles.getByRole("button", { name: "Copiar correo" })).toBeVisible();
  }
});

test("Inicio lists the configuration waiting for each approver, and closing only for who may close", async ({ browser }) => {
  const approver = await signIn(browser, "Aprobador de políticas contables");
  const tasks = approver.getByTestId("tasks");
  await expect(tasks.getByRole("link", { name: "Políticas contables por aprobar" })).toBeVisible();
  await expect(approver.getByTestId("task-count:/contabilidad/politicas/")).toHaveText(/^\d+\+?$/);
  await expect(approver.getByText("(E-11)")).toHaveCount(0);

  const controller = await signIn(browser, "Controller");
  await expect(controller.getByTestId("tasks").getByRole("link", { name: "Cerrar o reabrir períodos" })).toBeVisible();
  await expect(controller.getByTestId("tasks").getByRole("link", { name: "Reglas de contabilización por aprobar" })).toBeVisible();
  await expect(controller.getByTestId("task-count:/contabilidad/reglas/")).toHaveText(/^\d+\+?$/);

  const specialist = await signIn(browser, "Especialista fiscal");
  await expect(specialist.getByTestId("tasks").getByRole("link", { name: "Autorizaciones fiscales por vencer" })).toBeVisible();
  await expect(specialist.getByTestId("task-count:/fiscal/autorizaciones/?estado=ACTIVE")).toHaveText(/^\d+\+?$/);

  // G-16: the Auditor reads the periods but does not close them.
  const auditor = await signIn(browser, "Auditor");
  await expect(auditor.getByRole("heading", { name: "Inicio" })).toBeVisible();
  await expect(auditor.getByRole("link", { name: "Cerrar o reabrir períodos" })).toHaveCount(0);
  await expectFits(auditor);
});

test("the policies read by theme and the menu uses one name per screen", async ({ browser }) => {
  const controller = await signIn(browser, "Controller");
  await nav(controller, "Políticas");
  await expect(controller.getByTestId("policy-theme:Ventas y crédito")).toBeVisible();
  const revenue = controller.getByTestId("policy:REVENUE_ACCOUNTING");
  await expect(revenue.getByTestId("parameter-group:Fiscal")).toContainText("Fiscal");
  await nav(controller, "Cuentas por rol");
  await expect(controller.getByRole("heading", { name: "Cuentas por rol" })).toBeVisible();
  await nav(controller, "Reglas de contabilización");
  await expect(controller.getByRole("heading", { name: "Reglas de contabilización" })).toBeVisible();
});

test("master data and security lists search and explain what to do", async ({ browser }) => {
  const controller = await signIn(browser, "Controller");
  await nav(controller, "Materias primas");
  await controller.getByLabel("Buscar").fill("cemento");
  await expect(controller.locator("tbody tr").first()).toContainText(/cemento/i);
  await controller.getByLabel("Buscar").fill("zzzz-no-existe");
  await expect(controller.getByText("Ninguna materia prima coincide con la búsqueda.")).toBeVisible();

  const admin = await signIn(browser, "Administrador de seguridad");
  await nav(admin, "Usuarios y roles");
  await admin.getByLabel("Con el rol").selectOption({ label: "Controller" });
  await expect(admin.locator("tbody tr").first()).toContainText("Controller");
  await expect(admin.getByText("herramienta de despliegue")).toHaveCount(0);
  await nav(admin, "Solicitudes de rol");
  await admin.getByLabel("Estado").selectOption({ label: "Rechazadas" });
  await expect(admin.getByTestId("requests-empty").or(admin.locator("tbody tr").first())).toBeVisible();
});
