import { expect, test, type Page } from "@playwright/test";
import { confirmAction, nav, pick, signIn, submit } from "./support";

/** A weigh ticket: choosing the file verifies its fingerprint (E-UX3-8). */
async function attachEvidence(page: Page, label: string, name: string) {
  await page.getByLabel(label, { exact: true }).setInputFiles({ name, mimeType: "application/octet-stream", buffer: Buffer.from(`evidencia ${name}`) });
  await expect(page.getByTestId("evidence-verified")).toHaveText(`✓ Huella del archivo verificada: ${name}`);
}

// VS4-04 / VS4-05 (E-VS4-04-1/2/4, E-VS4-05-1/3, E2E-ECF): against the dev stack's simulated Alanube. The Controller sets the e-CF
// issuer's address; the Especialista fiscal registers an e-NCF range and the Controller approves it; an order picked up at the plant
// is invoiced, its e-CF 31 is sent and accepted by the worker, the invoice shows the e-NCF and the QR, prints without the watermark
// and goes by e-mail (Redirect) with its XML; the Controller closes the range and annuls its unused numbers, so the other journeys keep
// the manual channel; the e-CF inbox shows the accepted e-CF with its calls.

test("e-CF 31 through the simulated Alanube: range, invoice accepted with its QR, e-mail and annulment (E2E-ECF)", async ({ browser }) => {
  test.setTimeout(180_000);
  // A range of its own on every run (closed ranges still block overlaps).
  const first = (Date.now() % 9_000_000_000) + 1;
  const digits = (n: number) => String(n).padStart(10, "0");
  const firstEncf = `E31${digits(first)}`;

  const controller = await signIn(browser, "Controller");
  await nav(controller, "Empresa");
  await controller.getByLabel("Dirección", { exact: true }).fill("Carretera Higüey–La Romana km 3, Higüey");
  await controller.getByLabel("Teléfono", { exact: true }).fill("809-554-0000");
  await confirmAction(controller, "Guardar datos del emisor");
  await expect(controller.getByTestId("company-address")).toHaveText("Carretera Higüey–La Romana km 3, Higüey");

  const specialist = await signIn(browser, "Especialista fiscal");
  await nav(specialist, "Rangos e-NCF");
  await specialist.getByLabel("Primer número", { exact: true }).fill(firstEncf);
  await specialist.getByLabel("Último número", { exact: true }).fill(String(first + 999));
  await specialist.getByLabel("Vence el", { exact: true }).fill(`${new Date().getFullYear() + 1}-12-31`);
  await specialist.getByRole("button", { name: "Registrar rango" }).click();
  const row = specialist.getByTestId(`series:${firstEncf}`);
  await expect(row).toContainText("Por aprobar");
  await expect(row.getByRole("button", { name: "Aprobar" })).toHaveCount(0); // four eyes: the Controller approves

  await nav(controller, "Rangos e-NCF");
  const controllerRow = controller.getByTestId(`series:${firstEncf}`);
  await confirmAction(controllerRow, "Aprobar");
  await expect(controllerRow).toContainText("Vigente");
  await expect(controllerRow).toContainText("1000");

  // An order of 20 blocks picked up at the plant: 1,000.00 + 18 % ITBIS.
  const seller = await signIn(browser, "Vendedor");
  await nav(seller, "Pedidos");
  await seller.getByRole("link", { name: "Nuevo pedido" }).click();
  await pick(seller.getByLabel("Cliente", { exact: true }), "Constructora Uno");
  await pick(seller.getByLabel("Producto 1"), "BLOQUE-6", "BLOQUE-6 — Bloque de 6 pulgadas (un)");
  await seller.getByLabel("Cantidad 1").fill("20");
  await submit(seller, "Crear pedido");
  await seller.getByRole("button", { name: "Enviar a crédito" }).click();
  await expect(seller.getByTestId("order-status")).toHaveText("Confirmado");
  const orderUrl = seller.url();

  const dispatch = await signIn(browser, "Despacho");
  await dispatch.goto(orderUrl);
  await dispatch.getByRole("link", { name: "Planificar conduce" }).click();
  await dispatch.getByLabel("A despachar BLOQUE-6").fill("20");
  await dispatch.getByRole("button", { name: "Planificar conduce" }).click();
  const delivery = dispatch.getByTestId("delivery-status");
  await expect(delivery).toHaveText("Planificado");
  const deliveryNo = ((await dispatch.getByRole("heading", { level: 1 }).innerText()).match(/CD-\d+/) ?? [""])[0];
  await dispatch.getByLabel("Placa del cliente").fill("G765432");
  await dispatch.getByLabel("Chofer del cliente").fill("Luis Peña");
  await dispatch.getByRole("button", { name: "Iniciar carga" }).click();
  await dispatch.getByRole("button", { name: "Confirmar carga" }).click();
  await expect(delivery).toHaveText("Cargado");
  await dispatch.getByLabel("Peso bruto (kg)").fill("4000");
  await dispatch.getByLabel("Tara (kg)").fill("3700");
  await attachEvidence(dispatch, "Ticket de báscula", "ticket-ecf.jpg");
  await dispatch.getByRole("button", { name: "Registrar pesada y salida" }).click();
  await expect(delivery).toHaveText("Entregado");

  // Facturación issues the invoice: it takes the range's first e-NCF and the worker sends it.
  const billing = await signIn(browser, "Facturación");
  await nav(billing, "Por facturar");
  await billing.getByRole("checkbox", { name: `Facturar ${deliveryNo} BLOQUE-6` }).check();
  await billing.getByRole("button", { name: "Crear factura con 1 línea(s)" }).click();
  await confirmAction(billing, "Emitir factura");
  await expect(billing.getByTestId("invoice-fiscal-status")).toHaveText("e-CF en envío");
  await expect(billing.getByTestId("ecf-stamp")).toContainText(firstEncf);
  // The simulated DGII answers at the first status query (10 s after sending).
  await expect(async () => {
    await billing.reload();
    await expect(billing.getByTestId("invoice-fiscal-status")).toHaveText("e-CF aceptado", { timeout: 1_000 });
  }).toPass({ timeout: 60_000, intervals: [2_000] });
  await expect(billing.getByTestId("ecf-security-code")).not.toBeEmpty();
  await expect(billing.getByTestId("ecf-qr")).toBeVisible();

  const invoiceUrl = billing.url();
  await billing.getByRole("link", { name: "Imprimir factura" }).click();
  await expect(billing.getByTestId("print-encf")).toHaveText(firstEncf);
  await expect(billing.getByTestId("watermark")).toHaveCount(0);
  await expect(billing.getByTestId("print-ecf").getByTestId("ecf-qr")).toBeVisible();
  await billing.goto(invoiceUrl);

  // E-VS4-05-1: the accepted invoice goes by e-mail (Redirect in the dev stack), once the signed files are kept.
  const mail = billing.getByTestId("document-mail");
  await expect(async () => {
    await billing.reload();
    await mail.getByRole("button", { name: "Enviar por correo" }).click({ timeout: 1_000 });
    await mail.getByLabel("Otros correos").fill("compras@constructorauno.test", { timeout: 1_000 });
    await submit(billing, "Enviar correo");
    await expect(mail.getByTestId("mail-history").locator("tbody tr").first().getByTestId("mail-status")).toHaveText("Enviado", { timeout: 5_000 });
  }).toPass({ timeout: 45_000, intervals: [2_000] });

  // The inbox shows it accepted, with its calls and the signed files.
  await nav(specialist, "e-CF");
  await specialist.getByLabel("Buscar", { exact: true }).fill(firstEncf);
  await specialist.getByRole("button", { name: "Buscar" }).click();
  await specialist.getByRole("link", { name: firstEncf }).click();
  await expect(specialist.getByTestId("ecf-detail-status")).toHaveText("Aceptado");
  await expect(specialist.getByTestId("ecf-calls")).toContainText("Envío");
  await expect(specialist.getByTestId("ecf-file:XML")).toBeVisible();

  // The Controller closes the range and annuls the 999 numbers it will never use (E-VS4-12, ECF-10).
  await controller.reload();
  await confirmAction(controllerRow, "Cerrar");
  await expect(controllerRow).toContainText("Cerrado");
  await expect(controllerRow).toContainText("999");
  await confirmAction(controllerRow, "Anular números sin usar");
  await expect(controllerRow).toContainText("Anulado");
});
