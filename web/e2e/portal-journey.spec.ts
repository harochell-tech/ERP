import { expect, test } from "@playwright/test";
import { confirmAction, nav, signIn } from "./support";

// MFG2-03 (E-MFG2-3, E-MFG2-01-7): the Gerente de planta pairs the machines' portal on Producción › Portal — a machine with its batch
// plant, a mould, a shift, a batch-plant material — and removes one; the Supervisor sees the pairings and the state without forms.
// The dev stack does not read a portal: the state says no read yet.

test("Producción › Portal: the plant manager pairs the portal and the supervisor reads it", async ({ browser }) => {
  const manager = await signIn(browser, "Gerente de planta");
  await nav(manager, "Máquinas y turnos");
  await manager.getByLabel("Código de la máquina").fill("BLQ-PORTAL");
  await manager.getByLabel("Nombre de la máquina").fill("Bloquera del portal");
  await manager.getByRole("button", { name: "Crear máquina" }).click();
  await expect(manager.getByRole("cell", { name: "BLQ-PORTAL", exact: true })).toBeVisible();
  await manager.getByLabel("Código del turno").fill("PT1");
  await manager.getByLabel("Inicia", { exact: true }).fill("08:00");
  await manager.getByLabel("Termina", { exact: true }).fill("17:00");
  await manager.getByRole("button", { name: "Definir turno" }).click();
  await expect(manager.getByRole("cell", { name: "PT1", exact: true })).toBeVisible();

  await nav(manager, "Portal de máquinas");
  await expect(manager.getByRole("heading", { name: "Portal de máquinas" })).toBeVisible();
  await expect(manager.getByTestId("portal-last-ok")).toHaveText("Todavía ninguna");

  await manager.getByLabel("En el portal", { exact: true }).fill("planta2");
  await manager.getByLabel("Máquina de Core").selectOption({ label: "BLQ-PORTAL — Bloquera del portal" });
  await manager.getByLabel("Dosificadora (vacío: sin conexión)").fill("dosificadora2");
  await manager.getByRole("button", { name: "Emparejar máquina" }).click();
  await expect(manager.getByTestId("portal-machines")).toContainText("dosificadora2");

  await manager.getByLabel("Molde (pulgadas)").fill("6");
  await manager.getByLabel("Producto").selectOption({ index: 1 });
  await manager.getByRole("button", { name: "Emparejar molde" }).click();
  await expect(manager.getByTestId("portal-moulds")).toContainText("6 pulg.");

  await manager.getByLabel("Turno de Core").selectOption({ index: 1 });
  await manager.getByRole("button", { name: "Emparejar turno" }).click();
  await expect(manager.getByTestId("portal-shifts")).toContainText("Turno 1");

  await manager.getByLabel("Dosificadora", { exact: true }).fill("dosificadora2");
  await manager.getByLabel("Código del material").fill("cemento");
  const article = manager.getByLabel("Artículo");
  const cement = (await article.locator("option", { hasText: "CEMENTO-GRIS" }).first().getAttribute("value")) ?? "";
  await article.selectOption(cement);
  await manager.getByLabel("Se descarga de").selectOption({ index: 1 });
  await manager.getByRole("button", { name: "Emparejar material" }).click();
  const materials = manager.getByTestId("portal-materials");
  await expect(materials).toContainText("CEMENTO");
  await confirmAction(materials.getByRole("row", { name: /CEMENTO/ }), "Quitar");
  await expect(materials).not.toContainText("CEMENTO-GRIS");

  const supervisor = await signIn(browser, "Supervisor de producción");
  await nav(supervisor, "Portal de máquinas");
  await expect(supervisor.getByTestId("portal-machines")).toContainText("planta2");
  await expect(supervisor.getByRole("button", { name: "Emparejar máquina" })).toHaveCount(0);
});
