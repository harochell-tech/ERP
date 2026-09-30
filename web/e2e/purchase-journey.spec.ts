import { expect, test } from "@playwright/test";
import { nav, signIn, submit } from "./support";

// E-PR18b-10: buyer creates and submits a purchase order, the approver approves it, the storekeeper receives it —
// every actor signs in through the (simulated) Google sign-in and works only through the UI.


test("purchase order from creation to receipt", async ({ browser }) => {
  const buyer = await signIn(browser, "Comprador");
  await buyer.goto("/compras/ordenes/nueva/");
  await buyer.getByLabel("Planta de la orden").selectOption({ index: 1 });
  await buyer.getByLabel("Proveedor").selectOption({ index: 1 });
  // By code: the MFG-1 dev seed adds ADITIVO-P, which sorts first (MFG1-07).
  // UX4-02 (C-08): the description is not repeated when it equals the code.
  await buyer.getByLabel("Artículo 1").selectOption({ label: "ARENA-LAVADA" });
  await buyer.getByLabel("Cantidad 1").fill("40");
  await buyer.getByLabel("Precio 1").fill("1,000.00");
  // UX4-02 (C-09): the server's preview — the line's net and the net total (40 × 1,000.00), then the ITBIS or why it is missing.
  await expect(buyer.getByTestId("po-preview-net-1")).toHaveText("40,000.00");
  await expect(buyer.getByTestId("po-preview-net-total")).toHaveText("40,000.00");
  await expect(buyer.getByTestId("po-preview-itbis")).not.toBeEmpty();
  // UX4-02 (C-11): one button saves the draft and sends it to approval.
  await submit(buyer, "Guardar y enviar a aprobación");
  await expect(buyer.getByTestId("po-status")).toHaveText("Pendiente de aprobación");
  await expect(buyer.getByTestId("po-not-sent")).toHaveCount(0);
  const orderUrl = buyer.url();
  const poNo = ((await buyer.getByRole("heading", { level: 1 }).innerText()).match(/OC-[0-9A-Z-]+/) ?? [""])[0];
  expect(poNo).not.toBe("");
  // UX4-02 (C-13): the total and each line's pending quantity are the server's.
  await expect(buyer.getByTestId("po-detail-total")).toHaveText("40,000.00");
  await expect(buyer.getByTestId("qty-open")).toHaveText("40");
  // The buyer cannot approve its own order: the button is not offered to it.
  await expect(buyer.getByRole("button", { name: "Aprobar" })).toHaveCount(0);

  const approver = await signIn(browser, "Aprobador de compras");
  // UX4-02 (C-10): the list shows each order's total and the approver's shortcut.
  await nav(approver, "Órdenes de compra");
  await expect(approver.getByTestId("po-pending-mine")).toContainText("Pendientes de mi aprobación (");
  await approver.getByTestId("po-pending-mine").click();
  await expect(approver.getByTestId(`po-row-${poNo}`).getByTestId("po-total")).toHaveText("40,000.00");
  await approver.getByTestId(`po-row-${poNo}`).getByRole("link", { name: poNo }).click();
  await expect(approver).toHaveURL(orderUrl);
  await approver.getByRole("button", { name: "Aprobar" }).click();
  await expect(approver.getByTestId("po-status")).toHaveText("Aprobado");

  // UX3-02 (E-UX3-5): the storekeeper finds the order in Almacén › Por recibir; each line comes prefilled with what is pending.
  const storekeeper = await signIn(browser, "Almacenista");
  await storekeeper.goto("/");
  await expect(storekeeper.getByRole("link", { name: "Recibir material" })).toHaveAttribute("href", "/almacen/por-recibir/");
  await nav(storekeeper, "Por recibir");
  const card = storekeeper.getByTestId(`to-receive-${poNo}`);
  await expect(card).toContainText("ARENA-LAVADA");
  await card.getByRole("link", { name: "Recibir", exact: true }).click();
  await expect(storekeeper.getByTestId("open-ARENA-LAVADA")).toHaveText("40");
  await expect(storekeeper.getByTestId("max-ARENA-LAVADA")).not.toHaveText("—");
  await expect(storekeeper.getByLabel("Cantidad a recibir ARENA-LAVADA")).toHaveValue("40");
  // UX4-02 (C-15, E-UX4-8): the dev plant has two stock locations (PATIO-A, PATIO-B) and no RECEPCION, so the server proposes
  // none and the storekeeper picks one; a plant with RECEPCION (or a single stock location) comes preselected.
  await expect(storekeeper.getByLabel("Ubicación")).toHaveValue("");
  await storekeeper.getByLabel("Ubicación").selectOption({ label: "PATIO-A" });
  await submit(storekeeper, "Registrar recepción");
  await expect(storekeeper.getByRole("heading", { name: /^Recepción / })).toBeVisible();
  await expect(storekeeper.getByTestId("accounting-status")).toHaveText("Contabilizado");
  // UX4-02 (C-17): the correction form is folded behind its button.
  await expect(storekeeper.getByLabel("Diferencia")).toHaveCount(0);

  await storekeeper.goto(orderUrl);
  await expect(storekeeper.getByTestId("po-status")).toHaveText("Recibido");
  await expect(storekeeper.getByTestId("qty-received")).toHaveText("40");
  await expect(storekeeper.getByTestId("qty-open")).toHaveText("0");
  await expect(storekeeper.getByTestId("po-receipts")).toContainText("ARENA-LAVADA");
});
