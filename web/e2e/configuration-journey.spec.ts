import { expect, test, type Browser, type Page } from "@playwright/test";

// E-B03-15-4: the Controller prepares a new version of the POSTING policy and the policy approver (someone else) approves it,
// both through the configuration screens.

async function signIn(browser: Browser, account: string): Promise<Page> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto("/");
  await page.getByRole("link", { name: "Iniciar sesión" }).click();
  await page.getByRole("link", { name: account, exact: true }).click();
  await expect(page.getByTestId("user-email")).toBeVisible();
  return page;
}

const posting = (page: Page) => page.locator("section").filter({ has: page.getByRole("heading", { name: /^POSTING/ }) });

test("an accounting policy version is prepared and approved by two people", async ({ browser }) => {
  const nextYear = `${new Date().getFullYear() + 1}-01-01`;

  const controller = await signIn(browser, "Controller");
  await controller.getByRole("link", { name: "Políticas" }).click();
  await posting(controller).getByRole("button", { name: "Preparar nueva versión" }).click();
  await posting(controller).getByLabel(/late_entry_hours/).fill("48");
  await posting(controller).getByLabel(/rounding_difference_tolerance/).fill("0.05");
  await posting(controller).getByLabel("Vigente desde").fill(nextYear);
  await posting(controller).getByLabel("Justificación").fill("Revisión anual de la tolerancia de redondeo");
  await posting(controller).getByRole("button", { name: "Guardar borrador" }).click();
  await expect(posting(controller).getByRole("cell", { name: "Borrador" })).toBeVisible();
  // The preparer is not offered the approval.
  await expect(posting(controller).getByRole("button", { name: "Aprobar" })).toHaveCount(0);

  const approver = await signIn(browser, "Aprobador de políticas contables");
  await approver.getByRole("link", { name: "Políticas" }).click();
  await posting(approver).getByRole("button", { name: "Aprobar" }).click();
  await expect(posting(approver).getByRole("cell", { name: "Borrador" })).toHaveCount(0);
  await expect(posting(approver).getByRole("cell", { name: "Activo" })).toHaveCount(1);
});
