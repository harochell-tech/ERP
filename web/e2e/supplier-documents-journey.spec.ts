import { expect, test, type Page } from "@playwright/test";
import { confirmAction, expectFits, nav, pick, signIn, submit } from "./support";

// OCR1-03 (E-OCR1-03-10): supplier documents through the UI. «Agregados del Este» sent an e-CF of cement and a credit note through the
// (simulated) Alanube; the worker read them. Cuentas por pagar sees them on Inicio and in Compras › Comprobantes recibidos, passes the
// invoice to an expense invoice already filled from its XML, posts it — which accepts the e-CF before the DGII — and captures a printed
// e-CF by pasting its QR's link. The credit note stays marked «sin registro en Core».

function today(): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
}

/** The inbox's row of a document, reloading while the worker has not read it yet. */
async function row(page: Page, fiscalNumber: string) {
  const found = page.getByTestId(`document-row-${fiscalNumber}`);
  await expect(async () => {
    await page.reload();
    await expect(found).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 60_000 });
  return found;
}

test("a received e-CF becomes an expense invoice, posting accepts it, and a QR is captured (OCR1-03)", async ({ browser }) => {
  test.setTimeout(240_000);
  const payables = await signIn(browser, "Cuentas por pagar");
  await expect(payables.getByTestId("tasks").getByRole("link", { name: /Comprobantes recibidos por registrar/ })).toBeVisible();

  await nav(payables, "Comprobantes recibidos");
  await expect(payables.getByRole("heading", { name: "Comprobantes recibidos" })).toBeVisible();
  const invoiceRow = await row(payables, "E310000000501");
  await expect(invoiceRow).toContainText("Agregados del Este");
  await expect(invoiceRow).toContainText("5,900.00");
  await expect(invoiceRow).toContainText("XML");
  await expect(invoiceRow).toContainText("Sin responder");
  await expectFits(payables);

  // The credit note: no «Pasar a factura» (E-OCR1-01-3).
  await payables.getByRole("link", { name: "E340000000502" }).click();
  await expect(payables.getByTestId("check-NOTE_NOT_REGISTERED")).toBeVisible();
  await expect(payables.getByRole("link", { name: /Pasar a factura/ })).toHaveCount(0);
  await payables.goBack();

  // The invoice: its lines from the XML, then «Pasar a factura de gastos» with the form filled in.
  await payables.getByRole("link", { name: "E310000000501" }).click();
  await expect(payables.getByTestId("document-lines")).toContainText("Cemento gris 42.5 kg");
  await expect(payables.getByTestId("document-total")).toHaveText("5,900.00");
  await payables.getByRole("link", { name: "Pasar a factura de gastos" }).click();
  await expect(payables.getByTestId("from-document")).toContainText("E310000000501");
  await expect(payables.getByLabel("NCF")).toHaveValue("E310000000501");
  await expect(payables.getByLabel("Descripción 1")).toHaveValue("Cemento gris 42.5 kg");
  await expect(payables.getByLabel("Cantidad 1")).toHaveValue("10");
  await expect(payables.getByLabel("Precio 1")).toHaveValue("500");
  await payables.getByLabel("Planta").selectOption({ index: 1 });
  await payables.getByLabel("Vence").fill(today());
  await pick(payables.getByLabel("Categoría 1"), "Reparaciones");
  await payables.getByLabel("Tipo de impuesto 1").selectOption({ label: "ITBIS 18 %" });
  await expect(payables.getByTestId("expense-preview-total")).toHaveText("5,900.00");
  await submit(payables, "Registrar y cotejar");
  await expect(payables).toHaveURL(/\/cxp\/factura\/\?id=/);
  await confirmAction(payables, "Contabilizar");
  await expect(payables.getByTestId("accounting-status")).toContainText("Contabilizado");

  // Posting accepted the e-CF before the DGII (E-OCR1-02-4); the worker sends it to Alanube.
  await payables.goto("/compras/comprobantes/?estado=REGISTERED");
  const registered = await row(payables, "E310000000501");
  await expect(registered).toContainText("Registrado");
  await registered.getByRole("link", { name: "E310000000501" }).click();
  await expect(payables.getByTestId("document-response")).toContainText("Aceptado");
  await expect(payables.getByTestId("document-invoice")).toBeVisible();

  // A printed e-CF: its QR's link pasted (on a phone the camera reads it).
  const encf = `E31${String(Date.now()).slice(-10)}`;
  await nav(payables, "Comprobantes recibidos");
  await payables.getByRole("button", { name: "Escanear QR" }).click();
  await expect(payables.getByTestId("qr-scan")).toBeVisible();
  await payables
    .getByLabel("Enlace del QR")
    .fill(
      `https://ecf.dgii.gov.do/eCF/ConsultaTimbre?RncEmisor=101000011&RncComprador=131925332&ENCF=${encf}&FechaEmision=09-10-2026&MontoTotal=2360.00&FechaFirma=09-10-2026%2009:00:00&CodigoSeguridad=Qr1234`,
    );
  await payables.getByRole("button", { name: "Usar enlace" }).click();
  await expect(payables).toHaveURL(/\/compras\/comprobante\/\?id=/);
  await expect(payables.getByRole("heading", { name: `Crédito fiscal ${encf}` })).toBeVisible();
  await expect(payables.getByTestId("document-total")).toHaveText("2,360.00");
  await expect(payables.getByRole("link", { name: "Verificar en la DGII" })).toHaveAttribute("href", /ConsultaTimbre/);
  await expectFits(payables);
});
