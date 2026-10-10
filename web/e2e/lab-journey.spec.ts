import { expect, test } from "@playwright/test";
import { nav, signIn } from "./support";

// LAB1-01 (E-LAB1-3, E-LAB1-01-2/3/11/13/14): Calidad loads an item's requirements and a machine's short code on Calidad ›
// Requisitos por ítem, changes a parameter and adds a failure type on Calidad › Parámetros (the history says who changed what). The
// lab technician opens Calidad › Laboratorio and reads the rest without the forms. Recording specimens on a lot is covered over the
// API (QualityLabTests); the whole flow on screen is E2E-L1 (LAB1-04).

test("requirements, short code, parameters and failure types of the lab (LAB1-01)", async ({ browser }) => {
  const manager = await signIn(browser, "Gerente de planta");
  await nav(manager, "Máquinas y turnos");
  await manager.getByLabel("Código de la máquina").fill("BLQ-LAB");
  await manager.getByLabel("Nombre de la máquina").fill("Bloquera del laboratorio");
  await manager.getByRole("button", { name: "Crear máquina" }).click();
  await expect(manager.getByRole("cell", { name: "BLQ-LAB", exact: true })).toBeVisible();

  const quality = await signIn(browser, "Calidad");
  await nav(quality, "Requisitos por ítem");
  await expect(quality.getByRole("heading", { name: "Requisitos por ítem" })).toBeVisible();
  const item = quality.getByTestId("item-spec:BLOQUE-6");
  await expect(item).toContainText("Sin cargar");
  await item.getByRole("button", { name: "Cargar requisitos" }).click();
  const form = quality.getByTestId("item-spec-form:BLOQUE-6");
  await form.getByLabel("Prefijo de lote").fill("6");
  await form.getByLabel("Ancho nominal (cm)").fill("14.5");
  await form.getByLabel("Alto nominal (cm)").fill("19.5");
  await form.getByLabel("Largo nominal (cm)").fill("39.5");
  await form.getByLabel("Área neta (%)").fill("55");
  await form.getByLabel("Mínimo del promedio a 28 d (kg/cm²)").fill("70");
  await form.getByRole("button", { name: "Guardar requisitos" }).click();
  await expect(quality.getByTestId("item-spec:BLOQUE-6")).toContainText("v1");
  await expect(quality.getByTestId("item-spec:BLOQUE-6")).toContainText("14.5 × 19.5 × 39.5");
  await expect(quality.getByTestId("item-spec:BLOQUE-6")).toContainText("55 %");

  const machine = quality.getByTestId("machine-short-code:BLQ-LAB");
  await machine.getByLabel("Código corto de BLQ-LAB").fill("l1");
  await machine.getByRole("button", { name: "Guardar" }).click();
  await expect(quality.getByTestId("machine-short-code:BLQ-LAB").getByLabel("Código corto de BLQ-LAB")).toHaveValue("L1");

  await nav(quality, "Parámetros");
  await expect(quality.getByRole("heading", { name: "Parámetros del laboratorio" })).toBeVisible();
  const cv = quality.getByTestId("lab-parameter:MAX_CV");
  await expect(cv.getByRole("textbox")).toHaveValue("0.15");
  await expect(quality.getByTestId("lab-parameter:AGE_FACTOR_03").getByRole("textbox")).toHaveValue("0.84");
  await cv.getByRole("textbox").fill("0.18");
  await cv.getByRole("button", { name: "Guardar" }).click();
  await expect(quality.getByTestId("lab-parameter-history")).toContainText("0.18");
  await expect(quality.getByTestId("lab-parameter:MAX_CV")).toContainText("Cambiado por");
  await quality.getByLabel("Código del tipo de falla").fill("mixta");
  await quality.getByLabel("Nombre del tipo de falla").fill("Mixta");
  await quality.getByRole("button", { name: "Guardar tipo de falla" }).click();
  await expect(quality.getByTestId("failure-type:MIXTA")).toContainText("Mixta");
  await quality.getByTestId("failure-type:OTRA").getByRole("button", { name: "Desactivar" }).click();
  await expect(quality.getByTestId("failure-type:OTRA").getByRole("button", { name: "Activar" })).toBeVisible();

  const lab = await signIn(browser, "Laboratorio");
  await nav(lab, "Laboratorio");
  await expect(lab.getByRole("heading", { name: "Laboratorio" })).toBeVisible();
  await expect(lab.getByTestId("lab-lots")).toBeVisible();
  await nav(lab, "Parámetros");
  await expect(lab.getByTestId("lab-parameter:MAX_CV")).toContainText("0.18");
  await expect(lab.getByRole("button", { name: "Guardar tipo de falla" })).toHaveCount(0);
  await nav(lab, "Requisitos por ítem");
  await expect(lab.getByTestId("item-spec:BLOQUE-6")).toContainText("v1");
  await expect(lab.getByRole("button", { name: "Cambiar" })).toHaveCount(0);
});

// LAB1-03c (E-LAB1-03-8, 15): the page a certificate's QR opens needs no sign-in; a code that matches nothing says so. A real certificate's
// verification (in force, then void) is covered over the API (QualityCertificateAcceptanceTests).
test("a certificate's public verification page, without sign-in (LAB1-03c)", async ({ page }) => {
  await page.goto("/verificar/certificado/?c=00000000-0000-0000-0000-000000000000&k=aaaaaaaaaaaaaaaaaaaaaaaa");
  await expect(page.getByRole("heading", { name: "Verificación de certificado" })).toBeVisible();
  await expect(page.getByTestId("certificate-verification-missing")).toBeVisible();
});
