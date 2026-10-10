import { expect, test } from "@playwright/test";
import { confirmAction, expectFits, nav, pick, signIn, submit } from "./support";

// MFG-1 through the UI (MFG1-07, E-MFG1-07-6): the Gerente de planta creates the machine and the shift, the Supervisor de producción
// prepares the recipe and the Gerente approves it, the Controller prepares the standard cost from the recipe and the Aprobador de
// políticas approves it, the Supervisor starts yesterday's run and records the shift summary, the Gerente posts it (the lot goes into
// curing, 3 racks) and Calidad releases the cured lot to the yard — each actor signs in through the (simulated) Google sign-in.
// UX4-03: the recipe form is folded and needs at least 1 hour of curing (E-UX4-9); the run shows its variance with sign, % and the
// tolerance mark and the day its totals card (merma); "Cerrar resumen del turno" (E-UX4-14); a night run started today leaves a lot
// whose curing is not done, which Calidad cannot release yet (the remaining hours are shown instead).

/** A Dominican calendar date `offsetDays` from today, as yyyy-MM-dd. */
function dominicanDate(offsetDays: number): string {
  const today = new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
  const [year, month, day] = today.split("-").map(Number);
  return new Date(Date.UTC(year ?? 0, (month ?? 1) - 1, (day ?? 1) + offsetDays)).toISOString().slice(0, 10);
}

const REMAINING = /^Faltan? \d+ horas? de curado$/;

