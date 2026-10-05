import { expect, test, type Page } from "@playwright/test";
import { confirmAction, expectFits, nav, signIn, submit } from "./support";

// PRS-05 (E2E-PR1, E-PRS-05-1…7): a customer's own price list with freight, through the UI. The Controller creates «Resorts» —
// BLOQUE-6 at 44.00 and its freight to Bávaro at 3.50 per block — and the Aprobador de políticas approves it; Crédito moves «Hotel
// Playa Bávaro» to it and the Controller approves the terms; the Vendedor orders 100 blocks with our truck to Bávaro (4,400.00 +
// freight 350.00 + ITBIS 792.00 = 5,542.00); Despacho delivers them with the POD; Facturación invoices the delivery with the freight
// line exempt (5,542.00).

function dominicanNow(offsetMinutes = 0): string {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: "America/Santo_Domingo",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hourCycle: "h23",
  }).formatToParts(new Date(Date.now() + offsetMinutes * 60_000));
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  return `${get("year")}-${get("month")}-${get("day")}T${get("hour")}:${get("minute")}`;
}

async function attachEvidence(page: Page, label: string, name: string) {
  await page.getByLabel(label, { exact: true }).setInputFiles({ name, mimeType: "application/octet-stream", buffer: Buffer.from(`evidencia ${name}`) });
  await expect(page.getByTestId("evidence-verified")).toHaveText(`✓ Huella del archivo verificada: ${name}`);
}

