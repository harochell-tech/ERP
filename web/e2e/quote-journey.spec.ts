import { expect, test } from "@playwright/test";
import { confirmAction, nav, signIn, submit } from "./support";

// QUO1-04 (E-QUO1-04-9): a quote with a special price through the UI. The Vendedor quotes Constructora Uno 37 BLOQUE-6 at 45.00
// (the list is 50.00) and submits it for price approval; the Aprobador de políticas sees it counted on Inicio and approves (step-up:
// the fresh sign-in counts); the Vendedor sends it, opens the print view (ITBIS and total from the server), converts it into an
// order and lands on the order, which reads "Desde cotización COT-…" at the quoted price; the credit check confirms it. The journey
// opens its own quote by URL and uses a quantity no other journey uses, so other journeys' data (and the seeded sample quote) never
// matters.


test("a quote with a special price approved, sent, printed and converted into an order", async ({ browser }) => {
  // The Vendedor creates the quote below the list price and submits it for approval.
  const seller = await signIn(browser, "Vendedor");
  await nav(seller, "Cotizaciones");
  await expect(seller.getByRole("heading", { name: "Cotizaciones" })).toBeVisible();
  await seller.getByRole("link", { name: "Nueva cotización" }).click();
  // The list has a "Cliente" filter too: wait for the form before choosing the customer.
  await expect(seller.getByRole("heading", { name: "Nueva cotización" })).toBeVisible();
  await seller.getByLabel("Cliente", { exact: true }).selectOption({ label: "Constructora Uno (131925332)" });
  await seller.getByLabel("Referencia del cliente (opcional)").fill("Obra E2E cotización");
  await seller.getByLabel("Producto 1").selectOption({ label: "BLOQUE-6 — Bloque de 6 pulgadas (un)" });
  await expect(seller.getByTestId("list-price:1")).toHaveText("50.00");
  await seller.getByLabel("Cantidad 1").fill("37");
  await seller.getByLabel("Precio 1").fill("45.00");
  await expect(seller.getByTestId("special-price:1")).toHaveText("Precio especial: requiere aprobación");
  // UX4-03 (V-11, E-UX4-3): the server prices the draft at the quoted 45.00 while it is typed (37 × 45.00, ITBIS 18 %).
  await expect(seller.getByTestId("preview-line-net:1")).toHaveText("1,665.00");
  await expect(seller.getByTestId("preview-net")).toHaveText("1,665.00");
  await expect(seller.getByTestId("preview-itbis")).toHaveText("299.70");
  await expect(seller.getByTestId("preview-total")).toHaveText("1,964.70");
  await submit(seller, "Crear cotización");
  const status = seller.getByTestId("quote-status");
  await expect(status).toHaveText("Borrador");
  await expect(seller.getByTestId("quote-total")).toHaveText("1,665.00");
  await expect(seller.getByTestId("quote-price:1")).toHaveText("45.00");
  await expect(seller.getByRole("button", { name: "Marcar enviada al cliente" })).toHaveCount(0);
  const quoteNo = ((await seller.getByRole("heading", { level: 1 }).innerText()).match(/COT-\d+/) ?? [""])[0];
  expect(quoteNo).not.toBe("");
  const quoteUrl = seller.url();
  // UX3-02 (E-UX3-10): a draft prints with the diagonal "BORRADOR".
  await seller.getByRole("link", { name: "Imprimir cotización" }).click();
  await expect(seller.getByTestId("watermark")).toHaveText("BORRADOR");
  await seller.goto(quoteUrl);
  await seller.getByRole("button", { name: "Enviar a aprobación de precios" }).click();
  await expect(status).toHaveText("Pendiente de aprobación");
  await expect(seller.getByRole("button", { name: "Aprobar precios" })).toHaveCount(0);

  // The Aprobador de políticas sees the counter on Inicio and approves the prices (step-up: the fresh sign-in counts).
  const approver = await signIn(browser, "Aprobador de políticas contables");
  const task = approver.getByRole("link", { name: "Precios de cotización por aprobar" });
  await expect(task).toBeVisible();
  await expect(approver.getByTestId("task-count:/ventas/cotizaciones/?estado=PENDING_APPROVAL")).not.toHaveText("0");
  await task.click();
  await expect(approver.getByRole("link", { name: quoteNo })).toBeVisible();
  await approver.goto(quoteUrl);
  await confirmAction(approver, "Aprobar precios");
  await expect(approver.getByTestId("quote-status")).toHaveText("Borrador");
  await expect(approver.getByTestId("quote-approval")).toContainText("cubre las líneas actuales");

  // The Vendedor sends it and opens the print view: ITBIS and total are the server's.
  await seller.reload();
  await expect(seller.getByTestId("quote-approval")).toContainText("cubre las líneas actuales");
  await seller.getByRole("button", { name: "Marcar enviada al cliente" }).click();
  await expect(status).toHaveText("Enviada");
  await seller.getByRole("link", { name: "Imprimir cotización" }).click();
  await expect(seller.getByRole("heading", { name: `Cotización ${quoteNo}` })).toBeVisible();
  await expect(seller.getByTestId("print-customer")).toContainText("Constructora Uno");
  await expect(seller.getByTestId("print-net")).toHaveText("1,665.00");
  await expect(seller.getByTestId("print-itbis")).toHaveText("299.70");
  await expect(seller.getByTestId("print-total")).toHaveText("1,964.70");
  await expect(seller.getByTestId("print-conditions")).toContainText("Documento no fiscal");
  await expect(seller.getByTestId("print-conditions")).not.toContainText("X-Q1");
  await expect(seller.getByTestId("watermark")).toHaveCount(0); // sent and valid: no watermark
  await expect(seller.getByRole("button", { name: "Imprimir" })).toBeVisible();

  // Converted into an order at the quoted price; the credit check confirms it.
  await seller.goto(quoteUrl);
  await confirmAction(seller, "Convertir en pedido");
  await expect(seller.getByTestId("order-status")).toHaveText("Borrador");
  await expect(seller.getByTestId("order-quote")).toHaveText(`Desde cotización ${quoteNo}`);
  await expect(seller.locator("tr", { hasText: "BLOQUE-6" })).toContainText("45.00");
  await expect(seller.getByTestId("order-total")).toHaveText("1,665.00");
  await expect(seller.getByTestId("credit-preview-headline")).toContainText("Cabe en el crédito disponible");
  await seller.getByRole("button", { name: "Enviar a crédito" }).click();
  await expect(seller.getByTestId("order-status")).toHaveText("Confirmado");
  const orderNo = ((await seller.getByRole("heading", { level: 1 }).innerText()).match(/PV-\d+/) ?? [""])[0];

  // The quote reads CONVERTED and links the order.
  await seller.getByRole("link", { name: quoteNo }).click();
  await expect(status).toHaveText("Convertida en pedido");
  await expect(seller.getByTestId("quote-order")).toHaveText(orderNo);
});
