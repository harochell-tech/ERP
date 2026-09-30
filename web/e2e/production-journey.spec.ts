import { expect, test } from "@playwright/test";
import { confirmAction, expectFits, nav, signIn, submit } from "./support";

// MFG-1 through the UI (MFG1-07, E-MFG1-07-6): the Gerente de planta creates the machine and the shift, the Supervisor de producción
// prepares the recipe and the Gerente approves it, the Controller prepares the standard cost from the recipe and the Aprobador de
// políticas approves it, the Supervisor starts yesterday's run and records the shift summary, the Gerente posts it (the lot goes into
// curing, 3 racks) and Calidad releases the cured lot to the yard — each actor signs in through the (simulated) Google sign-in.


/** A Dominican calendar date `offsetDays` from today, as yyyy-MM-dd. */
function dominicanDate(offsetDays: number): string {
  const today = new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
  const [year, month, day] = today.split("-").map(Number);
  return new Date(Date.UTC(year ?? 0, (month ?? 1) - 1, (day ?? 1) + offsetDays)).toISOString().slice(0, 10);
}

test("a production day from the recipe to a released lot (MFG-1)", async ({ browser }) => {
  test.setTimeout(180_000); // eight sign-ins and a dozen commands
  const yesterday = dominicanDate(-1);

  // Masters: the block machine and the day shift.
  const manager = await signIn(browser, "Gerente de planta");
  await nav(manager, "Máquinas y turnos");
  await manager.getByLabel("Código de la máquina").fill("BESSER-1");
  await manager.getByLabel("Nombre de la máquina").fill("Bloquera Besser 1");
  await manager.getByRole("button", { name: "Crear máquina" }).click();
  await expect(manager.getByRole("cell", { name: "BESSER-1", exact: true })).toBeVisible();
  await manager.getByLabel("Código del turno").fill("DIA");
  await manager.getByLabel("Inicia", { exact: true }).fill("07:00");
  await manager.getByLabel("Termina", { exact: true }).fill("19:00");
  await manager.getByRole("button", { name: "Definir turno" }).click();
  await expect(manager.getByRole("cell", { name: "DIA", exact: true })).toBeVisible();

  // Recipe: 150 pavers per batch, 6 per cycle, 600 per rack, no minimum curing (so yesterday's lot can be released today).
  const supervisor = await signIn(browser, "Supervisor de producción");
  await nav(supervisor, "Recetas");
  await supervisor.getByLabel("Producto", { exact: true }).selectOption({ label: "ADOQUIN-H — Adoquín holandés (un)" });
  await supervisor.getByLabel("Máquina", { exact: true }).selectOption({ label: "BESSER-1 — Bloquera Besser 1" });
  await supervisor.getByLabel("Unidades por tanda").fill("150");
  await supervisor.getByLabel("Unidades por ciclo").fill("6");
  await supervisor.getByLabel("Unidades por rack").fill("600");
  await supervisor.getByLabel("Curado mínimo (horas)").fill("0");
  await supervisor.getByLabel("Curado máximo (horas)").fill("168");
  const materials: [string, string][] = [
    ["CEMENTO-GRIS — CEMENTO-GRIS (t)", "0.18"],
    ["ARENA-LAVADA — ARENA-LAVADA (t)", "1.8"],
    ["ADITIVO-P — ADITIVO-P (l)", "1.5"],
  ];
  for (const [index, [material, qty]] of materials.entries()) {
    if (index > 0) {
      await supervisor.getByRole("button", { name: "Agregar material" }).click();
    }
    await supervisor.getByLabel(`Material ${index + 1}`, { exact: true }).selectOption({ label: material });
    await supervisor.getByLabel(`Cantidad por tanda ${index + 1}`, { exact: true }).fill(qty);
  }
  await submit(supervisor, "Preparar receta");
  await supervisor.getByRole("link", { name: "Ver la receta" }).click();
  await expect(supervisor.getByTestId("recipe-status")).toHaveText("Borrador");
  await expect(supervisor.getByRole("button", { name: "Aprobar receta" })).toHaveCount(0);
  const recipeUrl = supervisor.url();

  await manager.goto(recipeUrl);
  await confirmAction(manager, "Aprobar receta");
  await expect(manager.getByTestId("recipe-status")).toHaveText("Activo");

  // Standard cost from the recipe: per paver 0.0012 t × 8,200 + 0.012 t × 1,000 + 0.01 l × 50 = 22.34, plus 5.90 of conversion.
  const controller = await signIn(browser, "Controller");
  await nav(controller, "Costos estándar");
  await controller.getByLabel("Receta activa").selectOption({ label: "ADOQUIN-H en BESSER-1 (v1)" });
  await controller.getByLabel("Precio estándar CEMENTO-GRIS (por t)").fill("8200.00");
  await controller.getByLabel("Precio estándar ARENA-LAVADA (por t)").fill("1000.00");
  await controller.getByLabel("Precio estándar ADITIVO-P (por l)").fill("50.00");
  await controller.getByLabel("Costo de conversión por unidad").fill("5.90");
  await submit(controller, "Preparar desde receta");
  await expect(controller.getByTestId("recipe-unit-cost")).toHaveText("28.24");

  const approver = await signIn(browser, "Aprobador de políticas contables");
  await nav(approver, "Costos estándar");
  const standard = approver.getByRole("row").filter({ hasText: "ADOQUIN-H" });
  await confirmAction(standard, "Aprobar");
  await expect(standard.getByText("Activo", { exact: true })).toBeVisible();

  // Yesterday's run on the day shift: 10 batches, 1,480 good pavers and the real consumption from the yard.
  await nav(supervisor, "Producción del día");
  await supervisor.getByLabel("Día", { exact: true }).fill(yesterday);
  await supervisor.getByLabel("Máquina", { exact: true }).selectOption({ label: "BESSER-1 — Bloquera Besser 1" });
  await supervisor.getByLabel("Turno", { exact: true }).selectOption({ label: "DIA (07:00–19:00)" });
  await supervisor.getByLabel("Producto", { exact: true }).selectOption({ label: "ADOQUIN-H — Adoquín holandés" });
  await expect(supervisor.getByLabel("Fecha de producción")).toHaveValue(yesterday);
  await submit(supervisor, "Iniciar corrida");
  await supervisor.getByRole("link", { name: "Abrir la corrida" }).click();
  await expect(supervisor.getByTestId("run-status")).toHaveText("En proceso");
  await supervisor.getByLabel("Tandas", { exact: true }).fill("10");
  await supervisor.getByLabel("Unidades buenas", { exact: true }).fill("1480");
  for (const [code, qty] of [
    ["CEMENTO-GRIS", "1.85"],
    ["ARENA-LAVADA", "18.375"],
    ["ADITIVO-P", "15"],
  ]) {
    await supervisor.getByLabel(`Ubicación ${code}`).selectOption({ label: "PATIO-A" });
    await supervisor.getByLabel(`Consumo ${code}`).fill(qty ?? "");
  }
  await submit(supervisor, "Registrar resumen");
  await expect(supervisor.getByTestId("summary-status")).toHaveText("Borrador");
  await expect(supervisor.getByRole("button", { name: "Contabilizar resumen" })).toHaveCount(0);
  const runUrl = supervisor.url();

  // The Gerente posts it: the run is completed and its lot of 3 racks goes into curing.
  await manager.goto(runUrl);
  await confirmAction(manager, "Contabilizar resumen");
  await expect(manager.getByTestId("summary-status")).toHaveText("Contabilizado");
  await expect(manager.getByTestId("run-status")).toHaveText("Completada");
  await expect(manager.getByTestId("lot-status")).toHaveText("En curado");
  await expect(manager.getByTestId("lot-racks")).toHaveText("3");
  const lotCode = (await manager.getByTestId("lot-code").textContent())?.trim() ?? "";
  expect(lotCode).not.toBe("");

  // Calidad sees the lot counted on Inicio and releases it to the yard (UX3-02, E-UX3-12/13): the screen opens on "Listos para
  // liberar" and the lot's one "Acciones" button opens a dialog (a sheet on a phone) with what Calidad may do.
  const quality = await signIn(browser, "Calidad");
  await quality.goto("/");
  await expect(quality.getByTestId("task-count:/produccion/lotes/")).not.toHaveText("0");
  await nav(quality, "Curado y liberación");
  await expect(quality.getByLabel("Estado", { exact: true })).toHaveValue("READY");
  const lotRow = quality.getByRole("row").filter({ hasText: lotCode });
  await lotRow.getByRole("button", { name: `Acciones del lote ${lotCode}` }).click();
  const dialog = quality.getByRole("dialog", { name: `Acciones del lote ${lotCode}` });
  await expect(dialog.getByRole("button", { name: "Bloquear" })).toBeVisible();
  await dialog.getByRole("button", { name: "Liberar", exact: true }).click();
  await dialog.getByLabel(`Liberar ${lotCode} a`).selectOption({ label: "PATIO-A" });
  await expectFits(quality);
  await dialog.getByRole("button", { name: "Confirmar: Liberar" }).click();
  await expect(dialog).toBeHidden();
  await quality.getByLabel("Estado", { exact: true }).selectOption("RELEASED");
  await expect(quality.getByTestId(`lot-status-${lotCode}`)).toHaveText("Liberado");
});