test("a production day from the recipe to a released lot (MFG-1)", async ({ browser }) => {
  test.setTimeout(240_000); // eight sign-ins and some twenty commands
  const yesterday = dominicanDate(-1);

  // Masters: the block machine, the day shift and the night shift (it ends the next morning).
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
  await manager.getByLabel("Código del turno").fill("NOCHE");
  await manager.getByLabel("Inicia", { exact: true }).fill("19:00");
  await manager.getByLabel("Termina", { exact: true }).fill("07:00");
  await manager.getByRole("button", { name: "Definir turno" }).click();
  await expect(manager.getByRole("row").filter({ has: manager.getByRole("cell", { name: "NOCHE", exact: true }) })).toContainText("Sí (termina al día siguiente)");

  // Recipe: 150 pavers per batch, 6 per cycle, 600 per rack, 1 hour of minimum curing (E-UX4-9: at least 1; yesterday's day-shift
  // lot may be released from 20:00 yesterday). The form is folded (P-35) and says the recipe stays in draft.
  const supervisor = await signIn(browser, "Supervisor de producción");
  await nav(supervisor, "Recetas");
  await supervisor.getByRole("button", { name: "Preparar una receta nueva" }).click();
  await expect(supervisor.getByTestId("recipe-draft-notice")).toBeVisible();
  await pick(supervisor.getByLabel("Producto", { exact: true }), "ADOQUIN-H");
  await supervisor.getByLabel("Máquina", { exact: true }).selectOption({ label: "BESSER-1 — Bloquera Besser 1" });
  await supervisor.getByLabel("Unidades por tanda").fill("150");
  await supervisor.getByLabel("Unidades por ciclo").fill("6");
  await supervisor.getByLabel("Unidades por rack").fill("600");
  await supervisor.getByLabel("Curado mínimo (horas)").fill("0");
  await supervisor.getByLabel("Curado máximo (horas)").fill("168");
  // P-37 / P-06: the code once when the description repeats it, "L" for litre.
  const materials: [string, string][] = [
    ["CEMENTO-GRIS (t)", "0.18"],
    ["ARENA-LAVADA (t)", "1.8"],
    ["ADITIVO-P (L)", "1.5"],
  ];
  for (const [index, [material, qty]] of materials.entries()) {
    if (index > 0) {
      await supervisor.getByRole("button", { name: "Agregar material" }).click();
    }
    await pick(supervisor.getByLabel(`Material ${index + 1}`, { exact: true }), material);
    await supervisor.getByLabel(`Cantidad por tanda ${index + 1}`).fill(qty);
  }
  await submit(supervisor, "Preparar receta");
  await expect(supervisor.getByText("El curado mínimo es de al menos 1 hora.")).toBeVisible();
  await supervisor.getByLabel("Curado mínimo (horas)").fill("1");
  await submit(supervisor, "Preparar receta");
  await supervisor.getByRole("link", { name: "Ver la receta" }).click();
  await expect(supervisor.getByTestId("recipe-status")).toHaveText("Borrador");
  await expect(supervisor.getByTestId("recipe-people")).toContainText("Preparada por: Usted");
  await expect(supervisor.getByRole("button", { name: "Aprobar receta" })).toHaveCount(0);
  const recipeUrl = supervisor.url();

  await manager.goto(recipeUrl);
  await confirmAction(manager, "Aprobar receta");
  await expect(manager.getByTestId("recipe-status")).toHaveText("Activa");
  await expect(manager.getByTestId("recipe-people")).toContainText("Aprobada por: Usted");

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

  // Yesterday's run on the day shift: 10 batches, 1,480 good pavers, 12 + 8 of merma and the real consumption from the yard. The
  // form is folded (P-18) and its date is the day shown (P-13); no consumption location is preselected (P-23).
  await nav(supervisor, "Producción del día");
  await expect(supervisor.getByTestId("production-steps")).toBeVisible();
  await supervisor.getByLabel("Día", { exact: true }).fill(yesterday);
  await supervisor.getByRole("button", { name: "Iniciar una corrida" }).click();
  await supervisor.getByLabel("Máquina", { exact: true }).selectOption({ label: "BESSER-1 — Bloquera Besser 1" });
  await supervisor.getByLabel("Turno", { exact: true }).selectOption({ label: "DIA (07:00–19:00)" });
  await supervisor.getByLabel("Producto", { exact: true }).selectOption({ label: "ADOQUIN-H — Adoquín holandés" });
  await expect(supervisor.getByLabel("Fecha de producción")).toHaveValue(yesterday);
  await submit(supervisor, "Iniciar corrida");
  await supervisor.getByRole("link", { name: "Abrir la corrida" }).click();
  await expect(supervisor.getByTestId("run-status")).toHaveText("En proceso");
  await supervisor.getByLabel("Tandas", { exact: true }).fill("10");
  await supervisor.getByLabel("Unidades buenas", { exact: true }).fill("1480");
  await supervisor.getByLabel("Merma de mezcla (unidades)").fill("12");
  await supervisor.getByLabel("Merma en fresco (unidades)").fill("8");
  await expect(supervisor.getByLabel("Ubicación CEMENTO-GRIS")).toHaveValue("");
  // Theory for 10 batches: 1.8 t cement, 18 t sand, 15 L admixture.
  for (const [code, qty] of [
    ["CEMENTO-GRIS", "1.85"],
    ["ARENA-LAVADA", "18.375"],
    ["ADITIVO-P", "16"],
  ]) {
    await supervisor.getByLabel(`Ubicación ${code}`).selectOption({ label: "PATIO-A" });
    await supervisor.getByLabel(`Consumo ${code}`).fill(qty ?? "");
  }
  await submit(supervisor, "Registrar resumen");
  await expect(supervisor.getByTestId("summary-status")).toHaveText("Borrador");
  await expect(supervisor.getByRole("button", { name: "Cerrar resumen del turno" })).toHaveCount(0);
  const runUrl = supervisor.url();

  // The Gerente closes it (E-UX4-14): the run is completed and its lot of 3 racks goes into curing.
  await manager.goto(runUrl);
  await expect(manager.getByTestId("run-recipe")).toHaveText("Versión 1");
  await confirmAction(manager, "Cerrar resumen del turno");
  await expect(manager.getByTestId("summary-status")).toHaveText("Cerrado");
  await expect(manager.getByTestId("run-status")).toHaveText("Completada");
  await expect(manager.getByTestId("lot-status")).toHaveText("En curado");
  await expect(manager.getByTestId("lot-racks")).toHaveText("3");
  const lotCode = (await manager.getByTestId("lot-code").textContent())?.trim() ?? "";
  expect(lotCode).not.toBe("");
  // P-16 / P-22: per batch, theory for the batches, the variance with its sign, % and the tolerance mark (PRODUCTION policy 5 %):
  // cement 1.85 − 1.8 = +0.05 t (0.05 ÷ 1.8 = +2.78 %), admixture 16 − 15 = +1 L (1 ÷ 15 = +6.67 %, over 5 %).
  await expect(manager.getByTestId("run-tolerance")).toContainText("±5 %");
  const cement = manager.getByTestId("consumption-CEMENTO-GRIS");
  await expect(cement.getByTestId("variance")).toHaveText("+0.05 t");
  await expect(cement.getByTestId("variance-pct")).toHaveText("+2.78 %");
  await expect(cement).toContainText("Dentro de tolerancia");
  const additive = manager.getByTestId("consumption-ADITIVO-P");
  await expect(additive.getByTestId("variance")).toHaveText("+1 L");
  await expect(additive.getByTestId("variance-pct")).toHaveText("+6.67 %");
  await expect(additive).toContainText("Fuera de tolerancia");

  // P-15: yesterday's totals from the server (1,480 good, 12 + 8 = 20 of merma) and the same variance on the day's materials.
  await manager.goto(`/produccion/dia/?dia=${yesterday}`);
  await expect(manager.getByTestId("day-good-units")).toHaveText("1,480");
  await expect(manager.getByTestId("day-mix-scrap")).toHaveText("12");
  await expect(manager.getByTestId("day-fresh-scrap")).toHaveText("8");
  await expect(manager.getByTestId("day-scrap")).toHaveText("20");
  await expect(manager.getByTestId("day-material-ADITIVO-P")).toContainText("Fuera de tolerancia");
  await expect(manager.getByTestId("day-material-CEMENTO-GRIS").getByTestId("variance-pct")).toHaveText("+2.78 %");
  await expectFits(manager);

  // Tonight's run on the night shift: its lot starts curing when the shift ends tomorrow at 07:00, so it cannot be released today.
  // P-14: the run's next step on the day's list.
  await nav(supervisor, "Producción del día");
  await supervisor.getByRole("button", { name: "Iniciar una corrida" }).click();
  await supervisor.getByLabel("Máquina", { exact: true }).selectOption({ label: "BESSER-1 — Bloquera Besser 1" });
  await supervisor.getByLabel("Turno", { exact: true }).selectOption({ label: "NOCHE (19:00–07:00)" });
  await supervisor.getByLabel("Producto", { exact: true }).selectOption({ label: "ADOQUIN-H — Adoquín holandés" });
  await submit(supervisor, "Iniciar corrida");
  await expect(supervisor.getByTestId("run-started")).toBeVisible();
  await supervisor.getByRole("link", { name: "Registrar resumen" }).click();
  await expect(supervisor.getByTestId("run-status")).toHaveText("En proceso");
  await supervisor.getByLabel("Tandas", { exact: true }).fill("1");
  await supervisor.getByLabel("Unidades buenas", { exact: true }).fill("150");
  for (const [code, qty] of [
    ["CEMENTO-GRIS", "0.18"],
    ["ARENA-LAVADA", "1.8"],
    ["ADITIVO-P", "1.5"],
  ]) {
    await supervisor.getByLabel(`Ubicación ${code}`).selectOption({ label: "PATIO-A" });
    await supervisor.getByLabel(`Consumo ${code}`).fill(qty ?? "");
  }
  await submit(supervisor, "Registrar resumen");
  await expect(supervisor.getByTestId("summary-status")).toHaveText("Borrador");
  await manager.goto(supervisor.url());
  await confirmAction(manager, "Cerrar resumen del turno");
  await expect(manager.getByTestId("lot-status")).toHaveText("En curado");
  await expect(manager.getByTestId("lot-curing-remaining")).toHaveText(REMAINING);
  const nightLot = (await manager.getByTestId("lot-code").textContent())?.trim() ?? "";
  expect(nightLot).not.toBe("");

  // Calidad sees the lot counted on Inicio and releases it to the yard (UX3-02, E-UX3-12/13): the screen opens on "Listos para
  // liberar" and the lot's one "Acciones" button opens a dialog (a sheet on a phone) with what Calidad may do.
  const quality = await signIn(browser, "Calidad");
  await quality.goto("/");
  await expect(quality.getByTestId("task-count:/produccion/lotes/")).not.toHaveText("0");
  await nav(quality, "Curado y liberación");
  await expect(quality.getByLabel("Estado", { exact: true })).toHaveValue("READY");
  await expect(quality.getByTestId(`lot-status-${nightLot}`)).toHaveCount(0);
  const lotRow = quality.getByRole("row").filter({ hasText: lotCode });
  await lotRow.getByRole("button", { name: `Acciones del lote ${lotCode}` }).click();
  const dialog = quality.getByRole("dialog", { name: `Acciones del lote ${lotCode}` });
  await expect(dialog.getByRole("button", { name: "Bloquear" })).toBeVisible();
  await dialog.getByRole("button", { name: "Liberar", exact: true }).click();
  // P-31: no location is preselected.
  await expect(dialog.getByLabel(`Liberar ${lotCode} a`)).toHaveValue("");
  await dialog.getByLabel(`Liberar ${lotCode} a`).selectOption({ label: "PATIO-A" });
  await expectFits(quality);
  await dialog.getByRole("button", { name: "Confirmar: Liberar" }).click();
  await expect(dialog).toBeHidden();
  await quality.getByLabel("Estado", { exact: true }).selectOption("RELEASED");
  await expect(quality.getByTestId(`lot-status-${lotCode}`)).toHaveText("Liberado");

  // P-29 / P-26: the night lot is still curing — its remaining hours are shown and its actions offer no "Liberar".
  await quality.getByLabel("Estado", { exact: true }).selectOption("CURING");
  await expect(quality.getByTestId(`lot-remaining-${nightLot}`)).toHaveText(REMAINING);
  const nightRow = quality.getByRole("row").filter({ hasText: nightLot });
  await nightRow.getByRole("button", { name: `Acciones del lote ${nightLot}` }).click();
  const nightDialog = quality.getByRole("dialog", { name: `Acciones del lote ${nightLot}` });
  await expect(nightDialog.getByTestId("release-not-yet")).toBeVisible();
  await expect(nightDialog.getByRole("button", { name: "Bloquear" })).toBeVisible();
  await expect(nightDialog.getByRole("button", { name: "Liberar", exact: true })).toHaveCount(0);
  await nightDialog.getByRole("button", { name: "Cerrar", exact: true }).click();
  await expect(nightDialog).toBeHidden();

  // LAB1-01 (E-LAB1-01-6…14): the lab technician finds the night lot on Calidad › Laboratorio, types two specimens with their
  // measures — the server shows each strength before saving: 44,000 ÷ (19.5 × 39.5) = 57.12431 kg/cm² — and voids one with a reason.
  const lab = await signIn(browser, "Laboratorio");
  await nav(lab, "Laboratorio");
  await lab.getByLabel("Código del lote").fill(nightLot);
  await lab.getByTestId(`lab-lot:${nightLot}`).getByRole("button", { name: "Ensayar" }).click();
  await expect(lab.getByTestId("lab-lot")).toContainText(nightLot);
  const press = lab.getByTestId("lab-compression-form");
  for (const [n, load] of [
    [1, "44000"],
    [2, "45000"],
  ] as const) {
    if (n > 1) {
      await press.getByRole("button", { name: "Agregar probeta" }).click();
    }
    const specimen = lab.getByTestId(`lab-specimen:${n}`);
    await specimen.getByLabel("Carga (kg)").fill(load);
    await specimen.getByLabel("Ancho (cm)").fill("19.5");
    await specimen.getByLabel("Alto (cm)").fill("19.5");
    await specimen.getByLabel("Largo (cm)").fill("39.5");
  }
  await expect(lab.getByTestId("lab-specimen-result:1")).toContainText("57.12431 kg/cm²");
  await expect(lab.getByTestId("lab-specimen-result:2")).toContainText("58.42259 kg/cm²");
  await expectFits(lab);
  await press.getByRole("button", { name: "Guardar 2 probeta(s)" }).click();
  const specimens = lab.getByTestId("lab-compression-tests");
  await expect(specimens.getByRole("row")).toHaveCount(3);
  await expect(specimens).toContainText("57.12431");
  await specimens.getByRole("row").nth(1).getByRole("button", { name: "Anular" }).click();
  const voidDialog = lab.getByRole("dialog");
  await voidDialog.getByLabel("Motivo: Anular").fill("Carga mal leída");
  await voidDialog.getByRole("button", { name: "Confirmar: Anular" }).click();
  await expect(specimens.getByRole("row").nth(1)).toContainText("Anulado: Carga mal leída");
  await expect(specimens.getByRole("button", { name: "Anular" })).toHaveCount(1);

  // LAB1-02 (E-LAB1-4, E-LAB1-02-14/15): Calidad › Lotes y veredicto shows the lot with its verdict and its recall; Calidad blocks it
  // by hand with a reason and unblocks it — it returns to the status it had.
  await nav(quality, "Lotes y veredicto");
  await quality.getByLabel("Código del lote").fill(nightLot);
  await quality.getByTestId(`quality-lot:${nightLot}`).getByRole("button", { name: "Ver" }).click();
  await expect(quality.getByTestId("quality-lot")).toContainText(nightLot);
  await expect(quality.getByTestId("quality-lot-evaluation")).toContainText("58.42");
  await expect(quality.getByTestId("quality-lot-recall")).toContainText("Ningún conduce ha tomado este lote.");
  await expectFits(quality);
  for (const [label, reason, status] of [
    ["Bloquear", "Fisuras en la inspección", "Bloqueado"],
    ["Desbloquear", "Inspección repetida", "En curado"],
  ] as const) {
    await quality.getByTestId("lot-quality-actions").getByRole("button", { name: label, exact: true }).click();
    const reasonDialog = quality.getByRole("dialog");
    await reasonDialog.getByLabel(`Motivo: ${label}`).fill(reason);
    await reasonDialog.getByRole("button", { name: `Confirmar: ${label}` }).click();
    await expect(quality.getByTestId("quality-lot-status")).toHaveText(status);
  }
});
