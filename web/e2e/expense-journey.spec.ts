import { expect, test } from "@playwright/test";
import { confirmAction, expectFits, nav, signIn, submit } from "./support";

// GAS1-07 (E2E-G1, E-GAS-07-1…8): an expense purchase through the UI. The Contador prepares a category; the Controller approves it
// from the list (never the Contador). Cuentas por pagar registers a telephone bill without an order — 30,000.00 of type
// Telecomunicaciones, priced by the server while it is typed: ITBIS 5,400.00, ISC 3,000.00 and CDT 600.00, 39,000.00 in all, over
// the 25,000.00 approval amount — so the Controller approves it, Cuentas por pagar posts it, and the month's 606 carries its
// selective tax and CDT in their fields. Paying it is the payment of any supplier invoice (treasury journey; E2E-G1 over the API).

function today(): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
}

test("expense category, telephone bill over the approval amount, posted and in the 606 (E2E-G1)", async ({ browser }) => {
  test.setTimeout(240_000);
  const stamp = String(Date.now());
  const categoryName = `Teléfono móvil ${stamp.slice(-6)}`;
  const ncf = `B01${stamp.slice(-8)}`;

  // The Contador prepares the category: its expense account, 606 type 02, a service.
  const contador = await signIn(browser, "Contador");
  await nav(contador, "Categorías de gasto");
  await contador.getByRole("button", { name: "Nueva categoría" }).click();
  await contador.getByLabel("Nombre de la categoría").fill(categoryName);
  await contador.getByLabel("Cuenta de gasto").selectOption({ label: "63300 Teléfono e internet" });
  await contador.getByLabel("Tipo en el 606").selectOption("02");
  await contador.getByLabel("Servicio o bien").selectOption("SERVICE");
  await submit(contador, "Preparar");
  const row = contador.getByRole("row").filter({ hasText: categoryName });
  await expect(row).toContainText("Por aprobar");
  await expect(contador.getByRole("button", { name: /^Aprobar seleccionadas/ })).toHaveCount(0);
  await expectFits(contador);

  // The Controller sees the categories waiting on Inicio and approves this one.
  const controller = await signIn(browser, "Controller");
  await expect(controller.getByTestId("tasks").getByRole("link", { name: /Categorías de gasto por aprobar/ })).toBeVisible();
  await expect(controller.getByTestId("task-count:/maestros/categorias-gasto/")).toHaveText(/^\d+\+?$/);
  await nav(controller, "Categorías de gasto");
  await controller.getByRole("checkbox", { name: `Seleccionar ${categoryName}` }).check();
  await confirmAction(controller, "Aprobar seleccionadas (1)");
  await expect(controller.getByRole("row").filter({ hasText: categoryName })).toContainText("Activa");

  // Cuentas por pagar: the bill without an order, priced by the server, registered and matched in one step.
  const payables = await signIn(browser, "Cuentas por pagar");
  await nav(payables, "Facturas de proveedor");
  await payables.goto("/cxp/facturas/nueva/");
  await payables.getByRole("link", { name: "Factura de gastos (sin artículo registrado)" }).click();
  await expect(payables.getByRole("heading", { name: "Registrar factura de gastos" })).toBeVisible();
  await payables.getByLabel("Proveedor").selectOption({ index: 1 });
  await payables.getByLabel("Planta").selectOption({ index: 1 });
  await payables.getByLabel("NCF").fill(ncf);
  await payables.getByLabel("Vence").fill(today());
  await payables.getByLabel("Descripción 1").fill("Factura de teléfono del mes");
  await payables.getByLabel("Categoría 1").selectOption({ label: categoryName });
  await payables.getByLabel("Tipo de impuesto 1").selectOption({ label: "Telecomunicaciones (ITBIS 18 % + ISC 10 % + CDT 2 %)" });
  await payables.getByLabel("Cantidad 1").fill("1");
  await payables.getByLabel("Precio 1").fill("30000");
  await expect(payables.getByTestId("expense-preview-net")).toHaveText("30,000.00");
  await expect(payables.getByTestId("expense-preview-taxes")).toHaveText("9,000.00");
  await expect(payables.getByTestId("expense-preview-total")).toHaveText("39,000.00");
  await expectFits(payables);
  await submit(payables, "Registrar y cotejar");
  await expect(payables).toHaveURL(/\/cxp\/factura\/\?id=/);
  await expect(payables.getByTestId("si-status")).toHaveText("Pendiente de aprobación");
  await expect(payables.getByText(`Factura de teléfono del mes (${categoryName} · TELECOM)`)).toBeVisible();
  await expect(payables.getByRole("button", { name: "Aprobar excepción" })).toHaveCount(0);
  const invoiceUrl = payables.url();

  // The Controller approves it over the amount; Cuentas por pagar posts it with P-37.
  await controller.goto(invoiceUrl);
  await controller.getByRole("button", { name: "Aprobar excepción" }).click();
  const dialog = controller.getByRole("dialog");
  await expect(dialog).toContainText("supera el monto de aprobación");
  await dialog.getByLabel("Motivo: Aprobar excepción").fill("Factura de teléfono del mes");
  await dialog.getByRole("button", { name: "Confirmar: Aprobar excepción" }).click();
  await expect(controller.getByTestId("si-status")).toHaveText("Cotejada, lista para contabilizar");
  await payables.reload();
  await confirmAction(payables, "Contabilizar");
  await expect(payables.getByTestId("accounting-status")).toContainText("Contabilizado");
  await expect(payables.getByTestId("si-gross")).toContainText("39,000.00");
  await expect(payables.getByText("Selectivo al consumo (gasto)")).toBeVisible();
  await expect(payables.getByText("Otros impuestos y tasas (gasto)")).toBeVisible();

  // The month's 606: type 02, all of it services, ITBIS 5,400.00, ISC 3,000.00 in field 20 and CDT 600.00 in field 21.
  await nav(contador, "Reportes fiscales");
  await contador.getByLabel("Período", { exact: true }).fill(today().slice(0, 7));
  const record = contador.getByTestId("report-606-row").filter({ hasText: ncf });
  await expect(record).toContainText("02 — Gastos por trabajos, suministros y servicios");
  for (const amount of ["30,000.00", "5,400.00", "3,000.00", "600.00"]) {
    await expect(record).toContainText(amount);
  }
  await expectFits(contador);
});

