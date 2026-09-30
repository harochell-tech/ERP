import { expect, type Browser, type Locator, type Page } from "@playwright/test";

// UX1-01b (E-UX1-01-11): what the journeys share. The same journeys run on a desktop and on a 390 px phone (project "mobile"):
// the menu is reached through "☰ Menú" on the phone, irreversible actions are confirmed in a dialog, and every page is checked
// for sideways overflow and for a reachable primary button.

/** Signs in through the (simulated) Google sign-in in a new browser context of the running project. */
export async function signIn(browser: Browser, account: string): Promise<Page> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto("/");
  await page.getByRole("link", { name: "Entrar con Google" }).click();
  await page.getByRole("link", { name: account, exact: true }).click();
  // On a phone the user block lives in the (closed) menu panel: attached, not visible.
  await expect(page.getByTestId("user-email")).toBeAttached();
  await expectFits(page);
  return page;
}

/** Clicks a main-menu item, opening the menu panel first on a phone. */
export async function nav(page: Page, label: string): Promise<void> {
  const toggle = page.getByRole("button", { name: "Menú", exact: true });
  if (await toggle.isVisible()) {
    await toggle.click();
    await expect(page.getByRole("navigation", { name: "Menú principal" })).toBeVisible();
  }
  await page.getByRole("navigation", { name: "Menú principal" }).getByRole("link", { name: label, exact: true }).click();
  await expectFits(page);
}

/** Presses an action guarded by the confirmation dialog (E-UX1-01-8) and confirms it. */
export async function confirmAction(page: Page | Locator, label: string, options: { exact?: boolean } = {}): Promise<void> {
  const root: Page = typeof (page as Locator).page === "function" ? (page as Locator).page() : (page as Page);
  await page.getByRole("button", { name: label, exact: options.exact ?? true }).click();
  const dialog = root.getByRole("dialog");
  await expect(dialog).toBeVisible();
  await dialog.getByRole("button", { name: `Confirmar: ${label}` }).click();
  await expect(dialog).toHaveCount(0);
}

/** Presses a form's primary button after checking that it can be reached and tapped (E-UX1-01-11). */
export async function submit(page: Page, name: string | RegExp): Promise<void> {
  const button = page.getByRole("button", { name, exact: typeof name === "string" });
  await expectClickable(button);
  await expectFits(page);
  await button.click();
}

/** Fails when the page scrolls sideways: the document may never be wider than the viewport. */
export async function expectFits(page: Page): Promise<void> {
  await page.waitForLoadState("domcontentloaded");
  const size = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, viewport: window.innerWidth, path: location.pathname }));
  expect(size.scroll, `${size.path} is ${size.scroll}px wide on a ${size.viewport}px screen`).toBeLessThanOrEqual(size.viewport);
}

/** Fails when the button cannot be tapped: hidden, disabled after the form is filled, or covered by another element. */
export async function expectClickable(button: Locator): Promise<void> {
  await button.scrollIntoViewIfNeeded();
  await expect(button).toBeVisible();
  await button.click({ trial: true, timeout: 10_000 });
}
