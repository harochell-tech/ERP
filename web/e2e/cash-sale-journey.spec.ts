import { expect, test } from "@playwright/test";
import { confirmAction, expectFits, nav, signIn, submit } from "./support";

// CF1-05 (E2E-C1, E-CF1-05-1…13): a cash sale to the final consumer through the UI. Caja sells 100 blocks at 50.00 to María Pérez
// (5,900.00 with ITBIS, below the identification amount), sends the sale to payment and collects it in two parts — 1,900.00 in cash
// (a cash payment for more than what is owed is refused) and 4,000.00 by transfer — which confirms it; Despacho delivers it on the
// customer's truck; Facturación invoices the delivery as an e-CF 32 that is born paid and records it without a receiver.

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

test("cash sale to a final consumer: collected, delivered and invoiced as a paid e-CF 32 (E2E-C1)", async ({ browser }) => {
  test.setTimeout(240_000);
  const stamp = String(Date.now());

  // Caja: the sale and its buyer, priced by the server.
  const caja = await signIn(browser, "Caja");
  await nav(caja, "Venta de contado");
  await caja.getByRole("link", { name: "Nueva venta de contado" }).click();
  await expect(caja.getByRole("heading", { name: "Nueva venta de contado" })).toBeVisible();
  await expect(caja.getByTestId("threshold-amount")).toHaveText("250,000.00");
  await caja.getByLabel("Producto 1").selectOption({ label: "BLOQUE-6 — Bloque de 6 pulgadas (un)" });
  await caja.getByLabel("Cantidad 1").fill("100");
  await expect(caja.getByTestId("preview-total")).toHaveText("5,900.00");
  await caja.getByLabel("Nombre del comprador").fill("María Pérez");
  await expectFits(caja);
  await submit(caja, "Crear venta");
  const status = caja.getByTestId("order-status");
  await expect(status).toHaveText("Borrador");
  await expect(caja.getByTestId("cash-buyer")).toContainText("María Pérez · sin identificación");
  await expect(caja.getByTestId("cash-not-sent")).toBeVisible();

  // Sent to payment: the total with the ITBIS of the day is fixed.
  await caja.getByRole("button", { name: "Enviar a pago" }).click();
  await expect(status).toHaveText("Pendiente de pago");
  await expect(caja.getByTestId("cash-itbis")).toHaveText("900.00");
  await expect(caja.getByTestId("cash-total")).toHaveText("5,900.00");
  await expect(caja.getByTestId("cash-due")).toHaveText("5,900.00");

  // E-CF1-05-5: cash is received for what is owed, never more. E-CF1-05-4: two payments, cash and transfer.
  await expect(caja.getByLabel("Monto del cobro")).toHaveValue("5900.00");
  await caja.getByLabel("Monto del cobro").fill("6000");
  await caja.getByRole("button", { name: "Cobrar" }).click();
  await expect(caja.getByText("El efectivo se registra por lo que falta por pagar")).toBeVisible();
  await caja.getByLabel("Monto del cobro").fill("1900");
  await caja.getByRole("button", { name: "Cobrar" }).click();
  await expect(caja.getByTestId("cash-due")).toHaveText("4,000.00");
  await expect(status).toHaveText("Pendiente de pago");
  await caja.getByLabel("Medio de cobro").selectOption("TRANSFER");
  await expect(caja.getByLabel("Monto del cobro")).toHaveValue("4000.00");
  await caja.getByLabel("Referencia (opcional)").fill(`TRF-CS-${stamp}`);
  await caja.getByRole("button", { name: "Cobrar" }).click();
  await expect(status).toHaveText("Confirmado");
  await expect(caja.getByTestId("cash-due")).toHaveText("0.00");
  await expect(caja.getByTestId("cash-counted")).toHaveText("5,900.00");
  await expect(caja.getByTestId("cash-payments").locator("tbody tr")).toHaveCount(2);
  await expect(caja.getByTestId("payment-form")).toHaveCount(0);
  await expectFits(caja);
  const saleUrl = caja.url();
  const saleNo = ((await caja.getByRole("heading", { level: 1 }).innerText()).match(/PV-\d+/) ?? [""])[0];
  expect(saleNo).not.toBe("");

  // The sale shows among the orders, marked «Contado» with its buyer.
  await nav(caja, "Pedidos");
  await expect(caja.getByRole("row", { name: new RegExp(saleNo) })).toContainText("Consumidor final — María Pérez");

  // Despacho: the paid sale leaves on the customer's truck.
  const dispatch = await signIn(browser, "Despacho");
  await dispatch.goto(saleUrl);
  await dispatch.getByRole("link", { name: "Planificar conduce" }).first().click();
  await expect(dispatch.getByLabel("A despachar BLOQUE-6")).toHaveValue("100");
  await submit(dispatch, "Planificar conduce");
  const delivery = dispatch.getByTestId("delivery-status");
  await expect(delivery).toHaveText("Planificado");
  const deliveryNo = ((await dispatch.getByRole("heading", { level: 1 }).innerText()).match(/CD-\d+/) ?? [""])[0];
  expect(deliveryNo).not.toBe("");
  await dispatch.getByLabel("Placa del cliente").fill("G654321");
  await dispatch.getByLabel("Chofer del cliente").fill("Pedro Díaz");
  await dispatch.getByRole("button", { name: "Iniciar carga" }).click();
  await expect(delivery).toHaveText("Cargando");
  await dispatch.getByRole("button", { name: "Confirmar carga" }).click();
  await expect(delivery).toHaveText("Cargado");
  await dispatch.getByLabel("Peso bruto (kg)").fill("9000");
  await dispatch.getByLabel("Tara (kg)").fill("8000");
  await dispatch.getByLabel("Ticket de báscula", { exact: true }).setInputFiles({ name: "ticket-contado.jpg", mimeType: "application/octet-stream", buffer: Buffer.from(`ticket ${stamp}`) });
  await expect(dispatch.getByTestId("evidence-verified")).toHaveText("✓ Huella del archivo verificada: ticket-contado.jpg");
  await dispatch.getByRole("button", { name: "Registrar pesada y salida" }).click();
  await expect(delivery).toHaveText("Entregado");

  // Facturación: the delivery as an e-CF 32, born paid from what Caja collected, recorded without a receiver.
  const billing = await signIn(browser, "Facturación");
  await nav(billing, "Por facturar");
  await billing.getByRole("checkbox", { name: `Facturar ${deliveryNo} BLOQUE-6` }).check();
  await billing.getByRole("button", { name: "Crear factura con 1 línea(s)" }).click();
  await confirmAction(billing, "Emitir factura");
  await expect(billing.getByTestId("invoice-status")).toHaveText("Cobrada");
  await expect(billing.getByTestId("invoice-total")).toHaveText("5,900.00");
  await billing.getByLabel("e-NCF", { exact: true }).fill(`E32${stamp.slice(-10)}`);
  await billing.getByLabel("Emitido el").fill(dominicanNow(-1));
  await billing.getByLabel("Código de seguridad").fill("A1B2C3");
  await billing.getByLabel("XML o PDF del e-CF", { exact: true }).setInputFiles({ name: "e-cf-contado.xml", mimeType: "application/octet-stream", buffer: Buffer.from(`e-cf ${stamp}`) });
  await expect(billing.getByLabel("SHA-256")).toHaveValue(/^[0-9a-f]{64}$/);
  await expect(billing.getByLabel("Cédula o RNC del receptor (si el e-CF lo lleva)")).toHaveValue("");
  await billing.getByLabel("Neto (según el portal)").fill("5000.00");
  await billing.getByLabel("ITBIS (según el portal)").fill("900.00");
  await billing.getByLabel("Total (según el portal)").fill("5900.00");
  await expectFits(billing);
  await billing.getByRole("button", { name: "Registrar e-CF" }).click();
  await expect(billing.getByTestId("invoice-fiscal-status")).toHaveText("e-CF aceptado");

  // Back at Caja: the sale is delivered and what was collected is on its invoice.
  await caja.goto(saleUrl);
  await expect(status).toHaveText("Entregado");
  await expect(caja.getByTestId("cash-invoiced")).toHaveText("5,900.00");
  await expect(caja.getByTestId("cash-due")).toHaveText("0.00");
});
