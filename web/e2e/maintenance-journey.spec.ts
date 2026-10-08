import { expect, test } from "@playwright/test";
import { confirmAction, nav, signIn } from "./support";

// MFG3-04 (E-MFG3-4…10, E-MFG3-01-2/3): the Gerente de planta sets an ideal cycle on Máquinas y turnos, defines a preventive
// maintenance task and records it done; Producción › Eficiencia opens (the dev stack reads no portal, so it says so). The Supervisor
// sees the tasks without the forms.

test("ideal cycle, preventive maintenance and the efficiency screen (MFG3-04)", async ({ browser }) => {
  const manager = await signIn(browser, "Gerente de planta");
  await nav(manager, "Máquinas y turnos");
  await manager.getByLabel("Código de la máquina").fill("BLQ-MANT");
  await manager.getByLabel("Nombre de la máquina").fill("Bloquera de mantenimiento");
  await manager.getByRole("button", { name: "Crear máquina" }).click();
  await expect(manager.getByRole("cell", { name: "BLQ-MANT", exact: true })).toBeVisible();
  const cycles = manager.getByTestId("ideal-cycles");
  await cycles.locator("select").nth(0).selectOption({ label: "BLQ-MANT — Bloquera de mantenimiento" });
  await cycles.locator("select").nth(1).selectOption({ index: 1 });
  await cycles.getByLabel("Desde").fill("2026-10-01");
  await cycles.getByLabel("Segundos por ciclo").fill("11.5");
  await cycles.getByRole("button", { name: "Guardar ciclo ideal" }).click();
  await expect(cycles.getByRole("row", { name: /BLQ-MANT/ })).toContainText("11.5");

  await nav(manager, "Mantenimiento preventivo");
  await expect(manager.getByRole("heading", { name: "Mantenimiento preventivo" })).toBeVisible();
  await manager.getByRole("combobox", { name: "Máquina", exact: true }).selectOption({ label: "BLQ-MANT — Bloquera de mantenimiento" });
  await manager.getByLabel("Código (el mecánico lo elige en el portal)").fill("zapatas");
  await manager.getByLabel("Tarea").fill("Cambiar zapatas");
  await manager.getByLabel("Cada").fill("30");
  await manager.getByRole("combobox", { name: "Unidad", exact: true }).selectOption("DAYS");
  await manager.getByRole("button", { name: "Crear tarea" }).click();
  const row = manager.getByTestId("maintenance-task:ZAPATAS");
  await expect(row).toContainText("Cada 30 días");
  await expect(manager.getByTestId("maintenance-state:ZAPATAS")).toHaveText("Al día");
  await confirmAction(row, "Marcar hecha");
  await expect(row).not.toContainText("Nunca");

  await nav(manager, "Eficiencia");
  await expect(manager.getByRole("heading", { name: "Eficiencia de las máquinas" })).toBeVisible();
  await expect(manager.getByTestId("efficiency-machines")).toContainText("Sin lecturas del portal en el período.");

  const supervisor = await signIn(browser, "Supervisor de producción");
  await nav(supervisor, "Mantenimiento preventivo");
  await expect(supervisor.getByTestId("maintenance-task:ZAPATAS")).toBeVisible();
  await expect(supervisor.getByRole("button", { name: "Crear tarea" })).toHaveCount(0);
});
