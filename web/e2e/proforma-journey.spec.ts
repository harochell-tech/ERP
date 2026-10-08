import { expect, test, type Page } from "@playwright/test";
import { confirmAction, expectFits, nav, pick, signIn, submit } from "./support";

// FIS1b-07 (E2E-P1, E-FIS1b-01-14): the CONFOTUR proforma as a collection document, through the UI. The Vendedor marks an order
// "exención en trámite, se cobra con ITBIS"; Despacho delivers it in two conduces and each issues its proforma (30 and 20 blocks at
// 50.00: 1,770.00 and 1,180.00 with ITBIS); Cobros records the customer's transfer of 2,950.00 and assigns it to the two proformas;
// Facturación registers the DGII certification citing both and the Especialista fiscal verifies it; Facturación invoices the two
// proformas as one e-CF 44 (2,500.00, paid by what was assigned); the ITBIS advanced (450.00) stays as the receipt's credit balance
// and is refunded: Cobros prepares the refund, the Controller releases it. The journey leaves nothing open for the others (the
// refund's match with the bank statement is E2E-P1 over the API).

function dominicanNow(offsetMinutes = 0, offsetDays = 0): { date: string; dateTime: string } {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: "America/Santo_Domingo",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hourCycle: "h23",
  }).formatToParts(new Date(Date.now() + offsetMinutes * 60_000 + offsetDays * 86_400_000));
  const get = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  const date = `${get("year")}-${get("month")}-${get("day")}`;
  return { date, dateTime: `${date}T${get("hour")}:${get("minute")}` };
}

async function attach(page: Page, label: string, name: string) {
  await page.getByLabel(label, { exact: true }).setInputFiles({ name, mimeType: "application/octet-stream", buffer: Buffer.from(`evidencia ${name}`) });
  await expect(page.getByLabel("SHA-256")).toHaveValue(/^[0-9a-f]{64}$/);
}

/** Plans a pickup of the order, loads it on the customer's truck and weighs it out: the delivery is the gate-out. Returns CD-…. */
async function deliver(dispatch: Page, orderUrl: string, quantity: string, ticket: string): Promise<string> {
  await dispatch.goto(orderUrl);
  await dispatch.getByRole("link", { name: "Planificar conduce" }).click();
  await dispatch.getByLabel("A despachar BLOQUE-6").fill(quantity);
  await submit(dispatch, "Planificar conduce");
  const status = dispatch.getByTestId("delivery-status");
  await expect(status).toHaveText("Planificado");
  const deliveryNo = ((await dispatch.getByRole("heading", { level: 1 }).innerText()).match(/CD-\d+/) ?? [""])[0];
  expect(deliveryNo).not.toBe("");
  await dispatch.getByLabel("Placa del cliente").fill("G654321");
  await dispatch.getByLabel("Chofer del cliente").fill("Pedro Díaz");
  await dispatch.getByRole("button", { name: "Iniciar carga" }).click();
  await expect(status).toHaveText("Cargando");
  await dispatch.getByRole("button", { name: "Confirmar carga" }).click();
  await expect(status).toHaveText("Cargado");
  await dispatch.getByLabel("Peso bruto (kg)").fill("5000");
  await dispatch.getByLabel("Tara (kg)").fill("4400");
  await dispatch.getByLabel("Ticket de báscula", { exact: true }).setInputFiles({ name: ticket, mimeType: "application/octet-stream", buffer: Buffer.from(`evidencia ${ticket}`) });
  await expect(dispatch.getByTestId("evidence-verified")).toHaveText(`✓ Huella del archivo verificada: ${ticket}`);
  await dispatch.getByRole("button", { name: "Registrar pesada y salida" }).click();
  await expect(status).toHaveText("Entregado");
  return deliveryNo;
}

