import { expect, test } from "@playwright/test";
import { confirmAction, nav, signIn } from "./support";

// FIN1-04 (E-FIN1-04-11): the Contador prepares and submits an adjustment, the Controller approves it, and the Contador sees a
// balanced trial balance, downloads it as CSV and opens the statements — each actor signs in through the simulated Google sign-in.


test("an adjustment from the Contador to the trial balance and the statements", async ({ browser }) => {
  const contador = await signIn(browser, "Contador");
  await nav(contador, "Diario de ajustes");
  await contador.getByRole("link", { name: "Nuevo ajuste" }).click();
  await contador.getByLabel("Descripción").fill("Provisión de energía de septiembre");
  await contador.getByLabel("Archivo del soporte").setInputFiles({ name: "factura-ede.pdf", mimeType: "application/pdf", buffer: Buffer.from("factura EDE septiembre") });
  await expect(contador.getByTestId("support-hash")).toContainText("SHA-256");
  await contador.getByLabel("Cuenta 1").selectOption({ label: "6200 — Energía eléctrica" });
  await contador.getByLabel("Monto 1").fill("1,250.00");
  await contador.getByLabel("Cuenta 2").selectOption({ label: "2200 — Gastos acumulados por pagar" });
  await contador.getByLabel("Lado 2").selectOption("credit");
  await contador.getByLabel("Monto 2").fill("1250");
  await contador.getByRole("button", { name: "Guardar borrador" }).click();
  await expect(contador.getByTestId("journal-status")).toHaveText("Borrador");
  await expect(contador.getByTestId("journal-difference")).toHaveText("0.00");
  await contador.getByRole("button", { name: "Enviar a aprobación" }).click();
  await expect(contador.getByTestId("journal-status")).toHaveText("Pendiente de aprobación");
  await expect(contador.getByRole("button", { name: "Aprobar y contabilizar" })).toHaveCount(0);
  const journalUrl = contador.url();

  const controller = await signIn(browser, "Controller");
  await controller.goto(journalUrl);
  await confirmAction(controller, "Aprobar y contabilizar");
  await expect(controller.getByTestId("journal-status")).toHaveText("Contabilizado");

  await nav(contador, "Balanza");
  await expect(contador.getByTestId("trial-balance-status")).toHaveText("Cuadra");
  await expect(contador.getByRole("link", { name: "Energía eléctrica" })).toBeVisible();
  const download = contador.waitForEvent("download");
  await contador.getByRole("link", { name: "Descargar CSV" }).click();
  expect((await download).suggestedFilename()).toMatch(/^balanza-\d{8}-\d{8}\.csv$/);

  await contador.getByRole("link", { name: "Energía eléctrica" }).click();
  await expect(contador.getByRole("heading", { name: "Mayor por cuenta" })).toBeVisible();
  await expect(contador.getByRole("cell", { name: /Ajuste AJ-/ })).toBeVisible();

  await nav(contador, "Estados financieros");
  await expect(contador.getByTestId("balance-status")).toHaveText("Cuadra");
  await expect(contador.getByTestId("balance-difference")).toHaveText("0.00");
  await contador.getByRole("tab", { name: "Estado de resultados" }).click();
  await expect(contador.getByTestId("net-income")).toBeVisible();
});