test("customer price list with freight, from the list to the invoice (E2E-PR1)", async ({ browser }) => {
  test.setTimeout(300_000);

  // The Controller creates «Resorts» and prepares its first version: a product price and a freight to Bávaro.
  const controller = await signIn(browser, "Controller");
  await nav(controller, "Listas de precios");
  await expect(controller.getByTestId("price-list:GENERAL")).toBeVisible();
  await controller.getByRole("button", { name: "Nueva lista" }).click();
  await controller.getByLabel("Código de la lista").fill("RESORTS");
  await controller.getByLabel("Nombre de la lista").fill("Resorts");
  await submit(controller, "Crear lista");
  await expect(controller.getByRole("heading", { name: "«Resorts»: precios vigentes" })).toBeVisible();
  await controller.getByRole("button", { name: "Preparar nueva versión" }).click();
  await controller.getByRole("button", { name: "Agregar producto" }).click();
  await controller.getByLabel("Producto 1", { exact: true }).selectOption({ label: "BLOQUE-6 — Bloque de 6 pulgadas" });
  await controller.getByLabel("Precio 1").fill("44.00");
  await controller.getByRole("button", { name: "Agregar flete" }).click();
  await controller.getByLabel("Producto del flete 1").selectOption({ label: "BLOQUE-6 — Bloque de 6 pulgadas" });
  await controller.getByLabel("Zona del flete 1").selectOption({ label: "Bávaro" });
  await controller.getByLabel("Flete 1", { exact: true }).fill("3.50");
  await expectFits(controller);
  await submit(controller, "Preparar versión");
  await expect(controller.getByTestId("price-list:RESORTS")).toContainText("Con versión por aprobar");

  const approver = await signIn(browser, "Aprobador de políticas contables");
  await nav(approver, "Listas de precios");
  await approver.getByTestId("price-list:RESORTS").getByRole("button", { name: "Resorts" }).click();
  await confirmAction(approver, "Aprobar");
  await expect(approver.getByTestId("current-prices")).toContainText("44.00");
  await expect(approver.getByTestId("current-prices-freight")).toContainText("Bávaro");

  // Crédito moves the hotel to «Resorts»; the Controller approves the terms.
  const credit = await signIn(browser, "Crédito");
  await nav(credit, "Clientes");
  await credit.getByRole("link", { name: "Hotel Playa Bávaro" }).click();
  await expect(credit).toHaveURL(/\/ventas\/cliente\/\?id=/);
  const customerUrl = credit.url();
  await credit.getByLabel("Lista de precios").selectOption({ label: "Resorts" });
  await submit(credit, "Preparar términos");
  await expect(credit.getByTestId("terms-list:2")).toHaveText("Resorts");
  await controller.goto(customerUrl);
  await confirmAction(controller, "Aprobar");
  await expect(controller.getByTestId("customer-price-list")).toContainText("Resorts");

  // The order: our truck to Bávaro, priced by the server from «Resorts» with its freight.
  const seller = await signIn(browser, "Vendedor");
  await nav(seller, "Pedidos");
  await seller.getByRole("link", { name: "Nuevo pedido" }).click();
  await seller.getByLabel("Cliente", { exact: true }).selectOption({ label: "Hotel Playa Bávaro (101000001)" });
  await seller.getByLabel("Término de entrega").selectOption("DELIVERED_OWN_TRANSPORT");
  await seller.getByLabel("Dirección de la obra").fill("Hotel Playa Bávaro, Punta Cana");
  await seller.getByLabel("Zona de entrega").selectOption({ label: "Bávaro" });
  await seller.getByLabel("Producto 1").selectOption({ label: "BLOQUE-6 — Bloque de 6 pulgadas (un)" });
  await seller.getByLabel("Cantidad 1").fill("100");
  await expect(seller.getByTestId("preview-line-net:1")).toHaveText("4,400.00");
  await expect(seller.getByTestId("preview-line-freight:1")).toContainText("350.00");
  await expect(seller.getByTestId("preview-freight")).toHaveText("350.00");
  await expect(seller.getByTestId("preview-itbis")).toHaveText("792.00");
  await expect(seller.getByTestId("preview-total")).toHaveText("5,542.00");
  await expectFits(seller);
  await submit(seller, "Crear pedido");
  await expect(seller.getByTestId("order-total")).toHaveText("4,750.00");
  await expect(seller.getByTestId("order-line-source:1")).toHaveText("Lista Resorts");
  await expect(seller.getByTestId("order-line-freight:1")).toContainText("Bávaro");
  await seller.getByRole("button", { name: "Enviar a crédito" }).click();
  await expect(seller.getByTestId("order-status")).toHaveText("Confirmado");
  const orderId = new URL(seller.url()).searchParams.get("id");

  // Despacho: our truck, the conduce shows the freight, the POD.
  const dispatch = await signIn(browser, "Despacho");
  await dispatch.goto(`/despacho/planificar/?pedido=${orderId}`);
  await submit(dispatch, "Planificar conduce");
  const status = dispatch.getByTestId("delivery-status");
  await expect(status).toHaveText("Planificado");
  await dispatch.getByLabel("Camión").selectOption({ label: "BR 09 · L123456 (12,000 kg)" });
  await dispatch.getByLabel("Chofer").selectOption({ label: "Juan Pérez" });
  await dispatch.getByRole("button", { name: "Iniciar carga" }).click();
  await expect(status).toHaveText("Cargando");
  await dispatch.getByRole("button", { name: "Confirmar carga" }).click();
  await expect(status).toHaveText("Cargado");
  const deliveryUrl = dispatch.url();
  const deliveryNo = ((await dispatch.getByRole("heading", { level: 1 }).innerText()).match(/CD-\d+/) ?? [""])[0];
  await dispatch.getByLabel("Peso bruto (kg)").fill("9000");
  await dispatch.getByLabel("Tara (kg)").fill("8000");
  await attachEvidence(dispatch, "Ticket de báscula", "ticket-resorts.jpg");
  await dispatch.getByRole("button", { name: "Registrar pesada y salida" }).click();
  await expect(status).toHaveText("En tránsito");
  await dispatch.getByRole("link", { name: "Imprimir conduce" }).click();
  await expect(dispatch.getByTestId("conduce-freight:1")).toContainText("Transporte de blocks — Bávaro");
  await expectFits(dispatch);
  await dispatch.goto(deliveryUrl);
  await dispatch.getByLabel("Recibió (nombre)").fill("Ing. Ana Ruiz");
  await dispatch.getByLabel("Fecha y hora de recepción").fill(dominicanNow(-1));
  await attachEvidence(dispatch, "Constancia de entrega firmada (foto o firma)", "pod-resorts.jpg");
  await dispatch.getByRole("button", { name: "Registrar entrega al cliente" }).click();
  await expect(status).toHaveText("Entregado");

  // Facturación: the block with ITBIS and the freight exempt.
  const billing = await signIn(browser, "Facturación");
  await nav(billing, "Por facturar");
  await billing.getByRole("checkbox", { name: `Facturar ${deliveryNo} BLOQUE-6` }).check();
  await billing.getByRole("button", { name: "Crear factura con 1 línea(s)" }).click();
  await confirmAction(billing, "Emitir factura");
  await expect(billing.getByTestId("invoice-total")).toHaveText("5,542.00");
  await expect(billing.getByTestId("invoice-line-freight:2")).toHaveText("Flete · exento de ITBIS");
  await expectFits(billing);
});
