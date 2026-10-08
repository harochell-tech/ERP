import { expect, test, type Browser, type Page } from "@playwright/test";
import { nav, pick, signIn, submit } from "./support";

// ENT-1 E2E-ENT through the UI (ENT1-03, E-ENT-1…8, E-ENT1-01-1…10): Despacho gives Juan Pérez his PIN; two site deliveries on our
// truck go out of the gate with the driver's QR on the conduce. On the first the driver, from a phone without signing in, gets one
// PIN wrong, then confirms everything received with a photo: the delivery is Entregado with no one in the office. On the second
// he reports broken blocks: the delivery waits, Inicio counts it, and Despacho completes it from what the driver wrote.

// A 1×1 PNG: the page reduces it to a JPEG as it would a camera photo.
const PHOTO = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==", "base64");

async function siteOrder(browser: Browser, quantity: string): Promise<string> {
  const seller = await signIn(browser, "Vendedor");
  await nav(seller, "Pedidos");
  await seller.getByRole("link", { name: "Nuevo pedido" }).click();
  await pick(seller.getByLabel("Cliente", { exact: true }), "Constructora Uno");
  await seller.getByLabel("Término de entrega").selectOption("DELIVERED_OWN_TRANSPORT");
  await seller.getByLabel("Dirección de la obra").fill("Obra Bávaro");
  await seller.getByLabel("Zona de entrega").selectOption({ label: "Higüey" });
  await pick(seller.getByLabel("Producto 1"), "BLOQUE-6", "BLOQUE-6 — Bloque de 6 pulgadas (un)");
  await seller.getByLabel("Cantidad 1").fill(quantity);
  await submit(seller, "Crear pedido");
  await seller.getByRole("button", { name: "Enviar a crédito" }).click();
  await expect(seller.getByTestId("order-status")).toHaveText("Confirmado");
  const url = seller.url();
  await seller.close();
  return url;
}

/** Plans, loads on BR 09 with Juan Pérez, weighs out; returns the QR's address printed on the conduce. */
async function gateOut(dispatch: Page, orderUrl: string, ticket: string): Promise<string> {
  await dispatch.goto(orderUrl);
  await dispatch.getByRole("link", { name: "Planificar conduce" }).click();
  await submit(dispatch, "Planificar conduce");
  const status = dispatch.getByTestId("delivery-status");
  await expect(status).toHaveText("Planificado");
  await dispatch.getByLabel("Camión").selectOption({ label: "BR 09 · L123456 (12,000 kg)" });
  await dispatch.getByLabel("Chofer").selectOption({ label: "Juan Pérez" });
  await dispatch.getByRole("button", { name: "Iniciar carga" }).click();
  await expect(status).toHaveText("Cargando");
  await dispatch.getByRole("button", { name: "Confirmar carga" }).click();
  await expect(status).toHaveText("Cargado");
  // E-ENT-8: no QR before the gate.
  const deliveryUrl = dispatch.url();
  await dispatch.getByRole("link", { name: "Imprimir conduce" }).click();
  await expect(dispatch.getByTestId("watermark")).toHaveText("BORRADOR – NO DESPACHADO");
  await expect(dispatch.getByTestId("conduce-driver-qr")).toHaveCount(0);
  await dispatch.goto(deliveryUrl);
  await dispatch.getByLabel("Peso bruto (kg)").fill("9000");
  await dispatch.getByLabel("Tara (kg)").fill("8000");
  await dispatch.getByLabel("Ticket de báscula", { exact: true }).setInputFiles({ name: ticket, mimeType: "application/octet-stream", buffer: Buffer.from(`evidencia ${ticket}`) });
  await dispatch.getByRole("button", { name: "Registrar pesada y salida" }).click();
  await expect(status).toHaveText("En tránsito");
  await expect(dispatch.getByTestId("driver-link-state")).toContainText("Activo: el chofer puede confirmar");
  await dispatch.getByRole("link", { name: "Imprimir conduce" }).click();
  await expect(dispatch.getByTestId("conduce-driver-qr")).toContainText("Chofer: escanee para confirmar la entrega");
  const qr = (await dispatch.getByTestId("driver-qr").getAttribute("data-url")) ?? "";
  expect(qr).toMatch(/\/entrega\/\?c=[0-9a-f]{32}&d=[0-9a-f]{32}&g=1&k=[A-Za-z0-9_-]{43}$/);
  await dispatch.goto(deliveryUrl);
  return qr;
}

/** The driver's phone: no session, the address from the QR. */
async function phone(browser: Browser, url: string): Promise<Page> {
  const context = await browser.newContext({ geolocation: { latitude: 18.6827, longitude: -68.4547 }, permissions: ["geolocation"] });
  const page = await context.newPage();
  await page.goto(url);
  return page;
}

