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

/** UX4-02 (C-27, E-UX4-6): the treasury journey's account, once the Controller names it. */
const ACCOUNT = "Operativa · TEST_BANK ••••6789";

test("payment from the proposal to a reconciled bank statement", async ({ browser }) => {
  // UX4-02 (C-27): the Controller names the seeded account; every treasury screen then reads "alias · banco ••••6789". Idempotent
  // (the button reads "Cambiar alias" when a previous run already named it).
  const controller = await signIn(browser, "Controller");
  await nav(controller, "Cuentas bancarias de la empresa");
  const accountRow = controller.getByRole("row").filter({ hasText: "••••6789" });
  await accountRow.getByRole("button", { name: /^(Poner|Cambiar) alias$/ }).click();
  await accountRow.getByLabel("Alias de TEST_BANK ••••6789").fill("Operativa");
  await accountRow.getByRole("button", { name: "Guardar alias" }).click();
  await expect(accountRow.getByTestId("bank-account-label")).toHaveText(ACCOUNT);

  const treasurer = await signIn(browser, "Tesorero");
  await nav(treasurer, "Propuesta de pago");
  // UX4-02 (C-26, E-UX4-12): the proposal opens with "Esta semana" (today + 7); the seeded invoice falls due in 30 days, so "Todo".
  await expect(treasurer.getByLabel("Vence hasta", { exact: true })).toHaveValue(today(7));
  await expect(treasurer.getByRole("button", { name: "Esta semana" })).toHaveAttribute("aria-pressed", "true");
  await treasurer.getByRole("button", { name: "Todo", exact: true }).click();
  await expect(treasurer.getByRole("button", { name: "Todo", exact: true })).toHaveAttribute("aria-pressed", "true");
  await expect(treasurer.getByTestId("supplier-payability")).toHaveText("Verificada · pagable");
  await treasurer.getByRole("checkbox", { name: "Pagar B0100000001" }).check();
  await treasurer.getByLabel("Cuenta de la empresa").selectOption({ label: ACCOUNT });
  await submit(treasurer, "Preparar pago");
  await expect(treasurer.getByTestId("payment-status")).toHaveText("Preparado");
  await expect(treasurer.getByTestId("payment-amount")).toHaveText("10,620.00");
  await expect(treasurer.getByTestId("payment-bank-account")).toHaveText(ACCOUNT);
  const paymentUrl = treasurer.url();
  await expect(treasurer.getByRole("button", { name: "Liberar pago" })).toHaveCount(0);

  await controller.goto(paymentUrl);
  await confirmAction(controller, "Liberar pago");
  await expect(controller.getByTestId("payment-status")).toHaveText("Liberado");

  // The bank's CSV (test format TEST_BANK): the transfer with the payment number and a 150.00 charge.
  const day = today();
  const csv = `Fecha,Referencia,Descripcion,Debito,Credito\n${dominican(day)},TRF-1,Transferencia PAG-000001,10620.00,\n${dominican(day)},,Comision transferencia,150.00,\n`;
  await nav(treasurer, "Extractos bancarios");
  // UX4-02 (C-30): the import opens from the button beside the title, as "Preparar un pago" on Pagos.
  await treasurer.getByRole("button", { name: "Importar extracto" }).click();
  await treasurer.getByLabel("Cuenta bancaria").selectOption({ label: ACCOUNT });
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
  // UX4-02 (C-29): the reconciling statement, statement → books with the server's totals, nothing left unexplained.
  await expect(controller.getByLabel("Cuenta bancaria")).toContainText(ACCOUNT);
  await expect(controller.getByTestId("reconciling-statement")).toContainText("Diferencia sin explicar");
  await expect(controller.getByTestId("recon-difference")).toHaveText("0.00");

  await controller.goto(paymentUrl);
  await expect(controller.getByTestId("payment-status")).toHaveText("Compensado");

  // UX4-02 (C-31): the payments list counts and totals what the filter selects (the server's), and names the account by its alias.
  await nav(controller, "Pagos");
  await expect(controller.getByTestId("payments-count")).toHaveText(/^\d+ pagos?$/);
  await expect(controller.getByTestId("payments-total")).toHaveText(/^[\d,]+\.\d{2}$/);
  await expect(controller.getByRole("row").filter({ hasText: "PAG-000001" })).toContainText(ACCOUNT);
  await nav(treasurer, "Extractos bancarios");
  await expect(treasurer.getByRole("row").filter({ hasText: "extracto.csv" }).first()).toContainText("2 líneas, ninguna pendiente");

  // UX3-02 (E-UX3-6): the invoice reads paid in the payables list — ITBIS, total with ITBIS and balance are the server's — and its
  // detail lists the payment.
  await nav(controller, "Facturas de proveedor");
  const invoiceRow = controller.getByTestId("si-row-B0100000001");
  await expect(invoiceRow.getByTestId("si-payment-status")).toHaveText("Pagada");
  await expect(invoiceRow.getByTestId("si-open")).toHaveText("0.00");
  await invoiceRow.getByRole("link", { name: "B0100000001" }).click();
  await expect(controller).toHaveURL(/\/cxp\/factura\/\?id=/); // the list may hold other invoices (GAS1-07 journeys)
  await expect(controller.getByTestId("si-payment-status")).toHaveText("Pagada");
  await expect(controller.getByTestId("si-payments")).toContainText("PAG-000001");
});
