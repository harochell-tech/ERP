import { expect, test } from "@playwright/test";
import { signIn, submit } from "./support";

// E-PR18b-10: buyer creates and submits a purchase order, the approver approves it, the storekeeper receives it —
// every actor signs in through the (simulated) Google sign-in and works only through the UI.


test("purchase order from creation to receipt", async ({ browser }) => {
  const buyer = await signIn(browser, "Comprador");
  await buyer.goto("/compras/ordenes/nueva/");
  await buyer.getByLabel("Planta de la orden").selectOption({ index: 1 });
  await buyer.getByLabel("Proveedor").selectOption({ index: 1 });
  // By code: the MFG-1 dev seed adds ADITIVO-P, which sorts first (MFG1-07).
  await buyer.getByLabel("Artículo 1").selectOption({ label: "ARENA-LAVADA — ARENA-LAVADA" });
  await buyer.getByLabel("Cantidad 1").fill("40");
  await buyer.getByLabel("Precio 1").fill("1,000.00");
  await submit(buyer, "Crear orden");
  await expect(buyer.getByTestId("po-status")).toHaveText("Borrador");
  const orderUrl = buyer.url();
  await buyer.getByRole("button", { name: "Enviar a aprobación" }).click();
  await expect(buyer.getByTestId("po-status")).toHaveText("Pendiente de aprobación");
  // The buyer cannot approve its own order: the button is not offered to it.
  await expect(buyer.getByRole("button", { name: "Aprobar" })).toHaveCount(0);

  const approver = await signIn(browser, "Aprobador de compras");
  await approver.goto(orderUrl);
  await approver.getByRole("button", { name: "Aprobar" }).click();
  await expect(approver.getByTestId("po-status")).toHaveText("Aprobado");

  const storekeeper = await signIn(browser, "Almacenista");
  await storekeeper.goto(orderUrl);
  await storekeeper.getByRole("link", { name: "Recibir material" }).click();
  await storekeeper.getByLabel("Ubicación").selectOption({ index: 1 });
  await storekeeper.getByLabel("Cantidad a recibir ARENA-LAVADA").fill("40");
  await submit(storekeeper, "Registrar recepción");
  await expect(storekeeper.getByRole("heading", { name: /^Recepción / })).toBeVisible();
  await expect(storekeeper.getByTestId("accounting-status")).toHaveText("Contabilizado");

  await storekeeper.goto(orderUrl);
  await expect(storekeeper.getByTestId("po-status")).toHaveText("Recibido");
  await expect(storekeeper.getByTestId("qty-received")).toHaveText("40");
});
