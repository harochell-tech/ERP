import { expect, test, type Page } from "@playwright/test";
import { confirmAction, nav, signIn } from "./support";

// FIS1-05 (E-FIS1-05-11): a CONFOTUR exempt sale through the UI. Facturación registers an authorization for Constructora Uno
// (BLOQUE-6, 100 blocks / 5,000.00) with a unique certificate, attaches the DGII certificate and submits it; the Especialista fiscal
// verifies it (step-up); the Vendedor creates an order (and opens its proforma), Despacho delivers it, and Facturación invoices the
// delivery under the authorization: e-CF 44 without ITBIS, issued and recorded with an E44 e-NCF; the authorization shows the
// consumption. The journey opens its own order and delivery by URL and uses its own certificate, so other journeys' data never
// matters (and it leaves nothing open for them: the order is fully delivered and invoiced).


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

/** UX3-02 (E-UX3-8 (a)): weigh tickets and PODs show no SHA-256 field; choosing the file verifies its fingerprint. */
async function attachEvidence(page: Page, label: string, name: string) {
  await page.getByLabel(label, { exact: true }).setInputFiles({ name, mimeType: "application/octet-stream", buffer: Buffer.from(`evidencia ${name}`) });
  await expect(page.getByTestId("evidence-verified")).toHaveText(`✓ Huella del archivo verificada: ${name}`);
  await expect(page.getByLabel("SHA-256")).toHaveCount(0);
}