test("proformas collected with ITBIS, certified, invoiced as one e-CF 44 and the ITBIS refunded", async ({ browser }) => {
  test.setTimeout(240_000); // six people, two deliveries, an invoice and a refund: longer than the other journeys
  const stamp = String(Date.now());
  const certificate = `CERT-PF-${stamp}`;

  // The Vendedor: an order of 50 blocks whose exemption is in process; its proformas collect with ITBIS.
  const seller = await signIn(browser, "Vendedor");
  await nav(seller, "Pedidos");
  await seller.getByRole("link", { name: "Nuevo pedido" }).click();
  await pick(seller.getByLabel("Cliente", { exact: true }), "Constructora Uno");
  await pick(seller.getByLabel("Producto 1"), "BLOQUE-6", "BLOQUE-6 — Bloque de 6 pulgadas (un)");
  await seller.getByLabel("Cantidad 1").fill("50");
  await seller.getByLabel("Exención de ITBIS").selectOption("WITH_ITBIS");
  await submit(seller, "Crear pedido");
  await expect(seller.getByTestId("order-total")).toHaveText("2,500.00");
  await expect(seller.getByTestId("order-exemption")).toContainText("cada entrega genera su proforma, que se cobra con ITBIS");
  await seller.getByRole("button", { name: "Enviar a crédito" }).click();
  await expect(seller.getByTestId("order-status")).toHaveText("Confirmado");
  const orderUrl = seller.url();

  // Despacho: two pickups, 30 and 20 blocks. Each one issues its proforma.
  const dispatch = await signIn(browser, "Despacho");
  const firstDelivery = await deliver(dispatch, orderUrl, "30", "ticket-pf-1.jpg");
  const secondDelivery = await deliver(dispatch, orderUrl, "20", "ticket-pf-2.jpg");

  // Facturación: the two proformas, apart from the fiscal receivable; their deliveries are not billable by conduce.
  const billing = await signIn(browser, "Facturación");
  await nav(billing, "Proformas");
  await expect(billing.getByRole("heading", { name: "Proformas", exact: true })).toBeVisible();
  const customer = billing.getByTestId("proformas:Constructora Uno");
  const firstRow = customer.locator("tbody tr", { hasText: firstDelivery });
  const secondRow = customer.locator("tbody tr", { hasText: secondDelivery });
  await expect(firstRow).toContainText("1,500.00");
  await expect(firstRow).toContainText("270.00");
  await expect(firstRow).toContainText("Sin certificación");
  await expect(secondRow).toContainText("1,180.00");
  const firstNo = ((await firstRow.innerText()).match(/PF-\d+/) ?? [""])[0];
  const secondNo = ((await secondRow.innerText()).match(/PF-\d+/) ?? [""])[0];
  expect(firstNo).not.toBe("");
  expect(secondNo).not.toBe("");
  await expectFits(billing);
  await billing.getByRole("link", { name: firstNo }).click();
  await expect(billing.getByTestId("proforma-no")).toHaveText(firstNo);
  await expect(billing.getByTestId("proforma-net")).toHaveText("1,500.00");
  await expect(billing.getByTestId("proforma-itbis")).toHaveText("270.00");
  await expect(billing.getByTestId("proforma-total")).toHaveText("1,770.00");
  await expect(billing.getByTestId("proforma-balance")).toHaveText("1,770.00");
  await expect(billing.getByText("Firma del suplidor")).toBeVisible();
  await expect(billing.getByRole("link", { name: "Imprimir" })).toBeVisible(); // PRT-01: the server's printed proforma
  await expectFits(billing);
  await nav(billing, "Por facturar");
  await expect(billing.getByRole("heading", { name: "Por facturar" })).toBeVisible();
  await expect(billing.getByRole("checkbox", { name: `Facturar ${firstDelivery} BLOQUE-6` })).toHaveCount(0);
  await billing.goto("/ventas/antiguedad/");
  await expect(billing.getByTestId("aging-proformas")).toHaveText("2,950.00");
  await expectFits(billing);

  // Cobros: the customer's transfer for both proformas with their ITBIS. It is recorded without applying it to invoices and
  // assigned to the proformas on the receipt.
  const cobros = await signIn(browser, "Cobros");
  await nav(cobros, "Recibos");
  await cobros.getByRole("link", { name: "Registrar cobro" }).click();
  await pick(cobros.getByLabel("Cliente", { exact: true }), "Constructora Uno");
  await expect(cobros.getByTestId("open-proformas-notice")).toContainText(firstNo);
  await cobros.getByLabel("Monto del cobro").fill("2950.00");
  await cobros.getByLabel("Cuenta bancaria").selectOption({ label: "TEST_BANK ••••4321" });
  await expect(cobros.getByTestId("suggestion-none").or(cobros.getByTestId("suggestion-totals"))).toBeVisible();
  const leaveUnapplied = cobros.getByRole("button", { name: "Dejar sin aplicar" });
  if ((await leaveUnapplied.count()) > 0) {
    await leaveUnapplied.click();
  }
  await submit(cobros, "Registrar cobro");
  await expect(cobros.getByTestId("receipt-unapplied")).toHaveText("2,950.00");
  const receiptUrl = cobros.url();
  await cobros.getByLabel(`Asignar a ${firstNo}`).fill("1770.00");
  await cobros.getByLabel(`Asignar a ${secondNo}`).fill("1180.00");
  await submit(cobros, "Asignar a proformas");
  await expect(cobros.getByTestId("receipt-allocated")).toHaveText("2,950.00");
  await expect(cobros.getByTestId("receipt-available")).toHaveText("0.00");
  await expect(cobros.getByTestId("receipt-allocations").locator("tbody tr")).toHaveCount(2);
  await expectFits(cobros);

  // Facturación registers the DGII certification citing the two proformas (the scope comes from them) and submits it.
  await nav(billing, "Autorizaciones fiscales");
  await billing.getByRole("link", { name: "Registrar autorización" }).click();
  // The list has its own "Cliente" filter: wait for the form before choosing the customer.
  await expect(billing.getByRole("heading", { name: "Registrar autorización fiscal" })).toBeVisible();
  await pick(billing.getByLabel("Cliente", { exact: true }), "Constructora Uno");
  await billing.getByLabel("Número de certificado").fill(certificate);
  await billing.getByLabel("Emitido el").fill(dominicanNow().date);
  await billing.getByLabel("Vigente hasta").fill(dominicanNow(0, 180).date);
  await billing.getByLabel("Proyecto", { exact: true }).fill("Hotel Playa (proformas E2E)");
  await billing.getByLabel("Resolución CONFOTUR").fill(`CONFOTUR-PF-${stamp}`);
  await billing.getByRole("checkbox", { name: `Citar ${firstNo}` }).check();
  await billing.getByRole("checkbox", { name: `Citar ${secondNo}` }).check();
  await expect(billing.getByTestId("scope-from-proformas")).toContainText("2 proforma(s)");
  await submit(billing, "Registrar autorización");
  const status = billing.getByTestId("authorization-status");
  await expect(status).toHaveText("Borrador");
  const authorizationUrl = billing.url();
  await expect(billing.getByTestId("authorization-proformas").locator("tbody tr")).toHaveCount(2);
  await billing.getByLabel("Tipo de documento").selectOption("CERTIFICADO_DGII");
  await attach(billing, "Archivo del documento", "certificado-proformas.pdf");
  await billing.getByRole("button", { name: "Adjuntar documento" }).click();
  await expect(billing.getByRole("cell", { name: "certificado-proformas.pdf" })).toBeVisible();
  await billing.getByRole("button", { name: "Enviar a verificación" }).click();
  await expect(status).toHaveText("Pendiente de verificación");

  // The Especialista fiscal verifies it (step-up: the fresh sign-in counts).
  const specialist = await signIn(browser, "Especialista fiscal");
  await specialist.goto(authorizationUrl);
  await confirmAction(specialist, "Verificar");
  await expect(specialist.getByTestId("authorization-status")).toHaveText("Activo");

  // Facturación invoices the two proformas under the certification: one e-CF 44 without ITBIS, paid by what was assigned.
  await nav(billing, "Proformas");
  await expect(firstRow).toContainText("Certificada");
  await billing.getByRole("checkbox", { name: `Facturar ${firstNo}` }).check();
  await billing.getByRole("checkbox", { name: `Facturar ${secondNo}` }).check();
  const choice = billing.getByLabel("Comprobante de Constructora Uno");
  const option = choice.locator("option", { hasText: certificate });
  await choice.selectOption((await option.getAttribute("value")) ?? "");
  await submit(billing, "Crear factura con 2 proforma(s)");
  await expect(billing.getByTestId("invoice-exemption")).toContainText("Exenta — CONFOTUR");
  await confirmAction(billing, "Emitir factura");
  // Issued and, in the same act, paid by what the receipt had assigned to its proformas.
  await expect(billing.getByTestId("invoice-status")).toHaveText("Cobrada");
  await expect(billing.getByTestId("invoice-total")).toHaveText("2,500.00");
  const invoiceNo = ((await billing.getByRole("heading", { level: 1 }).innerText()).match(/FA-\d+/) ?? [""])[0];
  expect(invoiceNo).not.toBe("");
  await billing.getByLabel("e-NCF", { exact: true }).fill(`E44${stamp.slice(-10)}`);
  await billing.getByLabel("Emitido el").fill(dominicanNow(-1).dateTime);
  await billing.getByLabel("Código de seguridad").fill("PF44");
  await attach(billing, "XML o PDF del e-CF", "e-cf-44-proformas.xml");
  await billing.getByLabel("RNC del receptor (según el portal)").fill("131925332");
  await billing.getByLabel("Neto (según el portal)").fill("2500.00");
  await billing.getByLabel("ITBIS (según el portal)").fill("0.00");
  await billing.getByLabel("Total (según el portal)").fill("2500.00");
  await billing.getByRole("button", { name: "Registrar e-CF" }).click();
  await expect(billing.getByTestId("invoice-fiscal-status")).toHaveText("e-CF aceptado");
  await nav(billing, "Proformas");
  await expect(billing.getByRole("heading", { name: "Proformas", exact: true })).toBeVisible();
  await billing.getByLabel("Estado").selectOption("INVOICED");
  await expect(customer.locator("tbody tr", { hasText: firstNo })).toContainText(invoiceNo);

  // Cobros: the receipt paid the invoice's 2,500.00; the ITBIS advanced (450.00) is the credit balance, and Cobros prepares its
  // refund — it cannot release it.
  await cobros.goto(receiptUrl);
  await expect(cobros.getByTestId("receipt-unapplied")).toHaveText("450.00");
  await cobros.getByLabel("Motivo de la devolución").fill("ITBIS adelantado en proformas certificadas");
  await expect(cobros.getByLabel("Monto de la devolución")).toHaveValue("450.00");
  await submit(cobros, "Preparar devolución");
  const refund = cobros.getByTestId("receipt-refunds").locator("tbody tr").first();
  await expect(refund).toContainText("Preparada");
  await expect(cobros.getByRole("button", { name: "Liberar devolución" })).toHaveCount(0);
  await expectFits(cobros);

  // The Controller releases it (step-up): the money leaves the bank and the receipt has nothing left.
  const controller = await signIn(browser, "Controller");
  await controller.goto(receiptUrl);
  await confirmAction(controller, "Liberar devolución");
  await expect(controller.getByTestId("receipt-refunds").locator("tbody tr").first()).toContainText("Liberada");
  await expect(controller.getByTestId("receipt-unapplied")).toHaveText("0.00");

  // The customer's statement of account tells it: receipt, invoice, refund; no proforma is left open.
  await nav(cobros, "Estado de cuenta");
  await pick(cobros.getByLabel("Cliente", { exact: true }), "Constructora Uno");
  await expect(cobros.getByRole("cell", { name: "Devolución al cliente" })).toBeVisible();
  await expect(cobros.getByTestId("statement-proformas")).toHaveCount(0);
  await expectFits(cobros);
});
