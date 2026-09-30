import { expect, test, type Page } from "@playwright/test";
import { expectFits, nav, signIn, submit } from "./support";

// E-B03-15-4 / UX2-02 (E-UX2-1…12): the configuration screens on a desktop and on a phone. The Controller prepares a version of
// the PURCHASING policy typing a percentage and the policy approver (someone else) approves it seeing "en vigor → propuesta"; the
// Contador prepares an account role map and the Controller approves it; the Controller renames a plant and opens the Centro de
// configuración. Everything new starts next year, so the journeys that post today are not affected.

const purchasing = (page: Page) => page.getByTestId("policy:PURCHASING");
const nextYear = () => new Date().getFullYear() + 1;

test("a policy version typed in % is approved by a second person who sees the change", async ({ browser }) => {
  const controller = await signIn(browser, "Controller");
  await nav(controller, "Políticas");
  // The parameter reads by its name and unit; the version in force shows the fraction as a percentage (0.02 → 2 %).
  await expect(purchasing(controller).getByRole("row", { name: /Sobre-recepción permitida \(%\).*2 %/ })).toBeVisible();
  await purchasing(controller).getByRole("button", { name: "Preparar nueva versión" }).click();
  const tolerance = purchasing(controller).getByLabel("Sobre-recepción permitida (%)");
  await expect(tolerance).toHaveValue("2");
  await tolerance.fill("7.5");
  await purchasing(controller).getByLabel("Vigente desde").fill(`${nextYear()}-01-01`);
  await purchasing(controller).getByLabel("Justificación").fill("Revisión anual de la tolerancia de recepción");
  await submit(controller, "Guardar borrador");
  const card = purchasing(controller).locator("[data-testid^='approval:PURCHASING:']");
  await expect(card).toBeVisible();
  // The preparer is not offered the approval.
  await expect(card.getByRole("button", { name: "Aprobar" })).toHaveCount(0);

  const approver = await signIn(browser, "Aprobador de políticas contables");
  await nav(approver, "Políticas");
  const pending = purchasing(approver).locator("[data-testid^='approval:PURCHASING:']");
  const changed = pending.locator("tr[data-changed='true']");
  await expect(changed).toHaveCount(1);
  await expect(changed).toContainText("Sobre-recepción permitida");
  await expect(changed).toContainText("2 %");
  await expect(changed).toContainText("7.5 %");
  await pending.getByRole("button", { name: "Aprobar" }).click();
  const dialog = approver.getByRole("dialog");
  await expect(dialog.getByTestId("approval-diff").locator("tr[data-changed='true']")).toContainText("7.5 %");
  await expectFits(approver);
  await dialog.getByRole("button", { name: "Confirmar: Aprobar" }).click();
  await expect(dialog).toHaveCount(0);
  await expect(purchasing(approver).locator("[data-testid^='approval:PURCHASING:']")).toHaveCount(0);
  await expect(approver.getByTestId("toast").first()).toContainText("aprobada y activa");
});

test("an account role map is prepared by the Contador and approved by the Controller", async ({ browser }) => {
  const contador = await signIn(browser, "Contador");
  await nav(contador, "Cuentas por rol");
  // Roles read by their names everywhere.
  await expect(contador.getByRole("cell", { name: "Cargos y comisiones bancarias" }).first()).toBeVisible();
  await contador.getByLabel("Rol contable").selectOption({ label: "Cargos y comisiones bancarias" });
  await contador.getByLabel("Cuenta", { exact: true }).selectOption({ label: "6105 — Cargos bancarios" });
  await contador.getByLabel("Vigente desde").fill(`${nextYear()}-02-01`);
  await submit(contador, "Guardar borrador");
  await expect(contador.getByTestId("toast").first()).toContainText("guardado en borrador");
  const draft = (page: Page) => page.locator("tr", { hasText: "Cargos y comisiones bancarias" }).filter({ hasText: "Borrador" });
  await expect(draft(contador)).toHaveCount(1);
  await expect(draft(contador).getByRole("button", { name: "Aprobar" })).toHaveCount(0);

  const controller = await signIn(browser, "Controller");
  await nav(controller, "Cuentas por rol");
  await draft(controller).getByRole("button", { name: "Aprobar" }).click();
  const dialog = controller.getByRole("dialog");
  await expect(dialog).toContainText("Cargos y comisiones bancarias");
  await dialog.getByRole("button", { name: "Confirmar: Aprobar" }).click();
  await expect(dialog).toHaveCount(0);
  await expect(draft(controller)).toHaveCount(0);
  await expect(controller.getByTestId("toast").first()).toContainText("aprobado y activo");
});

