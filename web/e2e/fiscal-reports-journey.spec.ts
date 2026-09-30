import { readFile } from "node:fs/promises";
import { expect, test, type Browser, type Page } from "@playwright/test";

// FIS2-03 (E-FIS2-03-7): the Especialista fiscal opens Fiscal › Reportes fiscales, picks the current month (the dev seed posts a
// supplier invoice today; the purchase journey may add more), sees the 606 header and its records, downloads the CSV for the DGII
// tool, opens the IT-1 and IR-17 summaries, and finds the seeded 606 classification on Reglas fiscales. Totals change with the
// other journeys' data, so the journey asserts shapes, never exact amounts.

async function signIn(browser: Browser, account: string): Promise<Page> {
  const context = await browser.newContext({ acceptDownloads: true });
  const page = await context.newPage();
  await page.goto("/");
  await page.getByRole("link", { name: "Iniciar sesión" }).click();
  await page.getByRole("link", { name: account, exact: true }).click();
  await expect(page.getByTestId("user-email")).toBeVisible();
  return page;
}

/** This month in the Dominican Republic, AAAAMM. */
function currentPeriod(): string {
  const today = new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
  return today.slice(0, 7).replace("-", "");
}

test("the 606, its CSV for the DGII tool and the IT-1 / IR-17 summaries", async ({ browser }) => {
  const period = currentPeriod();
  const specialist = await signIn(browser, "Especialista fiscal");
  await specialist.getByRole("link", { name: "Reportes fiscales" }).click();
  await expect(specialist.getByRole("heading", { name: "Reportes fiscales" })).toBeVisible();
  await expect(specialist.getByLabel("Período (AAAAMM)")).toHaveValue(/^\d{6}$/);
  await expect(specialist.getByText("Solo incluye las compras registradas en el sistema")).toBeVisible();

  await specialist.getByLabel("Período (AAAAMM)").fill(period);
  await expect(specialist.getByTestId("report-606-period")).toHaveText(period);
  await expect(specialist.getByTestId("report-606-rnc")).not.toHaveText("—"); // the dev company's RNC is a test fixture value
  await expect(specialist.getByTestId("report-606-count")).not.toHaveText("0");
  await expect(specialist.getByTestId("report-606-total")).toHaveText(/^[\d,]+\.\d{2}$/);
  const rows = specialist.getByTestId("report-606-row");
  await expect(rows.first()).toBeVisible();
  await expect(rows.filter({ hasText: "09 — Compras y gastos que formarán parte del costo de venta" }).first()).toBeVisible();
  const records = Number(await specialist.getByTestId("report-606-count").textContent());
  await expect(rows).toHaveCount(records);

  const download = specialist.waitForEvent("download");
  await specialist.getByRole("link", { name: "Descargar CSV para la herramienta DGII" }).click();
  const file = await download;
  expect(file.suggestedFilename()).toBe(`606-${period}.csv`);
  const csv = await readFile((await file.path())!, "utf8");
  const lines = csv.split(/\r?\n/).filter((l) => l.trim() !== "");
  expect(lines).toHaveLength(records);
  expect(lines[0]!.split(",")).toHaveLength(23);
  await expect(specialist.getByText("Pegue las filas del CSV y pulse Validar.")).toBeVisible();

  // The NCF opens the supplier invoice.
  await rows.first().getByRole("link").click();
  await expect(specialist).toHaveURL(/\/cxp\/factura\/\?id=/);
  await specialist.goBack();
  await specialist.getByLabel("Período (AAAAMM)").fill(period);

  await specialist.getByRole("tab", { name: "IT-1" }).click();
  await expect(specialist.getByText("Informativo: no es el formulario oficial de la DGII.")).toBeVisible();
  await expect(specialist.getByTestId("it1-purchase-itbis")).toHaveText(/^[\d,]+\.\d{2}$/);
  await expect(specialist.getByTestId("it1-purchase-itbis")).not.toHaveText("0.00");

  await specialist.getByRole("tab", { name: "IR-17" }).click();
  await expect(specialist.getByText("Informativo: no es el formulario oficial de la DGII.")).toBeVisible();
  await expect(specialist.getByTestId("ir17-itbis")).toHaveText(/^[\d,]+\.\d{2}$/);
  await expect(specialist.getByTestId("ir17-isr")).toHaveText(/^[\d,]+\.\d{2}$/);

  await specialist.getByRole("link", { name: "Reglas fiscales" }).click();
  await expect(specialist.getByRole("heading", { name: /Clasificación del 606/ })).toBeVisible();
});
