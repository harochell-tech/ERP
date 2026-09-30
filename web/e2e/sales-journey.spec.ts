import { expect, test, type Page } from "@playwright/test";
import { confirmAction, expectFits, nav, signIn, submit } from "./support";

// VS#3 E2E-S1 through the UI (VS3-10b, E-VS3-10-10): the Vendedor creates the order (credit auto-approved), Despacho loads it on our
// truck, weighs it out and records the POD, Facturación invoices it and records the e-CF, Cobros records the transfer and applies
// it, and the treasurer matches the bank's credit line — each actor signs in through the (simulated) Google sign-in. Receipts use
// the second company bank account (TEST_BANK ••••4321) so the treasury journey's account stays apart.


function dominicanNow(offsetMinutes = 0): { date: string; dateTime: string } {
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

test("sales order to a reconciled receipt (E2E-S1)", async ({ browser }) => {
  // Order: 100 blocks at 50.00, delivered at the site with our truck; the credit check approves it.
  const seller = await signIn(browser, "Vendedor");
  await nav(seller, "Pedidos");
  await seller.getByRole("link", { name: "Nuevo pedido" }).click();
  await seller.getByLabel("Cliente", { exact: true }).selectOption({ label: "Constructora Uno (131925332)" });
  await seller.getByLabel("Término de entrega").selectOption("DELIVERED_OWN_TRANSPORT");
  await seller.getByLabel("Dirección de la obra").fill("Obra Punta Cana");
  await seller.getByLabel("Producto 1").selectOption({ label: "BLOQUE-6 — Bloque de 6 pulgadas (un)" });
  await seller.getByLabel("Cantidad 1").fill("100");
  await submit(seller, "Crear pedido");
  await expect(seller.getByTestId("order-total")).toHaveText("5,000.00");
  await seller.getByRole("button", { name: "Enviar a crédito" }).click();
  await expect(seller.getByTestId("order-status")).toHaveText("Confirmado");

  // Dispatch: plan, load on our truck, weigh and gate out, POD.
  const dispatch = await signIn(browser, "Despacho");
  await nav(dispatch, "Tablero de despacho");
  await dispatch.getByRole("link", { name: "Planificar conduce" }).first().click();
  await dispatch.getByLabel("A despachar BLOQUE-6").fill("100");
  await submit(dispatch, "Planificar conduce");
  const status = dispatch.getByTestId("delivery-status");
  await expect(status).toHaveText("Planificado");
  await dispatch.getByLabel("Camión").selectOption({ label: "L123456 (12,000 kg)" });
  await dispatch.getByLabel("Chofer").selectOption({ label: "Juan Pérez" });
  await dispatch.getByRole("button", { name: "Iniciar carga" }).click();
  await expect(status).toHaveText("Cargando");
  await dispatch.getByRole("button", { name: "Confirmar carga" }).click();
  await expect(status).toHaveText("Cargado");
  // UX3-02 (E-UX3-7): the printable conduce carries "BORRADOR – NO DESPACHADO" until the truck goes out of the gate.
  const deliveryUrl = dispatch.url();
  await dispatch.getByRole("link", { name: "Imprimir conduce" }).click();
  await expect(dispatch.getByTestId("watermark")).toHaveText("BORRADOR – NO DESPACHADO");
  await expect(dispatch.getByTestId("conduce-customer")).toContainText("Constructora Uno");
  await expectFits(dispatch);
  await dispatch.goto(deliveryUrl);
  await dispatch.getByLabel("Peso bruto (kg)").fill("9000");
  await dispatch.getByLabel("Tara (kg)").fill("8000");
  await attachEvidence(dispatch, "Ticket de báscula", "ticket-bascula.jpg");
  await dispatch.getByRole("button", { name: "Registrar pesada y salida" }).click();
  await expect(status).toHaveText("En tránsito");
  await dispatch.getByRole("link", { name: "Imprimir conduce" }).click();
  await expect(dispatch.getByTestId("conduce-net")).toHaveText("1,000");
  await expect(dispatch.getByTestId("watermark")).toHaveCount(0);
  await dispatch.goto(deliveryUrl);
  await dispatch.getByLabel("Recibió (nombre)").fill("Ing. María Gómez");
  await dispatch.getByLabel("Fecha y hora de recepción").fill(dominicanNow(-1).dateTime);
  await attachEvidence(dispatch, "Evidencia del POD (foto o firma)", "pod-firma.jpg");
  await dispatch.getByRole("button", { name: "Registrar entrega (POD)" }).click();
  await expect(status).toHaveText("Entregado");
  await expect(dispatch.getByTestId("pod-evidence")).toHaveText("Evidencia: pod-firma.jpg (huella verificada)");

  // Billing: invoice what was delivered (5,000.00 + 18 % ITBIS), then the e-CF from the provider's portal.
  const billing = await signIn(browser, "Facturación");
  await nav(billing, "Por facturar");
  await billing.getByRole("checkbox", { name: /^Facturar CD-\d+ BLOQUE-6$/ }).first().check();
  await billing.getByRole("button", { name: "Crear factura con 1 línea(s)" }).click();
  await confirmAction(billing, "Emitir factura");
  await expect(billing.getByTestId("invoice-status")).toHaveText("Confirmado");
  await expect(billing.getByTestId("invoice-total")).toHaveText("5,900.00");
  await billing.getByLabel("e-NCF", { exact: true }).fill("E310000000001");
  await billing.getByLabel("Emitido el").fill(dominicanNow(-1).dateTime);
  await billing.getByLabel("Código de seguridad").fill("A1B2C3");
  await attach(billing, "XML o PDF del e-CF", "e-cf-FA.xml");
  await billing.getByLabel("RNC del receptor (según el portal)").fill("131925332");
  await billing.getByLabel("Neto (según el portal)").fill("5000.00");
  await billing.getByLabel("ITBIS (según el portal)").fill("900.00");
  await billing.getByLabel("Total (según el portal)").fill("5900.00");
  await billing.getByRole("button", { name: "Registrar e-CF" }).click();
  await expect(billing.getByTestId("invoice-fiscal-status")).toHaveText("e-CF aceptado");
  // UX3-02 (E-UX3-9): a credit note drafted on the invoice cannot be issued by who issued the invoice — the button is not offered.
  // It stays a draft (no accounting effect), so the receipt below still pays the full invoice.
  const invoiceUrl = billing.url();
  await billing.getByLabel("Acreditar línea 1").fill("100.00");
  await billing.getByLabel("Explicación").fill("Descuento por volumen (E2E)");
  await submit(billing, "Crear nota de crédito");
  await billing.getByRole("link", { name: /^NC-/ }).first().click();
  await expect(billing.getByTestId("credit-note-own-invoice")).toBeVisible();
  await expect(billing.getByRole("button", { name: "Emitir nota de crédito" })).toHaveCount(0);
  await expect(billing.getByText("Motivo: Descuento")).toBeVisible();
  await billing.goto(invoiceUrl);

  // Cobros: the customer's transfer to the receipts' account, applied to the invoice.
  const cobros = await signIn(browser, "Cobros");
  await nav(cobros, "Recibos");
  await cobros.getByRole("link", { name: "Registrar cobro" }).click();
  await cobros.getByLabel("Cliente", { exact: true }).selectOption({ label: "Constructora Uno (131925332)" });
  await cobros.getByLabel("Monto del cobro").fill("5900.00");
  await cobros.getByLabel("Cuenta bancaria").selectOption({ label: "TEST_BANK ••••4321" });
  await submit(cobros, "Registrar cobro");
  await expect(cobros.getByTestId("receipt-application")).toHaveText("Sin aplicar");
  await cobros.getByLabel(/^Aplicar a FA-/).first().fill("5900.00");
  await submit(cobros, "Aplicar cobro");
  await expect(cobros.getByTestId("receipt-application")).toHaveText("Aplicado");
  await expect(cobros.getByTestId("receipt-unapplied")).toHaveText("0.00");

  // Treasury: the bank's statement shows the transfer; the treasurer matches it to the receipt and BANK-GL has no difference.
  const day = dominicanNow().date;
  const [year, month, dd] = day.split("-");
  const csv = `Fecha,Referencia,Descripcion,Debito,Credito\n${dd}/${month}/${year},TRF-77,Transferencia Constructora Uno,,5900.00\n`;
  const treasurer = await signIn(browser, "Tesorero");
  await nav(treasurer, "Extractos bancarios");
  await treasurer.getByLabel("Cuenta bancaria").selectOption({ label: "TEST_BANK ••••4321" });
  await treasurer.getByLabel("Archivo del banco").setInputFiles({ name: "extracto-cobros.csv", mimeType: "text/csv", buffer: Buffer.from(csv) });
  await treasurer.getByLabel("Desde").fill(day);
  await treasurer.getByLabel("Hasta").fill(day);
  await treasurer.getByLabel("Saldo inicial").fill("0.00");
  await treasurer.getByLabel("Saldo final").fill("5900.00");
  await submit(treasurer, "Importar");
  await expect(treasurer.getByTestId("import-result")).toContainText("Importadas 1 de 1");
  await treasurer.getByRole("link", { name: "Conciliar este extracto" }).click();
  await treasurer.getByRole("button", { name: "Buscar cobros" }).click();
  await treasurer.getByRole("button", { name: /^Conciliar con cobro por transferencia REC-\d+/ }).click();
  await expect(treasurer.getByRole("tab", { name: "Conciliadas (1)" })).toBeVisible();
  await expect(treasurer.getByTestId("bank-gl-difference")).toHaveText("0.00");
});