test("the Analista fiscal configures a withholding with the guided form, which builds the server's JSON", async ({ browser }) => {
  const analyst = await signIn(browser, "Analista fiscal");
  await nav(analyst, "Reglas fiscales");
  await analyst.getByText("Configurar una versión").click();
  await analyst.getByLabel("Código de la regla").fill("RET_ISR_E2E");
  await analyst.getByLabel("Tipo", { exact: true }).selectOption({ label: "Retención en compras" });
  await analyst.getByLabel("Vigente desde").fill(`${nextYear()}-01-01`);
  await analyst.getByLabel("Código del impuesto").fill("RET_ISR");
  await analyst.getByLabel("Tasa (%)").fill("10");
  await analyst.getByLabel("Base de la retención").selectOption("NET");
  await analyst.getByLabel("Persona jurídica (RNC)").check();
  await analyst.getByLabel("Persona física (cédula)").uncheck();
  await analyst.getByLabel("Tipo de retención de ISR (606)").selectOption("2");
  await analyst.getByText("Ver JSON (avanzado)").click();
  await expect(analyst.getByLabel("Definición (JSON)")).toHaveValue(
    JSON.stringify({ tax_code: "RET_ISR", rate: "0.1", base: "NET", party_types: ["COMPANY"], isr_withholding_type: "2" }, null, 2),
  );
  await submit(analyst, "Configurar versión");
  await expect(analyst.getByTestId("toast").first()).toContainText("RET_ISR_E2E");
  const definition = analyst.getByTestId("definition:RET_ISR_E2E:1");
  await expect(definition).toContainText("RET_ISR al 10 %");
  await expect(definition).toContainText("Sobre el monto neto (retención de ISR)");
  await expect(definition).toContainText("Tipo de retención del 606: 2");
});

test("the Controller renames a plant and follows the setup in the Centro de configuración", async ({ browser }) => {
  const controller = await signIn(browser, "Controller");
  await nav(controller, "Empresa");
  await expect(controller.getByTestId("company-rnc")).not.toBeEmpty();
  await expect(controller.getByText("otro RNC es otra empresa")).toBeVisible();
  await controller.getByRole("button", { name: "Cambiar nombre" }).first().click();
  await controller.getByLabel(/^Nombre de la planta/).fill("Planta Higüey");
  await submit(controller, "Guardar nombre");
  const dialog = controller.getByRole("dialog");
  await expect(dialog).toContainText("Planta Higüey");
  await dialog.getByRole("button", { name: "Confirmar: Guardar nombre" }).click();
  await expect(controller.getByTestId(/^plant-name:/).first()).toHaveText("Planta Higüey");
  await expect(controller.getByTestId("toast").first()).toContainText("renombrada");

  await nav(controller, "Centro de configuración");
  await expect(controller.getByRole("heading", { name: "Centro de configuración" })).toBeVisible();
  await expect(controller.getByTestId("step:COMPANY")).toContainText("Listo");
  await expect(controller.locator("[data-testid^='step:']")).toHaveCount(19);
  await expect(controller.getByTestId("area:CONTABILIDAD")).toBeVisible();
  await expect(controller.getByTestId("step:POLICIES").getByRole("link")).toHaveAttribute("href", "/contabilidad/politicas/");
  const complete = await controller.getByTestId("setup-complete").count();

  // Inicio shows "Puesta en marcha" until every step is done.
  await controller.goto("/");
  if (complete === 0) {
    await expect(controller.getByTestId("setup-card")).toBeVisible();
    await expect(controller.getByTestId("setup-card-progress")).toContainText("de 19 pasos listos");
  } else {
    await expect(controller.getByTestId("setup-card")).toHaveCount(0);
  }
  await expectFits(controller);
});
