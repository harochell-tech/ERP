import { expect, test } from "@playwright/test";
import { expectFits, nav, pick, signIn, submit } from "./support";

// MAIL-03 (E-MAIL-5, 6, 7, E-MAIL-01-4, 6, 8): documents by e-mail through the UI. The dev stack runs mail in Redirect mode with a
// transport that only records, so nothing leaves. The Vendedor sends the seeded quote to the customer's saved e-mails (one
// unticked) plus a typed one, with a message; the sending shows «Enviado», redirected to the internal mailbox, and its PDF is
// downloaded. Cobros sends the customer's statement of account; the Vendedor is not offered that.

test("a quote and a statement of account sent by e-mail, redirected in a test environment", async ({ browser }) => {
  const seller = await signIn(browser, "Vendedor");
  await nav(seller, "Cotizaciones");
  await seller.getByRole("link", { name: /^COT-/ }).first().click();
  await expect(seller.getByTestId("quote-status")).toHaveText("Enviada");
  const quoteNo = ((await seller.getByRole("heading", { level: 1 }).innerText()).match(/COT-[\d-]+/) ?? [""])[0];
  expect(quoteNo).not.toBe("");
  const mail = seller.getByTestId("document-mail");
  await mail.getByRole("button", { name: "Enviar por correo" }).click();
  await expect(mail.getByTestId("mail-mode-notice")).toContainText("no llega al cliente");
  await expect(mail.getByRole("checkbox", { name: "compras@constructorauno.test" })).toBeChecked();
  await mail.getByRole("checkbox", { name: "obra@constructorauno.test" }).uncheck();

  // A malformed address is caught before sending.
  await mail.getByLabel("Otros correos").fill("gerencia@constructorauno");
  await submit(seller, "Enviar correo");
  await expect(mail.getByText("Revise esta dirección: gerencia@constructorauno.")).toBeVisible();
  await mail.getByLabel("Otros correos").fill("Gerencia@ConstructoraUno.test");
  await mail.getByLabel("Mensaje del correo").fill("Quedamos atentos a su orden de compra.");
  await submit(seller, "Enviar correo");

  const row = mail.getByTestId("mail-history").locator("tbody tr").first();
  await expect(row).toContainText("compras@constructorauno.test, gerencia@constructorauno.test");
  await expect(row.getByTestId("mail-status")).toHaveText("Enviado");
  await expect(row).toContainText("Redirigido a industrias@dev.rochell.test (no llegó al cliente)");
  const download = seller.waitForEvent("download");
  await row.getByRole("button", { name: "Descargar PDF" }).click();
  expect((await download).suggestedFilename()).toBe(`${quoteNo}.pdf`);
  await expectFits(seller);

  // The Vendedor reads the statement of account but is not offered to send it.
  await nav(seller, "Estado de cuenta");
  await pick(seller.getByLabel("Cliente", { exact: true }), "Constructora Uno");
  await expect(seller.getByTestId("statement-closing")).toBeVisible();
  await expect(seller.getByRole("button", { name: "Enviar estado de cuenta por correo" })).toHaveCount(0);

  // Cobros sends it to the saved e-mails.
  const cobros = await signIn(browser, "Cobros");
  await nav(cobros, "Estado de cuenta");
  await pick(cobros.getByLabel("Cliente", { exact: true }), "Constructora Uno");
  await cobros.getByRole("button", { name: "Enviar estado de cuenta por correo" }).click();
  await submit(cobros, "Enviar correo");
  const sent = cobros.getByTestId("mail-history").first().locator("tbody tr").first();
  await expect(sent).toContainText("compras@constructorauno.test, obra@constructorauno.test");
  await expect(sent.getByTestId("mail-status")).toHaveText("Enviado");
  await expectFits(cobros);
});
