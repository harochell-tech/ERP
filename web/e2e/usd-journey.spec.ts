import { expect, test } from "@playwright/test";
import { confirmAction, expectFits, nav, pick, pickFirst, signIn, submit } from "./support";

// USD1-07b (E2E-U1 screens, E-USD1-07-1…7): an import through the UI. Tesorería enters today's rate (60.00) and the Controller approves it
// from Inicio; the Comprador registers a foreign supplier and the Controller activates it. Cuentas por pagar registers its invoice of a
// used forklift for USD 8,000.00 — no NCF or tax type, the server shows the rate and 480,000.00 — which the Controller approves over the
// amount and Cuentas por pagar posts; the DUA brings 30,000.00 of duties; the settlement adds them to the forklift (510,000.00) once the
// Controller approves it. The payment with its exchange difference and the revaluation run over the API (E2E-U1).

function today(): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
}

test("rate, foreign supplier, USD invoice, DUA and import settlement (E2E-U1)", async ({ browser }) => {
  test.setTimeout(300_000);
  const stamp = String(Date.now()).slice(-6);
  const supplier = `Forklift Parts ${stamp}`;
  const invoiceNo = `INV-${stamp}`;
  const duaNo = `10020-IM-${stamp}`;

  // Tesorería enters today's Banco Central rate; the Controller sees it waiting on Inicio and approves it.
  const treasurer = await signIn(browser, "Tesorero");
  await nav(treasurer, "Tasas de cambio");
  await treasurer.getByLabel("Tasa", { exact: true }).fill("60.0000");
  await submit(treasurer, "Registrar tasa");
  await expect(treasurer.getByTestId(`rate:${today()}:60.0000`)).toContainText("Por aprobar");
  const controller = await signIn(browser, "Controller");
  await expect(controller.getByTestId("tasks").getByRole("link", { name: /Tasas de cambio por aprobar/ })).toBeVisible();
  await nav(controller, "Tasas de cambio");
  await confirmAction(controller.getByTestId(`rate:${today()}:60.0000`), "Aprobar");
  await expect(controller.getByTestId(`rate:${today()}:60.0000`)).toContainText("Vigente");

  // The Comprador registers the foreign supplier; the Controller activates it.
  const buyer = await signIn(browser, "Comprador");
  await nav(buyer, "Proveedores");
  await buyer.getByRole("button", { name: "Nuevo proveedor del exterior" }).click();
  const form = buyer.getByTestId("foreign-supplier-form");
  await form.getByLabel("Razón social").fill(supplier);
  await form.getByLabel("País (código de 2 letras)").fill("US");
  await form.getByLabel("Identificación fiscal del exterior (opcional)").fill(`EIN ${stamp}`);
  await submit(buyer, "Crear proveedor del exterior");
  await expect(buyer.getByRole("row").filter({ hasText: supplier })).toContainText(`US · EIN ${stamp}`);
  await nav(controller, "Proveedores");
  await controller.getByRole("searchbox").fill(supplier);
  await controller.getByRole("row").filter({ hasText: supplier }).getByRole("button", { name: "Activar" }).click();
  await expect(controller.getByRole("row").filter({ hasText: supplier })).toContainText("Activo");

  // Cuentas por pagar: the invoice in USD — the supplier's own number, no tax type, the server's rate and pesos.
  const payables = await signIn(browser, "Cuentas por pagar");
  await payables.goto("/cxp/facturas/gasto/");
  await expect(payables.getByRole("heading", { name: "Registrar factura de gastos" })).toBeVisible();
  await pick(payables.getByLabel("Proveedor"), supplier);
  await expect(payables.getByTestId("foreign-invoice-note")).toBeVisible();
  await expect(payables.getByLabel("Tipo de impuesto 1")).toHaveCount(0);
  await payables.getByLabel("Planta").selectOption({ index: 1 });
  await payables.getByLabel("Número de la factura del proveedor").fill(invoiceNo);
  await payables.getByLabel("Vence").fill(today());
  await payables.getByLabel("Descripción 1").fill("Montacargas usado Toyota 8FGU25");
  await pick(payables.getByLabel("Categoría 1"), "Montacargas y equipos");
  await payables.getByLabel("Cantidad 1").fill("1");
  await payables.getByLabel("Precio 1").fill("8000");
  await expect(payables.getByTestId("expense-preview-net")).toHaveText("8,000.00");
  await expect(payables.getByTestId("expense-preview-rate")).toContainText("60.0000");
  await expect(payables.getByTestId("expense-preview-dop")).toHaveText("480,000.00");
  await expectFits(payables);
  await submit(payables, "Registrar y cotejar");
  await expect(payables).toHaveURL(/\/cxp\/factura\/\?id=/);
  await expect(payables.getByTestId("si-status")).toHaveText("Pendiente de aprobación");
  await expect(payables.getByTestId("si-rate")).toHaveText("60.0000");
  const invoiceUrl = payables.url();

  // The Controller approves it over the amount; Cuentas por pagar posts it (P-38).
  await controller.goto(invoiceUrl);
  await controller.getByRole("button", { name: "Aprobar excepción" }).click();
  const dialog = controller.getByRole("dialog");
  await dialog.getByLabel("Motivo: Aprobar excepción").fill("Montacargas importado");
  await dialog.getByRole("button", { name: "Confirmar: Aprobar excepción" }).click();
  await expect(controller.getByTestId("si-status")).toHaveText("Cotejada, lista para contabilizar");
  await payables.reload();
  await confirmAction(payables, "Contabilizar");
  await expect(payables.getByTestId("accounting-status")).toContainText("Contabilizado");
  await expect(payables.getByTestId("si-usd")).toContainText("8,000.00");

  // The DUA: 30,000.00 of duties and 86,400.00 of ITBIS, owed to the DGA (a local supplier).
  await nav(payables, "DUA (aduana)");
  await payables.getByRole("button", { name: "Registrar DUA" }).click();
  const dua = payables.getByTestId("dua-form");
  await pickFirst(dua.getByLabel("DGA"));
  await dua.getByLabel("Planta").selectOption({ index: 1 });
  await dua.getByLabel("Número del DUA").fill(duaNo);
  await dua.getByLabel("Valor CIF").fill("480000");
  await dua.getByLabel("Aranceles").fill("30000");
  await dua.getByLabel("ITBIS de aduana").fill("86400");
  await submit(payables, "Registrar DUA");
  await expect(payables.getByTestId(`dua:${duaNo}`)).toContainText("Sin liquidar");

  // The settlement: the invoice is the goods, the DUA the cost; the Controller approves it and the forklift costs 510,000.00.
  await nav(payables, "Liquidaciones de importación");
  await payables.getByRole("button", { name: "Nueva liquidación" }).click();
  const settlement = payables.getByTestId("settlement-form");
  await settlement.getByLabel("Planta").selectOption({ index: 1 });
  await settlement.getByLabel(`Mercancía ${invoiceNo}`).check();
  await settlement.getByLabel(`DUA ${duaNo}`).check();
  await submit(payables, "Preparar y ver el reparto");
  await expect(payables).toHaveURL(/\/compras\/liquidacion\/\?id=/);
  await expect(payables.getByTestId("allocation:Montacargas usado Toyota 8FGU25")).toContainText("510,000.00");
  await expect(payables.getByRole("button", { name: "Aprobar y contabilizar" })).toHaveCount(0);
  await controller.goto(payables.url());
  await confirmAction(controller, "Aprobar y contabilizar");
  await expect(controller.getByText("Contabilizada").first()).toBeVisible();
  await expect(controller.getByTestId("settlement-total")).toContainText("30,000.00");
  await expectFits(controller);

  // The close: the revaluation page lists the months revalued.
  const contador = await signIn(browser, "Contador");
  await nav(contador, "Revaluación de saldos en dólares");
  await expect(contador.getByRole("heading", { name: "Revaluación de saldos en dólares" })).toBeVisible();
  await expectFits(contador);
});
