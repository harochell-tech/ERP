import { expect, test } from "@playwright/test";
import { confirmAction, nav, signIn } from "./support";

// PRT-02 (E-PRT-3…10, E-PRT-02-1…7): the Director changes the conduce's format on Configuración › Formatos de impresión — hides the
// lots, renames a column, adds a footer — sees it in the preview, activates it, and goes back to the built-in one; an advanced invoice
// without its fiscal data is not activated. The lab's certificate and rack label preview their examples (LAB1-03). The Contador sees the
// formats without changing them.

test("the Director changes, previews and activates a print format (PRT-02)", async ({ browser }) => {
  const director = await signIn(browser, "Director");
  await nav(director, "Formatos de impresión");
  await expect(director.getByRole("heading", { name: "Formatos de impresión" })).toBeVisible();
  await director.getByRole("tab", { name: /Conduce/ }).click();
  const editor = director.getByTestId("format-editor");
  await editor.getByLabel("Mostrar Lotes").uncheck();
  await editor.getByLabel("Título de producto").fill("Artículo");
  await editor.getByLabel("Pie").fill("Gracias por preferirnos.");
  await editor.getByTestId("format-preview-button").click();
  const preview = director.getByTestId("format-preview-document");
  await expect(preview).toContainText("VISTA PREVIA");
  await expect(preview).toContainText("Artículo");
  await expect(preview).toContainText("Gracias por preferirnos.");
  await expect(preview.getByRole("columnheader", { name: "Lotes" })).toHaveCount(0);
  await editor.getByLabel("Nota del cambio").fill("Sin lotes");
  await editor.getByRole("button", { name: "Guardar borrador" }).click();
  await expect(director.getByRole("tab", { name: "Conduce · borrador" })).toBeVisible();
  await confirmAction(director.getByTestId("format-editor"), "Activar la versión 1");
  await expect(director.getByTestId("format-versions")).toContainText("Activo");
  await expect(director.getByTestId("format-versions")).toContainText("Sin lotes");

  // Back to the built-in format, so the other journeys print as before.
  await director.getByRole("button", { name: "Volver al incluido" }).click();
  await confirmAction(director.getByTestId("format-editor"), "Activar la versión 2");

  // An advanced invoice that leaves out the fiscal data is refused.
  await director.getByRole("tab", { name: "Factura", exact: true }).click();
  await director.getByLabel("Avanzado (plantilla y CSS)").check();
  await director.getByTestId("format-body").fill("<h1>Factura {{ factura.numero }}</h1>");
  await director.getByRole("button", { name: "Guardar borrador" }).click();
  await director.getByTestId("format-editor").getByRole("button", { name: "Activar la versión 1", exact: true }).click();
  await director.getByRole("dialog").getByRole("button", { name: "Confirmar: Activar la versión 1" }).click();
  await expect(director.getByTestId("format-editor")).toContainText("Al formato le falta algo obligatorio");
  await director.getByRole("button", { name: "Volver al incluido" }).click();

  // LAB1-03 (E-LAB1-03-1, 9): the lab's certificate and the 100 × 150 mm rack label have their formats too; their previews show the examples.
  await director.getByRole("tab", { name: /Certificado de laboratorio/ }).click();
  await director.getByTestId("format-preview-button").click();
  await expect(director.getByTestId("format-preview-document")).toContainText("CR-8160924P1-190924");
  await expect(director.getByTestId("format-preview-document")).toContainText("Resistencia calculada sobre área bruta");
  await director.getByRole("tab", { name: /Etiqueta de rack/ }).click();
  await expect(director.getByTestId("format-editor")).toContainText("Etiqueta de 100 × 150 mm");
  await director.getByTestId("format-preview-button").click();
  // The latest lot with racks, else the example: either way one label per rack with its QR.
  await expect(director.getByTestId("format-preview-document").getByTestId("rack-label-qr").first()).toBeVisible();

  const contador = await signIn(browser, "Contador");
  await nav(contador, "Formatos de impresión");
  await expect(contador.getByTestId("format-editor")).toBeVisible();
  await expect(contador.getByRole("button", { name: "Guardar borrador" })).toHaveCount(0);
});
