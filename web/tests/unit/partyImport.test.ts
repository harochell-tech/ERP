import { describe, expect, it } from "vitest";
import {
  batchSummary,
  emailsProblem,
  fileProblem,
  importSummary,
  outcomeLabel,
  outcomeStatus,
  parseEmails,
  reasonText,
  rowsToLoad,
  termsText,
  type ImportPreview,
} from "@/lib/partyImport";

// IMP-02 (E-IMP-1…11): the bulk load of suppliers and customers, in words.

const preview = (over: Partial<ImportPreview>): ImportPreview => ({
  fileName: "Clientes.xlsx",
  sha256: "00",
  rows: 6,
  toCreate: 2,
  toLink: 1,
  existing: 1,
  duplicates: 0,
  rejected: 2,
  ignoredColumns: [],
  items: [],
  ...over,
});

describe("what a file would load", () => {
  it("counts new parties and suppliers that also become customers", () => {
    expect(rowsToLoad(preview({}))).toBe(3);
    expect(importSummary(preview({}), "customers", false)).toBe(
      "Se cargarán 2 clientes nuevos y 1 proveedores que pasan a ser también clientes de 6 filas; 1 ya existen, 2 no se pueden cargar.",
    );
    expect(importSummary(preview({ rows: 2, toLink: 0, existing: 0, rejected: 0 }), "suppliers", true)).toBe("Se cargaron 2 proveedores nuevos de 2 filas.");
  });

  it("names each outcome and the reason a row is left out", () => {
    expect(["CREATE", "LINK", "EXISTS", "DUPLICATE", "REJECTED"].map(outcomeLabel)).toEqual([
      "Se crea",
      "Proveedor que pasa a ser también cliente",
      "Ya existe",
      "Repetido en el archivo",
      "No se carga",
    ]);
    expect(["CREATE", "LINK", "EXISTS", "REJECTED"].map(outcomeStatus)).toEqual(["ACTIVE", "ACTIVE", "DRAFT", "REJECTED"]);
    expect(reasonText({ reasonCode: "ID_MISSING", reason: "The row has no fiscal identifier." })).toBe("No tiene RNC ni cédula.");
    expect(reasonText({ reasonCode: "NEW_CODE", reason: "Server text." })).toBe("Server text.");
    expect(reasonText({ reasonCode: null, reason: null })).toBe("");
  });

  it("shows the payment term as the file wrote it", () => {
    expect([0, 30, null, undefined].map(termsText)).toEqual(["Contado", "30 días", "—", "—"]);
  });

  it("refuses a file that is missing, of another kind or over 5 MB", () => {
    expect(fileProblem(null)).toBe("Elija el archivo exportado de ADM Cloud.");
    expect(fileProblem({ name: "clientes.pdf", size: 10 })).toBe("El archivo debe ser .xlsx o .csv.");
    expect(fileProblem({ name: "Clientes.XLSX", size: 5 * 1024 * 1024 + 1 })).toBe("El archivo supera 5 MB.");
    expect(fileProblem({ name: "Proveedores.csv", size: 1024 })).toBe(false);
  });
});

describe("e-mail lists (E-IMP-6)", () => {
  it("reads one per line or separated, once each", () => {
    expect(parseEmails("cxp@uno.test\n compras@uno.test; CXP@uno.test, \n")).toEqual(["cxp@uno.test", "compras@uno.test"]);
    expect(parseEmails("  ")).toEqual([]);
  });

  it("accepts up to ten valid addresses", () => {
    expect(emailsProblem(["cxp@uno.test"])).toBe(false);
    expect(emailsProblem(["cxp@uno.test", "sin-arroba"])).toBe("«sin-arroba» no es un correo válido.");
    expect(emailsProblem(Array.from({ length: 11 }, (_, i) => `c${i}@uno.test`))).toBe("Se guardan hasta 10 correos.");
  });
});

describe("batch results (E-IMP-01-6)", () => {
  const messages = { CUSTOMER_TERMS_REQUIRED: "El cliente necesita términos aprobados.", FOUR_EYES_REQUIRED: "Lo aprueba otra persona." };

  it("says how many were done and why the others were skipped", () => {
    expect(batchSummary(3, [{ outcome: "DONE" }, { outcome: "DONE" }, { outcome: "DONE" }], "clientes activados", messages)).toBe("3 clientes activados.");
    expect(
      batchSummary(
        1,
        [{ outcome: "DONE" }, { outcome: "SKIPPED", code: "CUSTOMER_TERMS_REQUIRED" }, { outcome: "SKIPPED", code: "CUSTOMER_TERMS_REQUIRED" }, { outcome: "SKIPPED", code: "OTHER" }],
        "clientes activados",
        messages,
      ),
    ).toBe("1 de 4 clientes activados; 3 omitidos: El cliente necesita términos aprobados (2); OTHER (1).");
  });
});
