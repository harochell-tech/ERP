import { expect, test, type Browser, type Page } from "@playwright/test";

// VS#3 E2E-S1 through the UI (VS3-10b, E-VS3-10-10): the Vendedor creates the order (credit auto-approved), Despacho loads it on our
// truck, weighs it out and records the POD, Facturación invoices it and records the e-CF, Cobros records the transfer and applies
// it, and the treasurer matches the bank's credit line — each actor signs in through the (simulated) Google sign-in. Receipts use
// the second company bank account (TEST_BANK ••••4321) so the treasury journey's account stays apart.

async function signIn(browser: Browser, account: string): Promise<Page> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto("/");
  await page.getByRole("link", { name: "Iniciar sesión" }).click();
  await page.getByRole("link", { name: account, exact: true }).click();
  await expect(page.getByTestId("user-email")).toBeVisible();
  return page;
}

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

test("sales order to a reconciled receipt (E2E-S1)", async ({ browser }) => {
  // Order: 100 blocks at 50.00, delivered at the site with our truck; the credit check approves it.
  const seller = await signIn(browser, "Vendedor");
  await seller.getByRole("link", { name: "Pedidos", exact: true }).click();
  await seller.getByRole("link", { name: "Nuevo pedido" }).click();
  await seller.getByLabel("Cliente", { exact: true }).selectOption({ label: "Constructora Uno (131925332)" });
  await seller.getByLabel("Término de entrega").selectOption("DELIVERED_OWN_TRANSPORT");
  await seller.getByLabel("Dirección de la obra").fill("Obra Punta Cana");
  await seller.getByLabel("Producto 1").selectOption({ label: "BLOQUE-6 — Bloque de 6 pulgadas (un)" });
  await seller.getByLabel("Cantidad 1").fill("100");
  await seller.getByRole("button", { name: "Crear pedido" }).click();
  await expect(seller.getByTestId("order-total")).toHaveText("5,000.00");
  await seller.getByRole("button", { name: "Enviar a crédito" }).click();
  await expect(seller.getByTestId("order-status")).toHaveText("Confirmado");

  // Dispatch: plan, load on our truck, weigh and gate out, POD.
  const dispatch = await signIn(browser, "Despacho");
  await dispatch.getByRole("link", { name: "Tablero de despacho" }).click();
  await dispatch.getByRole("link", { name: "Planificar conduce" }).first().click();
  await dispatch.getByLabel("A despachar BLOQUE-6").fill("100");
  await dispatch.getByRole("button", { name: "Planificar conduce" }).click();
  const status = dispatch.getByTestId("delivery-status");
  await expect(status).toHaveText("Planificado");
  await dispatch.getByLabel("Camión").selectOption({ label: "L123456 (12,000 kg)" });
  await dispatch.getByLabel("Chofer").selectOption({ label: "Juan Pérez" });
  await dispatch.getByRole("button", { name: "Iniciar carga" }).click();
  await expect(status).toHaveText("Cargando");
  await dispatch.getByRole("button", { name: "Confirmar carga" }).click();
  await expect(status).toHaveText("Cargado");
  await dispatch.getByLabel("Peso bruto (kg)").fill("9000");
  await dispatch.getByLabel("Tara (kg)").fill("8000");
  await attach(dispatch, "Ticket de báscula", "ticket-bascula.jpg");
  await dispatch.getByRole("button", { name: "Registrar pesada y salida" }).click();
  await expect(status).toHaveText("En tránsito");
  await dispatch.getByLabel("Recibió (nombre)").fill("Ing. María Gómez");
  await dispatch.getByLabel("Fecha y hora de recepción").fill(dominicanNow(-1).dateTime);
  await attach(dispatch, "Evidencia del POD (foto o firma)", "pod-firma.jpg");
  await dispatch.getByRole("button", { name: "Registrar entrega (POD)" }).click();
  await expect(status).toHaveText("Entregado");

  // Billing: invoice what was delivered (5,000.00 + 18 % ITBIS), then the e-CF from the provider's portal.
  const billing = await signIn(browser, "Facturación");
  await billing.getByRole("link", { name: "Por facturar" }).click();
  await billing.getByRole("checkbox", { name: /^Facturar CD-\d+ BLOQUE-6$/ }).first().check();
  await billing.getByRole("button", { name: "Crear factura con 1 línea(s)" }).click();
  await billing.getByRole("button", { name: "Emitir factura" }).click();
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

  // Cobros: the customer's transfer to the receipts' account, applied to the invoice.
  const cobros = await signIn(browser, "Cobros");
  await cobros.getByRole("link", { name: "Recibos", exact: true }).click();
  await cobros.getByRole("link", { name: "Registrar cobro" }).click();
  await cobros.getByLabel("Cliente", { exact: true }).selectOption({ label: "Constructora Uno (131925332)" });
  await cobros.getByLabel("Monto del cobro").fill("5900.00");
  await cobros.getByLabel("Cuenta bancaria").selectOption({ label: "TEST_BANK ••••4321" });
  await cobros.getByRole("button", { name: "Registrar cobro" }).click();
  await expect(cobros.getByTestId("receipt-application")).toHaveText("Sin aplicar");
  await cobros.getByLabel(/^Aplicar a FA-/).first().fill("5900.00");
  await cobros.getByRole("button", { name: "Aplicar cobro" }).click();
  await expect(cobros.getByTestId("receipt-application")).toHaveText("Aplicado");
  await expect(cobros.getByTestId("receipt-unapplied")).toHaveText("0.00");

  // Treasury: the bank's statement shows the transfer; the treasurer matches it to the receipt and BANK-GL has no difference.
  const day = dominicanNow().date;
  const [year, month, dd] = day.split("-");
  const csv = `Fecha,Referencia,Descripcion,Debito,Credito\n${dd}/${month}/${year},TRF-77,Transferencia Constructora Uno,,5900.00\n`;
  const treasurer = await signIn(browser, "Tesorero");
  await treasurer.getByRole("link", { name: "Extractos bancarios" }).click();
  await treasurer.getByLabel("Cuenta bancaria").selectOption({ label: "TEST_BANK ••••4321" });
  await treasurer.getByLabel("Archivo del banco").setInputFiles({ name: "extracto-cobros.csv", mimeType: "text/csv", buffer: Buffer.from(csv) });
  await treasurer.getByLabel("Desde").fill(day);
  await treasurer.getByLabel("Hasta").fill(day);
  await treasurer.getByLabel("Saldo inicial").fill("0.00");
  await treasurer.getByLabel("Saldo final").fill("5900.00");
  await treasurer.getByRole("button", { name: "Importar" }).click();
  await expect(treasurer.getByTestId("import-result")).toContainText("Importadas 1 de 1");
  await treasurer.getByRole("link", { name: "Conciliar este extracto" }).click();
  await treasurer.getByRole("button", { name: "Buscar cobros" }).click();
  await treasurer.getByRole("button", { name: /^Conciliar con cobro por transferencia REC-\d+/ }).click();
  await expect(treasurer.getByRole("tab", { name: "Conciliadas (1)" })).toBeVisible();
  await expect(treasurer.getByTestId("bank-gl-difference")).toHaveText("0.00");
});