test("expense purchase order approved and billed in part by an expense invoice (E2E-G1, E-GAS-07-3)", async ({ browser }) => {
  test.setTimeout(240_000);
  const ncf = `B01${String(Date.now()).slice(-8)}`;

  // The buyer orders 10 repairs at 1,000.00 with ITBIS 18 %, priced by the server, and sends the order to approval.
  const buyer = await signIn(browser, "Comprador");
  await buyer.goto("/compras/ordenes/nueva/");
  await buyer.getByRole("link", { name: "Orden de gastos (servicios y suministros)" }).click();
  await expect(buyer.getByRole("heading", { name: "Nueva orden de compra de gastos" })).toBeVisible();
  await buyer.getByLabel("Proveedor").selectOption({ index: 1 });
  await buyer.getByLabel("Descripción 1").fill("Mantenimiento de la mezcladora");
  await buyer.getByLabel("Categoría 1").selectOption({ label: "Reparaciones" });
  await buyer.getByLabel("Tipo de impuesto 1").selectOption({ label: "ITBIS 18 %" });
  await buyer.getByLabel("Cantidad 1").fill("10");
  await buyer.getByLabel("Precio 1").fill("1,000.00");
  await expect(buyer.getByTestId("expense-preview-total")).toHaveText("11,800.00");
  await expectFits(buyer);
  await submit(buyer, "Guardar y enviar");
  await expect(buyer.getByTestId("po-status")).toHaveText("Pendiente de aprobación");
  await expect(buyer.getByText("Mantenimiento de la mezcladora (Reparaciones · ITBIS_18)")).toBeVisible();
  const poNo = ((await buyer.getByRole("heading", { level: 1 }).innerText()).match(/OC-[0-9A-Z-]+/) ?? [""])[0];
  expect(poNo).not.toBe("");

  const approver = await signIn(browser, "Aprobador de compras");
  await approver.goto(buyer.url());
  await approver.getByRole("button", { name: "Aprobar" }).click();
  await expect(approver.getByTestId("po-status")).toHaveText("Aprobado");

  // Cuentas por pagar bills 6 of the 10 against the order: category and tax type are the order's, no approval amount applies.
  const payables = await signIn(browser, "Cuentas por pagar");
  await payables.goto("/cxp/facturas/gasto/");
  await payables.getByLabel("Proveedor").selectOption({ index: 1 });
  await payables.getByLabel("Orden de compra de gastos").selectOption({ label: poNo });
  await expect(payables.getByLabel("Categoría 1")).toBeDisabled();
  await expect(payables.getByLabel("Cantidad 1")).toHaveValue("10");
  await payables.getByLabel("Cantidad 1").fill("6");
  await payables.getByLabel("NCF").fill(ncf);
  await payables.getByLabel("Vence").fill(new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo" }).format(new Date()));
  await expect(payables.getByTestId("expense-preview-total")).toHaveText("7,080.00");
  await submit(payables, "Registrar y cotejar");
  await expect(payables.getByTestId("si-status")).toHaveText("Cotejada, lista para contabilizar");
  await confirmAction(payables, "Contabilizar");
  await expect(payables.getByTestId("accounting-status")).toContainText("Contabilizado");
  await expect(payables.getByText(poNo)).toBeVisible();
});