test("a CONFOTUR authorization verified and consumed by an e-CF 44 invoice", async ({ browser }) => {
  const stamp = String(Date.now());
  const certificate = `CERT-E2E-${stamp}`;
  const encf = `E44${stamp.slice(-10)}`;

  // Facturación registers the authorization, attaches the DGII certificate and submits it.
  const billing = await signIn(browser, "Facturación");
  await nav(billing, "Autorizaciones fiscales");
  await billing.getByRole("link", { name: "Registrar autorización" }).click();
  await expect(billing.getByRole("heading", { name: "Registrar autorización fiscal" })).toBeVisible();
  await billing.getByLabel("Cliente", { exact: true }).selectOption({ label: "Constructora Uno (131925332)" });
  await billing.getByLabel("Número de certificado").fill(certificate);
  await billing.getByLabel("Emitido el").fill(dominicanNow().date);
  await billing.getByLabel("Vigente hasta").fill(dominicanNow(0, 180).date);
  await billing.getByLabel("Proyecto", { exact: true }).fill("Hotel Playa (E2E)");
  await billing.getByLabel("Resolución CONFOTUR").fill(`CONFOTUR-${stamp}`);
  await billing.getByLabel("Producto 1").selectOption({ label: "BLOQUE-6 — Bloque de 6 pulgadas (un)" });
  await billing.getByLabel("Cantidad 1").fill("100");
  await billing.getByLabel("Neto 1").fill("5000.00");
  await billing.getByRole("button", { name: "Registrar autorización" }).click();
  const status = billing.getByTestId("authorization-status");
  await expect(status).toHaveText("Borrador");
  const authorizationUrl = billing.url();
  await billing.getByLabel("Tipo de documento").selectOption("CERTIFICADO_DGII");
  await attach(billing, "Archivo del documento", "certificado-dgii.pdf");
  await billing.getByRole("button", { name: "Adjuntar documento" }).click();
  await expect(billing.getByRole("cell", { name: "certificado-dgii.pdf" })).toBeVisible();
  await billing.getByRole("button", { name: "Enviar a verificación" }).click();
  await expect(status).toHaveText("Pendiente de verificación");
  await expect(billing.getByRole("button", { name: "Verificar" })).toHaveCount(0);

  // The Especialista fiscal sees it on Inicio and verifies it (step-up: the fresh sign-in counts).
  const specialist = await signIn(browser, "Especialista fiscal");
  await expect(specialist.getByRole("link", { name: "Autorizaciones por verificar" })).toBeVisible();
  await specialist.goto(authorizationUrl);
  await confirmAction(specialist, "Verificar");
  await expect(specialist.getByTestId("authorization-status")).toHaveText("Activo");
  // UX4-02 (G-26): valid for 180 more days, from the server's daysToExpiry; the history has no empty "Por" column.
  await expect(specialist.getByTestId("expiry")).toHaveText(/^Vence en \d+ días$/);
  await expect(specialist.getByTestId("authorization-history").getByRole("columnheader", { name: "Por" })).toHaveCount(0);

  // The Vendedor creates the order (40 blocks at 50.00) and opens its proforma.
  const seller = await signIn(browser, "Vendedor");
  await nav(seller, "Pedidos");
  await seller.getByRole("link", { name: "Nuevo pedido" }).click();
  await seller.getByLabel("Cliente", { exact: true }).selectOption({ label: "Constructora Uno (131925332)" });
  await seller.getByLabel("Producto 1").selectOption({ label: "BLOQUE-6 — Bloque de 6 pulgadas (un)" });
  await seller.getByLabel("Cantidad 1").fill("40");
  await seller.getByRole("button", { name: "Crear pedido" }).click();
  await expect(seller.getByTestId("order-total")).toHaveText("2,000.00");
  await seller.getByRole("button", { name: "Enviar a crédito" }).click();
  await expect(seller.getByTestId("order-status")).toHaveText("Confirmado");
  const orderUrl = seller.url();
  await seller.getByRole("link", { name: "Proforma", exact: true }).click(); // the menu also has "Proformas" (FIS1b-07)
  await expect(seller.getByTestId("proforma-net")).toHaveText("2,000.00");
  await expect(seller.getByTestId("proforma-itbis")).toHaveText("360.00");
  await expect(seller.getByText("Firma del suplidor")).toBeVisible();
  await expect(seller.getByRole("button", { name: "Imprimir" })).toBeVisible();

  // Despacho: picked up at the plant, delivered at the gate.
  const dispatch = await signIn(browser, "Despacho");
  await dispatch.goto(orderUrl);
  await dispatch.getByRole("link", { name: "Planificar conduce" }).click();
  await dispatch.getByLabel("A despachar BLOQUE-6").fill("40");
  await dispatch.getByRole("button", { name: "Planificar conduce" }).click();
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
  await dispatch.getByLabel("Peso bruto (kg)").fill("5000");
  await dispatch.getByLabel("Tara (kg)").fill("4400");
  await attachEvidence(dispatch, "Ticket de báscula", "ticket-e2e-44.jpg");
  await dispatch.getByRole("button", { name: "Registrar pesada y salida" }).click();
  await expect(delivery).toHaveText("Entregado");

  // Facturación invoices the delivery under the authorization: e-CF 44, ITBIS 0.00.
  await nav(billing, "Por facturar");
  await billing.getByRole("checkbox", { name: `Facturar ${deliveryNo} BLOQUE-6` }).check();
  const choice = billing.getByLabel("Autorización fiscal (e-CF 44)");
  const option = choice.locator("option", { hasText: certificate });
  await choice.selectOption((await option.getAttribute("value")) ?? "");
  await billing.getByRole("button", { name: "Crear factura con 1 línea(s)" }).click();
  await expect(billing.getByText(/e-CF 44/).first()).toBeVisible();
  await expect(billing.getByTestId("invoice-exemption")).toContainText("Exenta — CONFOTUR");
  await confirmAction(billing, "Emitir factura");
  await expect(billing.getByTestId("invoice-status")).toHaveText("Emitida");
  await expect(billing.getByTestId("invoice-total")).toHaveText("2,000.00");
  await expect(billing.getByTestId("invoice-exemption")).toContainText(certificate);
  await expect(billing.locator("tr", { hasText: "Indicador de facturación" }).locator("td").first()).toHaveText("4");
  await billing.getByLabel("e-NCF", { exact: true }).fill(encf);
  await billing.getByLabel("Emitido el").fill(dominicanNow(-1).dateTime);
  await billing.getByLabel("Código de seguridad").fill("E44SEC");
  await attach(billing, "XML o PDF del e-CF", "e-cf-44.xml");
  await billing.getByLabel("RNC del receptor (según el portal)").fill("131925332");
  await billing.getByLabel("Neto (según el portal)").fill("2000.00");
  await billing.getByLabel("ITBIS (según el portal)").fill("0.00");
  await billing.getByLabel("Total (según el portal)").fill("2000.00");
  await billing.getByRole("button", { name: "Registrar e-CF" }).click();
  await expect(billing.getByTestId("invoice-fiscal-status")).toHaveText("e-CF aceptado");
  const invoiceNo = ((await billing.getByRole("heading", { level: 1 }).innerText()).match(/FA-\d+/) ?? [""])[0];

  // The authorization shows the consumption: 2,000.00 consumed, 3,000.00 still available.
  await billing.goto(authorizationUrl);
  await expect(billing.getByTestId("authorization-net-consumed")).toHaveText("2,000.00");
  await expect(billing.getByTestId("authorization-net-available:1")).toHaveText("3,000.00");
  const consumption = billing.getByTestId("authorization-consumptions").locator("tbody tr");
  await expect(consumption).toHaveCount(1);
  await expect(consumption).toContainText(invoiceNo);
  await expect(consumption).toContainText("Consumo");
});
