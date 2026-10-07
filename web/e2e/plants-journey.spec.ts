import { expect, test } from "@playwright/test";
import { confirmAction, expectFits, nav, signIn } from "./support";

// PLT-01 (E-PLT-1…5): the Controller opens a plant from the screen — born with RECEPCION, PATIO, CURADO and TRANSITO —, adds a location,
// renames another and takes one out of use; the curing and transit locations go only with the plant.

test("a plant created from the screen with its locations (E-PLT-1…3)", async ({ browser }) => {
  const code = `PL${String(Date.now()).slice(-6)}`;
  const controller = await signIn(browser, "Controller");
  await nav(controller, "Plantas y ubicaciones");
  const form = controller.getByTestId("plant-form");
  await form.getByLabel("Código de la planta").fill(code);
  await form.getByLabel("Nombre de la planta").fill("Planta de prueba");
  await form.getByRole("button", { name: "Crear planta" }).click();
  const plant = controller.getByTestId(`plant:${code}`);
  for (const location of ["RECEPCION", "PATIO", "CURADO", "TRANSITO"]) {
    await expect(plant.getByTestId(`location:${code}:${location}`)).toContainText("En uso");
  }

  await plant.getByLabel(`Código de la ubicación nueva en ${code}`).fill("BODEGA-2");
  await plant.getByLabel(`Nombre de la ubicación nueva en ${code}`).fill("Bodega de cemento");
  await plant.getByRole("button", { name: "Agregar ubicación" }).click();
  await expect(plant.getByTestId(`location:${code}:BODEGA-2`)).toBeVisible();

  await plant.getByLabel(`Nombre de PATIO en ${code}`).fill("Patio norte");
  await plant.getByTestId(`location:${code}:PATIO`).getByRole("button", { name: "Guardar nombre" }).click();
  await expect(plant.getByLabel(`Nombre de PATIO en ${code}`)).toHaveValue("Patio norte");

  await confirmAction(plant.getByTestId(`location:${code}:BODEGA-2`), "Desactivar BODEGA-2");
  await expect(plant.getByTestId(`location:${code}:BODEGA-2`)).toContainText("Desactivada");
  await expect(plant.getByTestId(`location:${code}:CURADO`).getByRole("button", { name: /Desactivar/ })).toHaveCount(0);
  await expectFits(controller);
});
