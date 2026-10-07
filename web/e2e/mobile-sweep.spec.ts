import { expect, test, type Page } from "@playwright/test";
import { signIn } from "./support";

// UX6-01 (E-UX6-7): every page of the menu, for the accounts that between them see all of it, at 360, 390 and 430 px: nothing wider than
// the screen, every button and field at least 40 px high, and the fields at 16 px (no zoom on focus). Runs on the phone project only.

const ACCOUNTS = [
  "Controller",
  "Contador",
  "Vendedor",
  "Facturación",
  "Despacho",
  "Cobros",
  "Caja",
  "Comprador",
  "Cuentas por pagar",
  "Tesorero",
  "Almacenista",
  "Supervisor de producción",
  "Especialista fiscal",
  "Auditor",
  "Administrador de seguridad",
];
const WIDTHS = [360, 390, 430];

async function menuLinks(page: Page): Promise<string[]> {
  return page.evaluate(() =>
    [...document.querySelectorAll<HTMLAnchorElement>('nav[aria-label="Menú principal"] a[href^="/"]')].map((a) => a.getAttribute("href") ?? "/"),
  );
}

async function problems(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const found: string[] = [];
    const width = window.innerWidth;
    if (document.documentElement.scrollWidth > width) {
      found.push(`page ${document.documentElement.scrollWidth}px wide`);
    }
    const main = document.querySelector("main") ?? document.body;
    for (const el of main.querySelectorAll<HTMLElement>("button, .button, input:not([type=checkbox]):not([type=radio]):not([type=file]), select, textarea")) {
      const box = el.getBoundingClientRect();
      if (box.width === 0 || box.height === 0 || el.closest("[hidden], dialog:not([open])")) continue;
      const name = (el.getAttribute("aria-label") ?? el.textContent ?? el.tagName).trim().slice(0, 40);
      if (box.height < 39.5 && !el.classList.contains("link") && !el.classList.contains("search-select-clear"))
        found.push(`«${name}» ${Math.round(box.height)}px high`);
      // Inside a box that scrolls sideways (a wide table), the reader scrolls to it: only the page itself must fit.
      let scroller = false;
      for (let p = el.parentElement; p && !scroller; p = p.parentElement) {
        scroller = ["auto", "scroll"].includes(getComputedStyle(p).overflowX) && p.scrollWidth > p.clientWidth;
      }
      if (box.right > width + 0.5 && !scroller) found.push(`«${name}» runs off the screen`);
      if (["INPUT", "SELECT", "TEXTAREA"].includes(el.tagName) && parseFloat(getComputedStyle(el).fontSize) < 16)
        found.push(`«${name}» text ${getComputedStyle(el).fontSize}`);
    }
    return [...new Set(found)];
  });
}

test("every page fits a phone (E-UX6-7)", async ({ browser }) => {
  test.setTimeout(900_000);
  const report: string[] = [];
  const seen = new Set<string>();
  for (const account of ACCOUNTS) {
    const page = await signIn(browser, account);
    const links = (await menuLinks(page)).filter((href) => !seen.has(href));
    for (const href of links) {
      seen.add(href);
      for (const width of WIDTHS) {
        await page.setViewportSize({ width, height: 844 });
        await page.goto(href);
        await page.waitForLoadState("networkidle");
        for (const p of await problems(page)) report.push(`${href} @${width}: ${p}`);
      }
    }
    await page.context().close();
  }
  expect(seen.size).toBeGreaterThan(40);
  expect(report, report.join("\n")).toEqual([]);
});
