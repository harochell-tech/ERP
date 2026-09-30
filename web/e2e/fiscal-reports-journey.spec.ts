import { readFile } from "node:fs/promises";
import { expect, test } from "@playwright/test";
import { nav, signIn } from "./support";

// FIS2-03 (E-FIS2-03-7): the Especialista fiscal opens Fiscal › Reportes fiscales, picks the current month (the dev seed posts a
// supplier invoice today; the purchase journey may add more), sees the 606 header and its records, downloads the CSV for the DGII
// tool, opens the IT-1 and IR-17 summaries, and finds the seeded 606 classification on Reglas fiscales. Totals change with the
// other journeys' data, so the journey asserts shapes, never exact amounts.


/** This month in the Dominican Republic, AAAAMM. */
function currentPeriod(): string {
  const today = new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
  return today.slice(0, 7).replace("-", "");
}

test("the 606, its CSV for the DGII tool and the IT-1 / IR-17 summaries", async ({ browser }) => {
  const period = currentPeriod();
  const specialist = await signIn(browser, "Especialista fiscal");
  await nav(specialist, "Reportes fiscales");
  await expect(specialist.getByRole("heading", { name: "Reportes fiscales" })).toBeVisible();
  // UX4-02 (G-24): the period is a month picker (yyyy-MM); the report still shows AAAAMM.
  await expect(specialist.getByLabel("Período", { exact: true })).toHaveValue(/^\d{4}-\d{2}$/);
  await expect(specialist.getByText("Solo incluye las compras registradas en el sistema")).toBeVisible();

  await specialist.getByLabel("Período", { exact: true }).fill(`${period.slice(0, 4)}-${period.slice(4)}`);
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
  await specialist.getByLabel("Período", { exact: true }).fill(`${period.slice(0, 4)}-${period.slice(4)}`);

  await specialist.getByRole("tab", { name: "IT-1" }).click();
  await expect(specialist.getByText("Informativo: no es el formulario oficial de la DGII.")).toBeVisible();
  await expect(specialist.getByTestId("it1-purchase-itbis")).toHaveText(/^[\d,]+\.\d{2}$/);
  await expect(specialist.getByTestId("it1-purchase-itbis")).not.toHaveText("0.00");

  await specialist.getByRole("tab", { name: "IR-17" }).click();
  await expect(specialist.getByText("Informativo: no es el formulario oficial de la DGII.")).toBeVisible();
  await expect(specialist.getByTestId("ir17-itbis")).toHaveText(/^[\d,]+\.\d{2}$/);
  await expect(specialist.getByTestId("ir17-isr")).toHaveText(/^[\d,]+\.\d{2}$/);

  await nav(specialist, "Reglas fiscales");
  await expect(specialist.getByRole("heading", { name: /Clasificación del 606/ })).toBeVisible();
});
