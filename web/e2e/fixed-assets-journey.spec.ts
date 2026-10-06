import { expect, test } from "@playwright/test";
import { confirmAction, expectFits, nav, pick, signIn, submit } from "./support";

// AF1-05 (E2E-AF1 screens, E-AF1-05-1…10): a fixed asset through the UI. The dev stack holds an electric forklift bought two months ago
// (USD 9,400.00 at 60.00 = 564,000.00) awaiting service. The Contador prepares the class «Montacargas y equipos» (60 months, 10 %) and the
// Controller approves it from Inicio; the Contador puts the forklift into service two months ago, previews and posts last month's
// depreciation (8,460.00) and prepares its sale for 550,000.00; the Controller sees the loss of 5,540.00 and approves it. The initial load's
// preview shows a row's error.

const FORKLIFT = "Montacargas eléctrico Yale ERP050";

function serviceDay(): string {
  const today = new Intl.DateTimeFormat("en-CA", { timeZone: "America/Santo_Domingo", year: "numeric", month: "2-digit", day: "2-digit" }).format(new Date());
  const first = new Date(Date.UTC(Number(today.slice(0, 4)), Number(today.slice(5, 7)) - 1 - 2, 10));
  return first.toISOString().slice(0, 10);
}

test("class, service, depreciation, sale and the initial load's preview (E2E-AF1)", async ({ browser }) => {
  test.setTimeout(300_000);

  // The Contador: the forklift waits for its class and its service; the class is prepared.
  const contador = await signIn(browser, "Contador");
  await expect(contador.getByTestId("tasks").getByRole("link", { name: /Activos por poner en servicio/ })).toBeVisible();
  await nav(contador, "Activos fijos");
  await expect(contador.getByTestId(`asset:${FORKLIFT}`)).toContainText("En espera de servicio");
  await contador.getByRole("link", { name: "Clases", exact: true }).click();
  await expect(contador.getByTestId("asset-category:MONTACARGAS")).toContainText("Sin clase aprobada");
  const form = contador.getByTestId("asset-class-form");
  await pick(form.getByLabel("Categoría de activo fijo"), "Montacargas");
  await form.getByLabel("Vida útil").fill("60");
  await form.getByLabel("Valor residual").fill("10");
  await pick(form.getByLabel("Cuenta de depreciación acumulada"), "15390");
  await pick(form.getByLabel("Cuenta de gasto de depreciación"), "64100");
  await submit(contador, "Preparar clase");
  await expect(contador.getByTestId("asset-category:MONTACARGAS")).toContainText("60 meses");

  // The Controller approves it from Inicio.
  const controller = await signIn(browser, "Controller");
  await controller
    .getByTestId("tasks")
    .getByRole("link", { name: /Clases de activos por aprobar/ })
    .click();
  await confirmAction(controller.getByTestId("asset-category:MONTACARGAS"), "Aprobar clase");
  await expect(controller.getByTestId("asset-category:MONTACARGAS")).toContainText("60 meses · residual 10.00 %");
  await expectFits(controller);

  // Into service two months ago; the card shows what is left to depreciate.
  await nav(contador, "Activos fijos");
  await contador.getByTestId(`asset:${FORKLIFT}`).getByRole("link").click();
  const service = contador.getByTestId("service-form");
  await service.getByLabel("Fecha de puesta en servicio").fill(serviceDay());
  await service.getByLabel("Responsable").fill("Encargado de almacén");
  await confirmAction(service, "Poner en servicio");
  await expect(contador.getByTestId("asset-status")).toHaveText("En servicio");
  await expect(contador.getByTestId("asset-depreciable")).toHaveText("507,600.00");
  await expectFits(contador);

  // Last month: the preview, then the posting.
  await contador.getByRole("link", { name: "Depreciación del mes", exact: true }).click();
  await expect(contador.getByTestId("depreciation-total")).toHaveText("8,460.00");
  await confirmAction(contador, "Registrar depreciación");
  await expect(contador.getByTestId("depreciation-preview")).toContainText("ya está depreciado");
  await expectFits(contador);

  // The sale, prepared on the card; the Controller sees the loss and approves.
  await contador.getByRole("link", { name: "Activos", exact: true }).click();
  await contador.getByTestId(`asset:${FORKLIFT}`).getByRole("link").click();
  await expect(contador.getByTestId("asset-accumulated")).toHaveText("8,460.00");
  const disposal = contador.getByTestId("disposal-form");
  await disposal.getByLabel("Precio de venta").fill("550000");
  await disposal.getByLabel("Motivo de la baja").fill("Venta a Ferretería del Este");
  await confirmAction(disposal, "Preparar baja");
  await controller.goto("/");
  await controller
    .getByTestId("tasks")
    .getByRole("link", { name: /Bajas de activos por aprobar/ })
    .click();
  const row = controller.getByRole("row").filter({ hasText: FORKLIFT });
  await expect(row).toContainText("555,540.00");
  await expect(row).toContainText("5,540.00");
  await confirmAction(row, "Aprobar baja");
  await controller.getByLabel("Estado").selectOption({ label: "Contabilizada" });
  await expect(controller.getByRole("row").filter({ hasText: FORKLIFT })).toContainText("Contabilizada");
  await contador.reload();
  await expect(contador.getByTestId("asset-status")).toHaveText("Dado de baja");

  // The initial load: a row with an unknown category is refused in the preview.
  await contador.getByRole("link", { name: "Carga inicial", exact: true }).click();
  await contador.getByLabel("Archivo (CSV o Excel)").setInputFiles({
    name: "activos.csv",
    mimeType: "text/csv",
    buffer: Buffer.from(
      "Código,Descripción,Categoría,Planta,Fecha de compra,Costo,Depreciación acumulada\nCAM-001,Camión Volvo,NO_EXISTE,X,2023-06-15,2400000.00,960000.00\n",
    ),
  });
  await expect(contador.getByTestId("asset-load-summary")).toContainText("1 fila(s): 0 correcta(s), 1 con error");
  await expect(contador.getByTestId("load-row:CAM-001")).toContainText("La categoría NO_EXISTE no existe");
  await expectFits(contador);
});
