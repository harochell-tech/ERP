import { expect, test } from "@playwright/test";
import { expectFits, signIn } from "./support";

// NAV-01 (E-NAV-1…13): the menu follows the business; «Ir a…» finds a screen by typing (accents and case aside) and Enter opens it;
// a star keeps a screen under «Favoritos» in this browser, after a reload too. Runs on the desktop and on the phone.

test("the menu's groups, «Ir a…» and favourites (NAV-01)", async ({ browser }) => {
  const contador = await signIn(browser, "Contador");
  const toggle = contador.getByRole("button", { name: "Menú", exact: true });
  const menu = contador.getByRole("navigation", { name: "Menú principal" });
  const openMenu = async () => {
    if (await toggle.isVisible()) {
      await toggle.click();
      await expect(menu).toBeVisible();
    }
  };

  // E-NAV-1/9/11: Cierre lives in Contabilidad; Maestros, Cierre, Auditoría and Seguridad are gone.
  await openMenu();
  const groups = menu.locator(".menu-group-toggle");
  await expect(groups.filter({ hasText: "Maestros" })).toHaveCount(0);
  await expect(groups.filter({ hasText: /^Cierre/ })).toHaveCount(0);
  await menu.getByRole("button", { name: /Contabilidad/ }).click();
  await expect(menu.getByRole("link", { name: "Períodos y cierre", exact: true })).toBeVisible();

  // E-NAV-12: typing finds the screen; Enter opens the first match.
  const search = menu.getByRole("textbox", { name: "Ir a una pantalla" });
  await search.fill("BALANZA");
  await expect(menu.getByTestId("menu-search-results").getByRole("link")).toHaveCount(1);
  await search.press("Enter");
  await expect(contador).toHaveURL(/\/contabilidad\/balanza\/$/);
  await expect(contador.getByRole("heading", { name: "Balanza" }).first()).toBeVisible();
  await openMenu();
  await search.fill("zzz");
  await expect(menu.getByTestId("menu-search-results")).toContainText("Ninguna pantalla con «zzz».");
  await search.fill("");

  // E-NAV-13: the star keeps Mayor under Favoritos, after a reload too, and takes it away.
  await menu.getByRole("button", { name: /Contabilidad/ }).click();
  if (!(await menu.getByRole("link", { name: "Mayor", exact: true }).isVisible())) {
    await menu.getByRole("button", { name: /Contabilidad/ }).click();
  }
  const star = menu.getByRole("listitem").filter({ has: contador.getByRole("link", { name: "Mayor", exact: true }) }).getByRole("button", { name: "Favorito" });
  await expect(star).toHaveAttribute("aria-pressed", "false");
  await star.click();
  await expect(menu.getByTestId("menu-favorites").getByRole("link", { name: "Mayor", exact: true })).toBeVisible();
  await contador.reload();
  await expect(contador.getByTestId("user-email")).toBeAttached(); // the shell is back before the menu is opened
  await openMenu();
  await expect(menu.getByTestId("menu-favorites").getByRole("link", { name: "Mayor", exact: true })).toBeVisible();
  await menu.getByRole("listitem").filter({ has: contador.getByRole("link", { name: "Mayor", exact: true }) }).getByRole("button", { name: "Favorito" }).click();
  await expect(menu.getByTestId("menu-favorites")).toHaveCount(0);
  await expectFits(contador);
});