test("the driver confirms from the conduce's QR, and differences go to Despacho (E2E-ENT)", async ({ browser }) => {
  test.setTimeout(240_000);

  // E-ENT-2: Despacho gives Juan Pérez his PIN.
  const dispatch = await signIn(browser, "Despacho");
  await dispatch.goto("/maestros/flota/");
  const pinCell = dispatch.getByTestId("driver-pin:00112345678");
  await pinCell.getByRole("button", { name: /Asignar PIN|Cambiar PIN/ }).click();
  await dispatch.getByLabel("PIN de Juan Pérez").fill("4821");
  await pinCell.getByRole("button", { name: "Guardar PIN" }).click();
  await expect(pinCell).toContainText("Asignado");

  // First delivery: everything received.
  const first = await gateOut(dispatch, await siteOrder(browser, "60"), "ticket-qr-1.jpg");
  const driver = await phone(browser, first);
  await expect(driver.getByTestId("driver-delivery")).toContainText("Constructora Uno");
  await expect(driver.getByTestId("driver-delivery")).toContainText("60 un · Bloque de 6 pulgadas");
  await expect(driver.getByText("precio", { exact: false })).toHaveCount(0);
  await driver.getByTestId("driver-pin").fill("1111");
  await driver.getByRole("button", { name: "Continuar" }).click();
  await expect(driver.getByTestId("driver-notice")).toHaveText("PIN equivocado. Le quedan 4 intentos.");
  await driver.getByTestId("driver-pin").fill("4821");
  await driver.getByRole("button", { name: "Continuar" }).click();
  await driver.getByTestId("driver-receiver").fill("Ing. María Gómez");
  await driver.getByTestId("driver-photo").setInputFiles({ name: "entrega.png", mimeType: "image/png", buffer: PHOTO });
  await expect(driver.getByAltText("Foto de la entrega")).toBeVisible();
  await driver.getByTestId("driver-confirm").click();
  await expect(driver.getByTestId("driver-done")).toHaveText("Entrega confirmada. Gracias.");
  // The same QR again: used.
  await driver.reload();
  await expect(driver.getByTestId("driver-state")).toHaveText("Esta entrega ya fue confirmada.");
  await driver.context().close();
  await dispatch.reload();
  await expect(dispatch.getByTestId("delivery-status")).toHaveText("Entregado");
  await expect(dispatch.getByTestId("driver-confirmation")).toContainText("Recibido completo · recibió Ing. María Gómez");
  await expect(dispatch.getByTestId("driver-confirmation")).toContainText("ubicación");
  await dispatch.getByRole("button", { name: "Ver foto" }).click();
  await expect(dispatch.getByTestId("driver-evidence")).toBeVisible();

  // Second delivery: broken blocks at the site.
  const second = await gateOut(dispatch, await siteOrder(browser, "40"), "ticket-qr-2.jpg");
  const driver2 = await phone(browser, second);
  await driver2.getByTestId("driver-pin").fill("4821");
  await driver2.getByRole("button", { name: "Continuar" }).click();
  await driver2.getByTestId("driver-receiver").fill("Capataz Luis");
  await driver2.getByLabel("Hubo diferencias").check();
  await driver2.getByTestId("driver-note").fill("5 bloques rotos al descargar, se devuelven");
  await driver2.getByLabel("Firma en pantalla").check();
  const pad = driver2.getByLabel("Firma de quien recibe");
  await pad.scrollIntoViewIfNeeded();
  const box = (await pad.boundingBox())!;
  await driver2.mouse.move(box.x + 20, box.y + 40);
  await driver2.mouse.down();
  await driver2.mouse.move(box.x + 200, box.y + 120, { steps: 8 });
  await driver2.mouse.up();
  await expect(driver2.getByTestId("signature-ready")).toBeVisible();
  await driver2.getByTestId("driver-confirm").click();
  await expect(driver2.getByTestId("driver-done")).toHaveText("Diferencias enviadas a Despacho. Gracias.");
  await driver2.context().close();

  // E-ENT1-01-10: Inicio counts it; the POD form starts from the driver's confirmation.
  await nav(dispatch, "Inicio");
  await expect(dispatch.getByTestId("task-count:/despacho/tablero/?chofer=diferencias")).toHaveText("1");
  await dispatch.goBack();
  await dispatch.reload();
  await expect(dispatch.getByTestId("delivery-status")).toHaveText("En tránsito");
  await expect(dispatch.getByTestId("pod-from-driver")).toContainText("5 bloques rotos al descargar");
  await expect(dispatch.getByLabel("Recibió (nombre)")).toHaveValue("Capataz Luis");
  await dispatch.getByLabel("Recibido BLOQUE-6").fill("35");
  await dispatch.getByLabel("Devuelto BLOQUE-6").fill("5");
  await dispatch.getByLabel("Motivo de la excepción (si hubo faltante o devolución)").fill("Rotos al descargar");
  await dispatch.getByRole("button", { name: "Registrar entrega al cliente" }).click();
  await expect(dispatch.getByTestId("delivery-status")).toHaveText("Entregado con excepciones");
  await expect(dispatch.getByTestId("driver-confirmation")).toContainText("entrega al cliente registrada");

  // ENT1-04: the board lists the day's driver confirmations, the full one and the one with differences.
  await nav(dispatch, "Tablero de despacho");
  const today = dispatch.getByTestId("board-driver-today");
  await expect(today.getByRole("heading")).toHaveText("Confirmadas por el chofer hoy (2)");
  await expect(today.getByRole("row", { name: /Completa/ })).toHaveCount(1);
  await expect(today.getByRole("row", { name: /Capataz Luis.*Con diferencias/ })).toHaveCount(1);
});
