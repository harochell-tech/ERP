import { expect, test } from "@playwright/test";
import { confirmAction, nav, signIn } from "./support";

// VS4-04 (E-VS4-04-1/2, E-VS4-03-2): the Controller sets the e-CF issuer's address on Configuración › Empresa; the Especialista fiscal
// registers an e-NCF range on Fiscal › Rangos e-NCF; the Controller approves it (it is the type's range in force) and closes it again,
// so the other journeys keep the manual channel; the e-CF inbox opens with its filters. The dev stack's gateway is Off: nothing is sent.

test("issuer address, an e-NCF range registered, approved and closed, and the e-CF inbox", async ({ browser }) => {
  // A range of its own on every run (closed ranges still block overlaps).
  const first = (Date.now() % 9_000_000_000) + 1;
  const last = first + 999;
  const digits = (n: number) => String(n).padStart(10, "0");

  const controller = await signIn(browser, "Controller");
  await nav(controller, "Empresa");
  await controller.getByLabel("Dirección", { exact: true }).fill("Carretera Higüey–La Romana km 3, Higüey");
  await controller.getByLabel("Teléfono", { exact: true }).fill("809-554-0000");
  await confirmAction(controller, "Guardar datos del emisor");
  await expect(controller.getByTestId("company-address")).toHaveText("Carretera Higüey–La Romana km 3, Higüey");

  const specialist = await signIn(browser, "Especialista fiscal");
  await nav(specialist, "Rangos e-NCF");
  await expect(specialist.getByRole("heading", { name: "Rangos e-NCF" })).toBeVisible();
  await specialist.getByLabel("Primer número", { exact: true }).fill(`E31${digits(first)}`);
  await specialist.getByLabel("Último número", { exact: true }).fill(String(last));
  await specialist.getByLabel("Vence el", { exact: true }).fill(`${new Date().getFullYear() + 1}-12-31`);
  await specialist.getByRole("button", { name: "Registrar rango" }).click();
  const row = specialist.getByTestId(`series:E31${digits(first)}`);
  await expect(row).toContainText("Por aprobar");
  await expect(row.getByRole("button", { name: "Aprobar" })).toHaveCount(0); // four eyes: the Controller approves

  await nav(controller, "Rangos e-NCF");
  const controllerRow = controller.getByTestId(`series:E31${digits(first)}`);
  await confirmAction(controllerRow, "Aprobar");
  await expect(controllerRow).toContainText("Vigente");
  await expect(controllerRow).toContainText(`E31${digits(first)}`);
  await expect(controllerRow).toContainText("1000");
  await confirmAction(controllerRow, "Cerrar");
  await expect(controllerRow).toContainText("Cerrado");

  await nav(specialist, "e-CF");
  await expect(specialist.getByRole("heading", { name: "e-CF", exact: true })).toBeVisible();
  await specialist.getByLabel("Estado", { exact: true }).selectOption("REQUIRES_ACTION");
  await expect(specialist).toHaveURL(/estado=REQUIRES_ACTION/);
  await expect(specialist.getByTestId("ecf-inbox")).toContainText("No hay e-CF con estos filtros.");
});
