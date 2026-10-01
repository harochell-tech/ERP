import { expect, test, type Page } from "@playwright/test";
import { confirmAction, nav, signIn } from "./support";

// IMP-02 (E-IMP-1…11) through the UI: the Comprador reviews and imports a supplier file and the Controller activates the new
// drafts at once; the Vendedor imports a customer file, the Controller approves the terms of the new drafts and Crédito activates them. The files are
// synthetic (E-IMP-11), in the columns of the ADM Cloud export, as .csv.

const SUPPLIERS = [
  "Razón Social,Teléfono 1,Correo Electrónico,Término de Pago,ID Fiscal,Moneda",
  "Ferretería Importada Uno SRL,809-555-0141,ventas@importada-uno.test; cobros@importada-uno.test,30 días,1-30-77001-4,DOP",
  "Repuestos Importados Dos SRL,,,Contado,130770022,DOP",
  "Repuestos Importados Dos (repetido),,,,130770022,DOP",
  "Proveedor Extranjero LTD,,,,,USD",
].join("\r\n");

const CUSTOMERS = [
  "Razón Social,Correo Electrónico,ID Fiscal,Término de Pago,Forma de Pago,Límite de Crédito",
  "Constructora Importada Tres SRL,cxp@importada-tres.test; compras@importada-tres.test,130770031,30 días,Cheque,250000.00",
  "CLIENTE CONSUMIDOR FINAL,,,,Efectivo,",
].join("\r\n");

async function chooseFile(page: Page, name: string, content: string) {
  await page.getByRole("button", { name: "Importar desde ADM Cloud" }).click();
  await page.getByLabel("Archivo").setInputFiles({ name, mimeType: "text/csv", buffer: Buffer.from(content, "utf8") });
  await page.getByRole("button", { name: "Revisar archivo" }).click();
}

test("suppliers and customers are imported from a file and processed in batches", async ({ browser }) => {
  const buyer = await signIn(browser, "Comprador");
  await nav(buyer, "Proveedores");
  await chooseFile(buyer, "Proveedores.csv", SUPPLIERS);
  await expect(buyer.getByTestId("import-summary")).toContainText("Se cargarán 2 proveedores nuevos de 4 filas; 1 repetidos en el archivo, 1 no se pueden cargar.");
  await expect(buyer.getByTestId("import-summary")).toContainText("No se usan las columnas: Moneda.");
  const rows = buyer.getByTestId("import-rows");
  await expect(rows.getByRole("row", { name: /Proveedor Extranjero LTD/ })).toContainText("No tiene RNC ni cédula.");
  await expect(rows.getByRole("row", { name: /Ferretería Importada Uno SRL/ })).toContainText("ventas@importada-uno.test, cobros@importada-uno.test");
  await buyer.getByRole("button", { name: "Importar 2 proveedores" }).click();
  await expect(buyer.getByTestId("import-result")).toContainText("Se cargaron 2 proveedores nuevos de 4 filas");
  await buyer.getByRole("link", { name: "Ferretería Importada Uno SRL" }).click();
  await expect(buyer.getByTestId("supplier-contact")).toContainText("809-555-0141");
  await expect(buyer.getByTestId("supplier-contact")).toContainText("ventas@importada-uno.test, cobros@importada-uno.test");
  await expect(buyer.getByTestId("supplier-terms")).toHaveText("30 días");

  const controller = await signIn(browser, "Controller");
  await nav(controller, "Proveedores");
  await controller.getByLabel("Seleccionar Ferretería Importada Uno SRL").check();
  await controller.getByLabel("Seleccionar Repuestos Importados Dos SRL").check();
  await confirmAction(controller, "Activar seleccionados (2)");
  await expect(controller.getByRole("row", { name: /Ferretería Importada Uno SRL/ })).toContainText("Activo");
  await expect(controller.getByRole("row", { name: /Repuestos Importados Dos SRL/ })).toContainText("Activo");

  const seller = await signIn(browser, "Vendedor");
  await nav(seller, "Clientes");
  await chooseFile(seller, "Clientes.csv", CUSTOMERS);
  await expect(seller.getByTestId("import-summary")).toContainText("Se cargarán 1 clientes nuevos de 2 filas; 1 no se pueden cargar.");
  await seller.getByRole("button", { name: "Importar 1 clientes" }).click();
  await expect(seller.getByTestId("import-result")).toContainText("Se cargaron 1 clientes nuevos de 2 filas");

  await controller.goto("/ventas/clientes/");
  await controller.getByLabel("Seleccionar Constructora Importada Tres SRL").check();
  await confirmAction(controller, "Aprobar términos de los seleccionados (1)");
  await controller.getByRole("link", { name: "Constructora Importada Tres SRL" }).click();
  await expect(controller.getByRole("main")).toContainText("250,000.00");

  // E-IMP-01-6: with approved terms, Crédito activates the selected drafts.
  const credit = await signIn(browser, "Crédito");
  await nav(credit, "Clientes");
  await credit.getByLabel("Seleccionar Constructora Importada Tres SRL").check();
  await confirmAction(credit, "Activar seleccionados (1)");
  await expect(credit.getByRole("row", { name: /Constructora Importada Tres SRL/ })).toContainText("Activo");

  // E-IMP-6: the Vendedor sees and edits every e-mail of the customer, one per line.
  await seller.goto("/ventas/clientes/");
  await seller.getByRole("link", { name: "Constructora Importada Tres SRL" }).click();
  await expect(seller.getByLabel("Correos")).toHaveValue("cxp@importada-tres.test\ncompras@importada-tres.test");
});
