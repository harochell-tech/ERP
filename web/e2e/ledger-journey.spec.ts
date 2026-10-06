import { expect, test } from "@playwright/test";
import { confirmAction, nav, pick, signIn } from "./support";

// FIN1-04 (E-FIN1-04-11): the Contador prepares and submits an adjustment, the Controller approves it, and the Contador sees a
// balanced trial balance, downloads it as CSV and opens the statements — each actor signs in through the simulated Google sign-in.


test("an adjustment from the Contador to the trial balance and the statements", async ({ browser }) => {
  const contador = await signIn(browser, "Contador");
  await nav(contador, "Diario de ajustes");
  await contador.getByRole("link", { name: "Nuevo ajuste" }).click();
  await contador.getByLabel("Descripción").fill("Provisión de energía de septiembre");
  await contador.getByLabel("Archivo del soporte").setInputFiles({ name: "factura-ede.pdf", mimeType: "application/pdf", buffer: Buffer.from("factura EDE septiembre") });
  // UX4-02 (A-13, E-UX3-8 (a)): the fingerprint is computed and hidden; the reference is proposed from the file name; no jargon.
  await expect(contador.getByTestId("support-hash")).toContainText("Huella del archivo verificada: factura-ede.pdf");
  await expect(contador.getByLabel("Referencia del soporte")).toHaveValue("factura-ede.pdf");
  await expect(contador.getByText(/ACR-TAX/)).toHaveCount(0);
  await expect(contador.getByTestId("totals-on-save")).toBeVisible();
  await pick(contador.getByLabel("Cuenta 1"), "6200");
  await contador.getByLabel("Monto 1").fill("1,250.00");
  await pick(contador.getByLabel("Cuenta 2"), "2200");
  await contador.getByLabel("Lado 2").selectOption("credit");
  await contador.getByLabel("Monto 2").fill("1250");
  await contador.getByRole("button", { name: "Guardar borrador" }).click();
  await expect(contador.getByTestId("journal-status")).toHaveText("Borrador");
  await expect(contador.getByTestId("journal-difference")).toHaveText("0.00");
  // UX4-02 (A-14): nobody approved yet, so no "Aprobó"; who must approve it; the fingerprint short.
  await expect(contador.getByText("Aprobó", { exact: true })).toHaveCount(0);
  await expect(contador.getByTestId("journal-approver")).toContainText("Controller");
  await expect(contador.getByTestId("support-fingerprint")).toContainText("Huella verificada");
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
  // UX4-02 (A-11): the closing balance split into debit and credit balances, whose totals are equal when it balances.
  await expect(contador.getByRole("columnheader", { name: "Saldo deudor (RD$)" })).toBeVisible();
  await expect(contador.getByRole("columnheader", { name: "Saldo acreedor (RD$)" })).toBeVisible();
  const debitBalance = await contador.getByTestId("trial-balance-debit-balance").textContent();
  expect(debitBalance).toMatch(/^[\d,]+\.\d{2}$/);
  await expect(contador.getByTestId("trial-balance-credit-balance")).toHaveText(debitBalance ?? "");
  const download = contador.waitForEvent("download");
  await contador.getByRole("link", { name: "Descargar CSV" }).click();
  expect((await download).suggestedFilename()).toMatch(/^balanza-\d{8}-\d{8}\.csv$/);

  await contador.getByRole("link", { name: "Energía eléctrica" }).click();
  await expect(contador.getByRole("heading", { name: "Mayor por cuenta" })).toBeVisible();
  await expect(contador.getByRole("cell", { name: /Ajuste AJ-/ })).toBeVisible();
  // UX4-02 (A-12): the account selector narrowed by a search; the adjustment's credit shows on the other account.
  await contador.getByLabel("Buscar cuenta").fill("gastos acum");
  await contador.getByLabel("Cuenta", { exact: true }).selectOption({ label: "2200 — Gastos acumulados por pagar" });
  await expect(contador.getByRole("cell", { name: /Ajuste AJ-/ }).first()).toBeVisible();

  await nav(contador, "Estados financieros");
  await expect(contador.getByTestId("balance-status")).toHaveText("Cuadra");
  // UX4-02 (A-10): results inside Patrimonio, "Total pasivo + patrimonio" equal to the assets; no difference line when it balances.
  await expect(contador.getByTestId("balance-difference")).toHaveCount(0);
  await expect(contador.getByRole("rowheader", { name: "Total pasivo + patrimonio" })).toBeVisible();
  await expect(contador.getByTestId("total-liabilities-and-equity")).toHaveText((await contador.getByTestId("total-assets").textContent()) ?? "");
  await expect(contador.getByText(/Estructura versión/)).toHaveCount(0);
  await contador.getByRole("tab", { name: "Estado de resultados" }).click();
  await expect(contador.getByTestId("net-income")).toBeVisible();

  // UX3-02 (E-UX3-1): the guided close. The current month has not ended; last month's "Ajustes contables" shows its checklist and
  // "Verificar ahora" runs its blocking reconciliations at the month's end (nothing is closed here: other journeys post today).
  await nav(controller, "Períodos y cierre");
  const today = new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
  const [year, month] = today.split("-").map(Number);
  const current = controller.getByTestId(`month-${today.slice(0, 7)}`);
  await expect(current.getByTestId("component-ACR-NTX")).toContainText("Aún no termina");
  const previous = month === 1 ? `${(year ?? 0) - 1}-12` : `${year}-${String((month ?? 1) - 1).padStart(2, "0")}`;
  if (month === 1) {
    await controller.getByRole("button", { name: `← ${(year ?? 0) - 1}` }).click();
  }
  const component = controller.getByTestId(`month-${previous}`).getByTestId("component-ACR-NTX");
  await component.locator("summary").click();
  await expect(component.getByTestId("check-ended")).toContainText("El mes terminó: Sí");
  await expect(component.getByTestId("check-TB-BALANCED")).toBeVisible();
  await component.getByRole("button", { name: "Verificar ahora" }).click();
  await expect(component.getByTestId("check-TB-BALANCED")).toContainText("verificada");
  await component.getByTestId("check-TB-BALANCED").getByRole("link", { name: "Ver resultado" }).click();
  await expect(controller.getByTestId("run-guidance")).toBeVisible();
});
