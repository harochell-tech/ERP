import { expect, test } from "@playwright/test";
import { confirmAction, nav, signIn, submit } from "./support";

// VS#2 E2E-01 through the UI (VS2-08): the treasurer prepares the payment of the seeded posted invoice from the proposal, the
// Controller releases it, the treasurer imports the bank statement and confirms the suggested match, the Controller recognizes
// the bank charge, and BANK-GL shows no difference — each actor signs in through the (simulated) Google sign-in.


function today(offsetDays = 0): string {
  const date = new Date(Date.now() + offsetDays * 86_400_000);
  return new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(date);
}

function dominican(isoDate: string): string {
  const [year, month, day] = isoDate.split("-");
  return `${day}/${month}/${year}`;
}

test("payment from the proposal to a reconciled bank statement", async ({ browser }) => {
  const treasurer = await signIn(browser, "Tesorero");
  await nav(treasurer, "Propuesta de pago");
  await treasurer.getByLabel("Vence hasta").fill(today(60));
  await expect(treasurer.getByTestId("supplier-payability")).toHaveText("Verificada · pagable");
  await treasurer.getByRole("checkbox", { name: "Pagar B0100000001" }).check();
  await submit(treasurer, "Preparar pago");
  await expect(treasurer.getByTestId("payment-status")).toHaveText("Preparado");
  await expect(treasurer.getByTestId("payment-amount")).toHaveText("10,620.00");
  const paymentUrl = treasurer.url();
  await expect(treasurer.getByRole("button", { name: "Liberar pago" })).toHaveCount(0);

  const controller = await signIn(browser, "Controller");
  await controller.goto(paymentUrl);
  await confirmAction(controller, "Liberar pago");
  await expect(controller.getByTestId("payment-status")).toHaveText("Liberado");

  // The bank's CSV (test format TEST_BANK): the transfer with the payment number and a 150.00 charge.
  const day = today();
  const csv = `Fecha,Referencia,Descripcion,Debito,Credito\n${dominican(day)},TRF-1,Transferencia PAG-000001,10620.00,\n${dominican(day)},,Comision transferencia,150.00,\n`;
  await nav(treasurer, "Extractos bancarios");
  await treasurer.getByLabel("Archivo del banco").setInputFiles({ name: "extracto.csv", mimeType: "text/csv", buffer: Buffer.from(csv) });
  await treasurer.getByLabel("Desde").fill(day);
  await treasurer.getByLabel("Hasta").fill(day);
  await treasurer.getByLabel("Saldo inicial").fill("0.00");
  await treasurer.getByLabel("Saldo final").fill("-10770.00");
  await submit(treasurer, "Importar");
  await expect(treasurer.getByTestId("import-result")).toContainText("Importadas 2 de 2");
  await treasurer.getByRole("link", { name: "Conciliar este extracto" }).click();
  await treasurer.getByRole("button", { name: "Conciliar con PAG-000001 (por número de pago)" }).click();
  await expect(treasurer.getByRole("tab", { name: "Conciliadas (1)" })).toBeVisible();
  const reconciliationUrl = treasurer.url();

  await controller.goto(reconciliationUrl);
  await confirmAction(controller, "Registrar como cargo");
  await expect(controller.getByRole("tab", { name: "Cargos registrados (1)" })).toBeVisible();
  await expect(controller.getByTestId("bank-gl-difference")).toHaveText("0.00");

  await controller.goto(paymentUrl);
  await expect(controller.getByTestId("payment-status")).toHaveText("Compensado");
});
